// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun.Format
{
    /// <summary>
    /// A low-level, structural reader for Ready-to-Run images. Parses headers and sections
    /// without cross-referencing metadata — each section table exposes only the raw indices,
    /// RIDs, offsets, and flags encoded in that section.
    /// </summary>
    public sealed class ReadyToRunReader
    {
        private readonly IBinaryImageReader _compositeReader;
        private readonly NativeReader _imageReader;

        // Lazy-init backing fields
        private ReadyToRunHeader _header;
        private bool _headerInitialized;

        private Machine? _machine;
        private int? _pointerSize;
        private bool? _composite;
        private List<ReadyToRunCoreHeader> _assemblyHeaders;

        // Section table caches (lazy)
        private string _compilerIdentifier;
        private string _ownerCompositeExecutable;
        private RuntimeFunctionsTable _runtimeFunctions;
        private ImportSectionsTable _importSections;
        private MethodDefEntryPointsTable _methodDefEntryPoints;
        private ExceptionInfoTable _exceptionInfo;
        private DebugInfoTable _debugInfo;
        private AvailableTypesTable _availableTypes;
        private InstanceMethodEntryPointsTable _instanceMethodEntryPoints;
        private InliningInfoTable _inliningInfo;
        private InliningInfo2Table _inliningInfo2;
        private PgoInstrumentationDataTable _pgoInstrumentationData;
        private CrossModuleInlineInfoTable _crossModuleInlineInfo;
        private HotColdMapTable _hotColdMap;
        private MethodIsGenericMapTable _methodIsGenericMap;
        private EnclosingTypeMapTable _enclosingTypeMap;
        private TypeGenericInfoMapTable _typeGenericInfoMap;
        private ComponentAssembliesTable _componentAssemblies;
        private IReadOnlyList<Guid> _manifestAssemblyMvids;
        private ManifestMetadataSection _manifestMetadata;
        private Dictionary<ReadyToRunSectionType, SectionData> _opaqueSections;

        public ReadyToRunReader(IBinaryImageReader compositeReader, NativeReader imageReader)
        {
            _compositeReader = compositeReader;
            _imageReader = imageReader;
        }

        /// <summary>The underlying binary image reader (PE or MachO).</summary>
        public IBinaryImageReader CompositeReader => _compositeReader;

        /// <summary>NativeReader for raw byte access into the image.</summary>
        public NativeReader ImageReader => _imageReader;

        /// <summary>Machine architecture of the image.</summary>
        public Machine Machine
        {
            get
            {
                EnsureHeader();
                return _machine!.Value;
            }
        }

        /// <summary>Pointer size for the target architecture (4 or 8).</summary>
        public int TargetPointerSize
        {
            get
            {
                EnsureHeader();
                return _pointerSize!.Value;
            }
        }

        /// <summary>Whether this is a composite R2R image.</summary>
        public bool Composite
        {
            get
            {
                EnsureHeader();
                return _composite!.Value;
            }
        }

        /// <summary>The parsed R2R header.</summary>
        public ReadyToRunHeader ReadyToRunHeader
        {
            get
            {
                EnsureHeader();
                return _header;
            }
        }

        /// <summary>Per-assembly headers (composite images only). Null for single-file images.</summary>
        public IReadOnlyList<ReadyToRunCoreHeader> ReadyToRunAssemblyHeaders
        {
            get
            {
                EnsureHeader();
                return _assemblyHeaders;
            }
        }

        /// <summary>Get the file offset corresponding to an RVA.</summary>
        public int GetOffset(int rva) => _compositeReader.GetOffset(rva);

        // ── Section properties ──────────────────────────────────────────

        /// <summary>CompilerIdentifier (100): UTF-8 string identifying the compiler.</summary>
        public string CompilerIdentifier =>
            _compilerIdentifier ??= ParseCompilerIdentifier();

        /// <summary>OwnerCompositeExecutable (116): filename of the owning composite image.</summary>
        public string OwnerCompositeExecutable =>
            _ownerCompositeExecutable ??= ParseOwnerCompositeExecutable();

        /// <summary>RuntimeFunctions (102): sorted array of runtime function entries.</summary>
        public RuntimeFunctionsTable RuntimeFunctions =>
            _runtimeFunctions ??= ParseSection(ReadyToRunSectionType.RuntimeFunctions,
                (s) => RuntimeFunctionsTable.Parse(this, s));

        /// <summary>ImportSections (101): array of import section descriptors.</summary>
        public ImportSectionsTable ImportSections =>
            _importSections ??= ParseSection(ReadyToRunSectionType.ImportSections,
                (s) => ImportSectionsTable.Parse(this, s));

        /// <summary>MethodDefEntryPoints (103): sparse array mapping MethodDef RID → entry point.</summary>
        public MethodDefEntryPointsTable MethodDefEntryPoints =>
            _methodDefEntryPoints ??= ParseMethodDefEntryPointsFromHeaders();

        /// <summary>ExceptionInfo (104): array of (methodRva, ehInfoRva) pairs.</summary>
        public ExceptionInfoTable ExceptionInfo =>
            _exceptionInfo ??= ParseSection(ReadyToRunSectionType.ExceptionInfo,
                (s) => ExceptionInfoTable.Parse(this, s));

        /// <summary>DebugInfo (105): sparse array mapping runtime function ID → debug data offset.</summary>
        public DebugInfoTable DebugInfo =>
            _debugInfo ??= ParseSection(ReadyToRunSectionType.DebugInfo,
                (s) => DebugInfoTable.Parse(this, s));

        /// <summary>AvailableTypes (108): hashtable of (rid, isExported) per available type.</summary>
        public AvailableTypesTable AvailableTypes =>
            _availableTypes ??= ParseAvailableTypesFromHeaders();

        /// <summary>InstanceMethodEntryPoints (109): hashtable of generic method entry points.</summary>
        public InstanceMethodEntryPointsTable InstanceMethodEntryPoints =>
            _instanceMethodEntryPoints ??= ParseSection(ReadyToRunSectionType.InstanceMethodEntryPoints,
                (s) => InstanceMethodEntryPointsTable.Parse(this, s));

        /// <summary>InliningInfo (110): v1 inlining info (deprecated).</summary>
        public InliningInfoTable InliningInfo =>
            _inliningInfo ??= ParseSection(ReadyToRunSectionType.InliningInfo,
                (s) => InliningInfoTable.Parse(this, s));

        /// <summary>InliningInfo2 (114): v2 inlining info.</summary>
        public InliningInfo2Table InliningInfo2 =>
            _inliningInfo2 ??= ParseSection(ReadyToRunSectionType.InliningInfo2,
                (s) => InliningInfo2Table.Parse(this, s));

        /// <summary>PgoInstrumentationData (117): hashtable of PGO data entries.</summary>
        public PgoInstrumentationDataTable PgoInstrumentationData =>
            _pgoInstrumentationData ??= ParseSection(ReadyToRunSectionType.PgoInstrumentationData,
                (s) => PgoInstrumentationDataTable.Parse(this, s));

        /// <summary>CrossModuleInlineInfo (119): cross-module inlining data.</summary>
        public CrossModuleInlineInfoTable CrossModuleInlineInfo =>
            _crossModuleInlineInfo ??= ParseSection(ReadyToRunSectionType.CrossModuleInlineInfo,
                (s) => CrossModuleInlineInfoTable.Parse(this, s));

        /// <summary>HotColdMap (120): pairs of hot/cold runtime function indices.</summary>
        public HotColdMapTable HotColdMap =>
            _hotColdMap ??= ParseSection(ReadyToRunSectionType.HotColdMap,
                (s) => HotColdMapTable.Parse(this, s));

        /// <summary>MethodIsGenericMap (121): bitvector of generic method flags.</summary>
        public MethodIsGenericMapTable MethodIsGenericMap =>
            _methodIsGenericMap ??= ParseMethodIsGenericMapFromHeaders();

        /// <summary>EnclosingTypeMap (122): RID array of enclosing types.</summary>
        public EnclosingTypeMapTable EnclosingTypeMap =>
            _enclosingTypeMap ??= ParseEnclosingTypeMapFromHeaders();

        /// <summary>TypeGenericInfoMap (123): packed 4-bit generic info per type.</summary>
        public TypeGenericInfoMapTable TypeGenericInfoMap =>
            _typeGenericInfoMap ??= ParseTypeGenericInfoMapFromHeaders();

        /// <summary>ComponentAssemblies (115): per-assembly header entries in composite images.</summary>
        public ComponentAssembliesTable ComponentAssemblies =>
            _componentAssemblies ??= ParseSection(ReadyToRunSectionType.ComponentAssemblies,
                (s) => ComponentAssembliesTable.Parse(this, s));

        /// <summary>ManifestAssemblyMvids (118): array of assembly MVIDs.</summary>
        public IReadOnlyList<Guid> ManifestAssemblyMvids =>
            _manifestAssemblyMvids ??= ParseManifestAssemblyMvids();

        /// <summary>ManifestMetadata (112): raw ECMA-335 metadata blob location.</summary>
        public ManifestMetadataSection ManifestMetadata =>
            _manifestMetadata ??= ParseSection(ReadyToRunSectionType.ManifestMetadata,
                (s) => new ManifestMetadataSection(GetOffset(s.RelativeVirtualAddress), s.Size));

        /// <summary>
        /// Gets the raw section data for an opaque/undocumented section type.
        /// Returns null if the section is not present.
        /// </summary>
        public SectionData GetOpaqueSection(ReadyToRunSectionType sectionType)
        {
            EnsureHeader();
            _opaqueSections ??= new Dictionary<ReadyToRunSectionType, SectionData>();

            if (_opaqueSections.TryGetValue(sectionType, out var cached))
                return cached;

            if (_header.Sections.TryGetValue(sectionType, out var section))
            {
                var data = new SectionData(sectionType, section.RelativeVirtualAddress, section.Size, GetOffset(section.RelativeVirtualAddress));
                _opaqueSections[sectionType] = data;
                return data;
            }

            return null;
        }

        /// <summary>
        /// Enumerates all sections present in the image as <see cref="SectionData"/>.
        /// This provides a way to account for every section's RVA and size.
        /// </summary>
        public IEnumerable<SectionData> EnumerateAllSections()
        {
            EnsureHeader();
            foreach (var kvp in _header.Sections)
            {
                yield return new SectionData(kvp.Key, kvp.Value.RelativeVirtualAddress, kvp.Value.Size, GetOffset(kvp.Value.RelativeVirtualAddress));
            }
        }

        // ── Header initialization ───────────────────────────────────────

        private void EnsureHeader()
        {
            if (_headerInitialized)
                return;

            _machine = _compositeReader.Machine;
            _pointerSize = _machine switch
            {
                Machine.I386 or Machine.Arm or Machine.Thumb or Machine.ArmThumb2 => 4,
                Machine.Amd64 or Machine.Arm64 or Machine.LoongArch64 or Machine.RiscV64 => 8,
                _ => throw new NotImplementedException(_machine.ToString()),
            };

            if (!_compositeReader.TryGetReadyToRunHeader(out int headerRva, out bool isComposite))
                throw new BadImageFormatException("Not a ReadyToRun image");

            _composite = isComposite;

            int headerOffset = GetOffset(headerRva);
            _header = new ReadyToRunHeader(_imageReader, headerRva, headerOffset);

            if (_composite.Value)
            {
                ParseAssemblyHeaders();
            }

            _headerInitialized = true;
        }

        private void ParseAssemblyHeaders()
        {
            if (!_header.Sections.TryGetValue(ReadyToRunSectionType.ComponentAssemblies, out var componentAssembliesSection))
            {
                _assemblyHeaders = new List<ReadyToRunCoreHeader>();
                return;
            }

            _assemblyHeaders = new List<ReadyToRunCoreHeader>();
            int offset = GetOffset(componentAssembliesSection.RelativeVirtualAddress);
            int count = componentAssembliesSection.Size / ComponentAssembly.Size;

            for (int i = 0; i < count; i++)
            {
                var assembly = new ComponentAssembly(_imageReader, ref offset);
                int asmHeaderOffset = GetOffset(assembly.AssemblyHeaderRVA);
                _assemblyHeaders.Add(new ReadyToRunCoreHeader(_imageReader, ref asmHeaderOffset));
            }
        }

        // ── Section parsing helpers ─────────────────────────────────────

        private T ParseSection<T>(ReadyToRunSectionType sectionType, Func<ReadyToRunSection, T> parser) where T : class
        {
            EnsureHeader();
            if (_header.Sections.TryGetValue(sectionType, out var section))
                return parser(section);
            return null;
        }

        /// <summary>
        /// Calculates the runtime function entry size based on the target machine.
        /// Amd64 has 3 ints (start, end, unwind); others have 2 (start, unwind).
        /// </summary>
        internal int CalculateRuntimeFunctionSize()
        {
            return Machine == Machine.Amd64 ? 3 * sizeof(int) : 2 * sizeof(int);
        }

        // Per-assembly section aggregation for sections that can appear per-assembly in composite images

        private MethodDefEntryPointsTable ParseMethodDefEntryPointsFromHeaders()
        {
            EnsureHeader();
            // Try global header first (non-composite)
            if (_header.Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out var section))
                return MethodDefEntryPointsTable.Parse(this, section);

            // Composite: aggregate from per-assembly headers
            if (_assemblyHeaders is not null)
            {
                var allEntries = new List<MethodDefEntry>();
                foreach (var asmHeader in _assemblyHeaders)
                {
                    if (asmHeader.Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out section))
                    {
                        var table = MethodDefEntryPointsTable.Parse(this, section);
                        allEntries.AddRange(table.Entries);
                    }
                }
                if (allEntries.Count > 0)
                    return MethodDefEntryPointsTable.FromEntries(allEntries);
            }
            return null;
        }

        private AvailableTypesTable ParseAvailableTypesFromHeaders()
        {
            EnsureHeader();
            if (_header.Sections.TryGetValue(ReadyToRunSectionType.AvailableTypes, out var section))
                return AvailableTypesTable.Parse(this, section);

            if (_assemblyHeaders is not null)
            {
                var allEntries = new List<AvailableTypeEntry>();
                for (int i = 0; i < _assemblyHeaders.Count; i++)
                {
                    if (_assemblyHeaders[i].Sections.TryGetValue(ReadyToRunSectionType.AvailableTypes, out section))
                    {
                        var table = AvailableTypesTable.Parse(this, section);
                        allEntries.AddRange(table.Entries);
                    }
                }
                if (allEntries.Count > 0)
                    return AvailableTypesTable.FromEntries(allEntries);
            }
            return null;
        }

        private MethodIsGenericMapTable ParseMethodIsGenericMapFromHeaders()
        {
            return ParsePerAssemblySection(ReadyToRunSectionType.MethodIsGenericMap,
                (s) => MethodIsGenericMapTable.Parse(this, s));
        }

        private EnclosingTypeMapTable ParseEnclosingTypeMapFromHeaders()
        {
            return ParsePerAssemblySection(ReadyToRunSectionType.EnclosingTypeMap,
                (s) => EnclosingTypeMapTable.Parse(this, s));
        }

        private TypeGenericInfoMapTable ParseTypeGenericInfoMapFromHeaders()
        {
            return ParsePerAssemblySection(ReadyToRunSectionType.TypeGenericInfoMap,
                (s) => TypeGenericInfoMapTable.Parse(this, s));
        }

        private T ParsePerAssemblySection<T>(ReadyToRunSectionType sectionType, Func<ReadyToRunSection, T> parser) where T : class
        {
            EnsureHeader();
            if (_header.Sections.TryGetValue(sectionType, out var section))
                return parser(section);

            // In composite images, try the first assembly header that has it
            if (_assemblyHeaders is not null)
            {
                foreach (var asmHeader in _assemblyHeaders)
                {
                    if (asmHeader.Sections.TryGetValue(sectionType, out section))
                        return parser(section);
                }
            }
            return null;
        }

        // ── Simple section parsers ──────────────────────────────────────

        private string ParseCompilerIdentifier()
        {
            EnsureHeader();
            if (!_header.Sections.TryGetValue(ReadyToRunSectionType.CompilerIdentifier, out var section))
                return null;

            int offset = GetOffset(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1]; // exclude null terminator
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _imageReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private string ParseOwnerCompositeExecutable()
        {
            EnsureHeader();
            if (!_header.Sections.TryGetValue(ReadyToRunSectionType.OwnerCompositeExecutable, out var section))
                return null;

            int offset = GetOffset(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1]; // exclude null terminator
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _imageReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private IReadOnlyList<Guid> ParseManifestAssemblyMvids()
        {
            EnsureHeader();
            if (!_header.Sections.TryGetValue(ReadyToRunSectionType.ManifestAssemblyMvids, out var section))
                return null;

            int offset = GetOffset(section.RelativeVirtualAddress);
            int count = section.Size / 16;
            var mvids = new List<Guid>(count);
            byte[] guidBytes = new byte[16];

            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < 16; j++)
                    guidBytes[j] = _imageReader.ReadByte(ref offset);
                mvids.Add(new Guid(guidBytes));
            }

            return mvids;
        }
    }
}
