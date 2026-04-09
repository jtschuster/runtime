// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using Internal.ReadyToRunConstants;
using Internal.Runtime;


namespace ILCompiler.Reflection.ReadyToRun.Structural
{
    /// <summary>
    /// A low-level, structural reader for Ready-to-Run images. Parses headers and sections
    /// without cross-referencing metadata — each section table exposes only the raw indices,
    /// RIDs, offsets, and flags encoded in that section.
    /// Also implements <see cref="IR2RImageContext"/> to support signature decoding.
    /// </summary>
    public sealed partial class ReadyToRunReader : IR2RImageContext, IDisposable
    {
        private readonly IBinaryImageReader _compositeReader;
        private readonly NativeReader _imageReader;
        private readonly byte[] _image;
        private readonly string _filename;
        private readonly GCHandle _imagePin;

        // Assembly resolution
        private IAssemblyResolver _assemblyResolver;
        private List<IAssemblyMetadata> _assemblyCache;

        // Lazy-init backing fields
        private ReadyToRunHeader _header;
        private bool _headerInitialized;

        private Machine? _machine;
        private int? _pointerSize;
        private bool? _composite;
        private List<ReadyToRunCoreHeader> _assemblyHeaders;

        // Manifest metadata / reference resolution
        private IAssemblyMetadata _manifestMetadataAssembly;
        private List<AssemblyReferenceHandle> _manifestReferences;
        private MetadataReader _manifestReader;

        // Misc caches
        private IReadOnlyList<Guid> _manifestAssemblyMvids;
        private Dictionary<ReadyToRunSectionType, SectionData> _opaqueSections;

        public ReadyToRunReader(IBinaryImageReader compositeReader, NativeReader imageReader, byte[] image, string filename = null)
        {
            _compositeReader = compositeReader;
            _imageReader = imageReader;
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _filename = filename ?? string.Empty;
            _assemblyCache = new List<IAssemblyMetadata>();
            _imagePin = GCHandle.Alloc(_image, GCHandleType.Pinned);
        }

        public void Dispose()
        {
            if (_imagePin.IsAllocated)
                _imagePin.Free();
        }

        /// <summary>The underlying binary image reader (PE or MachO).</summary>
        public IBinaryImageReader CompositeReader => _compositeReader;

        /// <summary>NativeReader for raw byte access into the image.</summary>
        public NativeReader ImageReader => _imageReader;

        /// <summary>Raw bytes of the PE image (implements <see cref="IR2RImageContext"/>).</summary>
        public byte[] Image => _image;

        /// <summary>Filename of the R2R image being read.</summary>
        public string Filename => _filename;

        /// <summary>
        /// Whether component assembly indices in the manifest start at 2 (V6+).
        /// In older formats they start at 1.
        /// </summary>
        public bool ComponentAssemblyIndicesStartAtTwo
        {
            get
            {
                EnsureHeader();
                return _header.MajorVersion >= 6;
            }
        }

        /// <summary>Offset used for component assembly index adjustment.</summary>
        public int ComponentAssemblyIndexOffset => ComponentAssemblyIndicesStartAtTwo ? 2 : 1;

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

        /// <summary>Get the file offset corresponding to a section RVA.</summary>
        public int GetOffset(SectionRva rva) => _compositeReader.GetOffset((int)rva);

        // ── Section properties ──────────────────────────────────────────

        private bool TryFindSection(IReadOnlyList<ReadyToRunSectionHandle> sections, ReadyToRunSectionType sectionType, out ReadyToRunSectionHandle section)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Type == sectionType)
                {
                    section = sections[i];
                    return true;
                }
            }
            section = default;
            return false;
        }

        /// <summary>All section handles from the global R2R header.</summary>
        public IReadOnlyList<ReadyToRunSectionHandle> Sections
        {
            get
            {
                EnsureHeader();
                return _header.Sections;
            }
        }

        /// <summary>ManifestAssemblyMvids (118): array of assembly MVIDs.</summary>
        public IReadOnlyList<Guid> ManifestAssemblyMvids =>
            _manifestAssemblyMvids ??= ParseManifestAssemblyMvids();

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

            if (TryFindSection(_header.Sections, sectionType, out var section))
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
            foreach (var section in _header.Sections)
            {
                yield return new SectionData(section.Type, section.RelativeVirtualAddress, section.Size, GetOffset(section.RelativeVirtualAddress));
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
            _header = ReadReadyToRunHeader(_imageReader, headerRva, headerOffset);

            if (_composite.Value)
            {
                ParseAssemblyHeaders();
            }

            _headerInitialized = true;
        }

        private void ParseAssemblyHeaders()
        {
            if (!TryFindSection(_header.Sections, ReadyToRunSectionType.ComponentAssemblies, out var componentAssembliesSection))
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
                _assemblyHeaders.Add(ReadReadyToRunCoreHeader(_imageReader, ref asmHeaderOffset));
            }
        }

        // ── Section parsing helpers ─────────────────────────────────────

        /// <summary>
        /// Calculates the runtime function entry size based on the target machine.
        /// Amd64 has 3 ints (start, end, unwind); others have 2 (start, unwind).
        /// </summary>
        internal int CalculateRuntimeFunctionSize()
        {
            return Machine == Machine.Amd64 ? 3 * sizeof(int) : 2 * sizeof(int);
        }

        /// <summary>Gets the compiler identifier string from a CompilerIdentifier section.</summary>
        public string GetCompilerIdentifier(ReadyToRunSectionHandle section)
        {
            if (section.Size <= 1)
                return string.Empty;

            int offset = GetOffset(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _imageReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Gets the owner composite executable filename from an OwnerCompositeExecutable section.</summary>
        public string GetOwnerCompositeExecutable(ReadyToRunSectionHandle section)
        {
            if (section.Size <= 1)
                return string.Empty;

            int offset = GetOffset(section.RelativeVirtualAddress);
            byte[] bytes = new byte[section.Size - 1];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = _imageReader.ReadByte(ref offset);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Gets the manifest metadata location from a ManifestMetadata section.</summary>
        public ManifestMetadataSection GetManifestMetadataSection(ReadyToRunSectionHandle section)
        {
            return new ManifestMetadataSection(GetOffset(section.RelativeVirtualAddress), section.Size);
        }

        private IReadOnlyList<Guid> ParseManifestAssemblyMvids()
        {
            EnsureHeader();
            if (!TryFindSection(_header.Sections, ReadyToRunSectionType.ManifestAssemblyMvids, out var section))
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

        // ── Assembly resolution infrastructure ─────────────────────────────

        /// <summary>
        /// Sets the assembly resolver used by <see cref="OpenReferenceAssembly"/> for
        /// locating reference assemblies on disk.
        /// </summary>
        public void SetAssemblyResolver(IAssemblyResolver resolver)
        {
            _assemblyResolver = resolver;
        }

        /// <summary>
        /// Gets the global (component 0) metadata for non-composite images.
        /// Returns null for composite images.
        /// </summary>
        public IAssemblyMetadata GetGlobalMetadata()
        {
            EnsureHeader();
            EnsureManifestReferences();
            return Composite ? null : (_assemblyCache.Count > 0 ? _assemblyCache[0] : null);
        }

        /// <summary>
        /// Open a reference assembly by module index.
        /// Implements <see cref="IR2RImageContext.OpenReferenceAssembly"/>.
        /// </summary>
        public IAssemblyMetadata OpenReferenceAssembly(int refAsmIndex)
        {
            EnsureHeader();
            EnsureManifestReferences();

            IAssemblyMetadata result = refAsmIndex < _assemblyCache.Count ? _assemblyCache[refAsmIndex] : null;
            if (result is not null)
                return result;

            AssemblyReferenceHandle assemblyReferenceHandle = GetAssemblyAtIndex(refAsmIndex, out MetadataReader metadataReader);

            if (assemblyReferenceHandle.IsNil)
            {
                result = _manifestMetadataAssembly;
            }
            else if (_assemblyResolver is not null)
            {
                result = _assemblyResolver.FindAssembly(metadataReader, assemblyReferenceHandle, _filename);
                if (result is null)
                {
                    string name = metadataReader.GetString(metadataReader.GetAssemblyReference(assemblyReferenceHandle).Name);
                    throw new Exception($"Missing reference assembly: {name}");
                }
            }

            if (result is not null)
            {
                while (_assemblyCache.Count <= refAsmIndex)
                    _assemblyCache.Add(null);
                _assemblyCache[refAsmIndex] = result;
            }

            return result;
        }

        /// <summary>
        /// Gets the assembly name for a given module reference index.
        /// </summary>
        public string GetReferenceAssemblyName(int refAsmIndex)
        {
            EnsureManifestReferences();
            AssemblyReferenceHandle handle = GetAssemblyAtIndex(refAsmIndex, out MetadataReader reader);
            if (reader is null)
                return $"Module#{refAsmIndex}";

            if (handle.IsNil)
                return reader.GetString(reader.GetAssemblyDefinition().Name);

            return reader.GetString(reader.GetAssemblyReference(handle).Name);
        }

        private AssemblyReferenceHandle GetAssemblyAtIndex(int refAsmIndex, out MetadataReader metadataReader)
        {
            Debug.Assert(refAsmIndex != 0 || !Composite);

            int assemblyRefCount = Composite ? 0 : (_assemblyCache.Count > 0 && _assemblyCache[0] is not null
                ? _assemblyCache[0].MetadataReader.GetTableRowCount(TableIndex.AssemblyRef)
                : 0);
            AssemblyReferenceHandle assemblyReferenceHandle = default;
            metadataReader = null;

            if (refAsmIndex <= assemblyRefCount && assemblyRefCount > 0)
            {
                metadataReader = _assemblyCache[0].MetadataReader;
                assemblyReferenceHandle = MetadataTokens.AssemblyReferenceHandle(refAsmIndex);
            }
            else
            {
                int index = refAsmIndex - assemblyRefCount;
                if (ComponentAssemblyIndicesStartAtTwo)
                {
                    if (index == 1)
                    {
                        metadataReader = _manifestReader;
                        assemblyReferenceHandle = default;
                    }
                    index--;
                }
                if (index > 0 && _manifestReferences is not null && index - 1 < _manifestReferences.Count)
                {
                    metadataReader = _manifestReader;
                    assemblyReferenceHandle = _manifestReferences[index - 1];
                }
            }

            return assemblyReferenceHandle;
        }

        private void EnsureManifestReferences()
        {
            if (_manifestReferences is not null)
                return;

            _manifestReferences = new List<AssemblyReferenceHandle>();
            EnsureHeader();

            // Get the manifest metadata section
            if (!TryFindSection(_header.Sections, ReadyToRunSectionType.ManifestMetadata, out var manifestSectionHandle))
                return;

            var manifestSection = GetManifestMetadataSection(manifestSectionHandle);

            // Create the manifest metadata reader
            int manifestOffset = manifestSection.FileOffset;
            int manifestSize = manifestSection.Size;
            if (manifestSize <= 0)
                return;

            unsafe
            {
                fixed (byte* pImage = _image)
                {
                    _manifestMetadataAssembly = _compositeReader.GetManifestAssemblyMetadata(
                        new MetadataReader(pImage + manifestOffset, manifestSize));
                }
            }

            if (_manifestMetadataAssembly is null)
                return;

            _manifestReader = _manifestMetadataAssembly.MetadataReader;

            // For non-composite: seed the assembly cache with the standalone metadata
            if (!Composite && _assemblyCache.Count == 0)
            {
                var standaloneMetadata = _compositeReader.GetStandaloneAssemblyMetadata();
                if (standaloneMetadata is not null)
                    _assemblyCache.Add(standaloneMetadata);
            }

            // Enumerate assembly references from the manifest
            foreach (var handle in _manifestReader.AssemblyReferences)
            {
                _manifestReferences.Add(handle);
            }
        }

        // ── Entry point descriptor decoding ────────────────────────────────

        /// <summary>
        /// Decode the runtime function index and optional fixup offset from a compressed
        /// entry point descriptor at the given image offset. This is the same encoding used
        /// after MethodDef and InstanceMethod signature blobs.
        /// </summary>
        /// <param name="offset">Image offset to start reading from.</param>
        /// <param name="runtimeFunctionIndex">Decoded runtime function index.</param>
        /// <param name="fixupOffset">Fixup list offset, or null if no fixups.</param>
        public void GetRuntimeFunctionIndexFromOffset(int offset, out int runtimeFunctionIndex, out int? fixupOffset)
        {
            fixupOffset = null;

            uint id = 0;
            offset = (int)_imageReader.DecodeUnsigned((uint)offset, ref id);
            if ((id & 1) != 0)
            {
                if ((id & 2) != 0)
                {
                    uint val = 0;
                    _imageReader.DecodeUnsigned((uint)offset, ref val);
                    offset -= (int)val;
                }

                fixupOffset = offset;
                id >>= 2;
            }
            else
            {
                id >>= 1;
            }

            runtimeFunctionIndex = (int)id;
        }
    }
}
