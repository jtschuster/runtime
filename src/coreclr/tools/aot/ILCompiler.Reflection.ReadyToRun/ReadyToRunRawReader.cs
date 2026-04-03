// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection.PortableExecutable;

using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Represents an entry in the <see cref="ReadyToRunSectionType.HotColdMap"/> section.
    /// Each entry maps a cold runtime function to its corresponding hot runtime function.
    /// </summary>
    public readonly struct HotColdEntry
    {
        /// <summary>
        /// Zero-based index into the RuntimeFunctions table for the cold part of the method.
        /// </summary>
        public int ColdRuntimeFunctionIndex { get; }

        /// <summary>
        /// Zero-based index into the RuntimeFunctions table for the hot part of the method.
        /// </summary>
        public int HotRuntimeFunctionIndex { get; }

        internal HotColdEntry(int coldRuntimeFunctionIndex, int hotRuntimeFunctionIndex)
        {
            ColdRuntimeFunctionIndex = coldRuntimeFunctionIndex;
            HotRuntimeFunctionIndex = hotRuntimeFunctionIndex;
        }
    }

    /// <summary>
    /// Condensed generic information for a single type, as stored in the
    /// <see cref="ReadyToRunSectionType.TypeGenericInfoMap"/> section.
    /// </summary>
    public readonly struct TypeGenericInfo
    {
        private readonly byte _rawValue;

        internal TypeGenericInfo(byte rawValue)
        {
            _rawValue = rawValue;
        }

        /// <summary>
        /// Number of generic parameters on the type. Values above 2 indicate "more than two".
        /// </summary>
        public int GenericParameterCount => _rawValue & 0x3;

        /// <summary>
        /// <see langword="true"/> when one or more generic parameters have constraints.
        /// </summary>
        public bool HasConstraints => (_rawValue & 0x4) != 0;

        /// <summary>
        /// <see langword="true"/> when one or more generic parameters have co- or contra-variance.
        /// </summary>
        public bool HasVariance => (_rawValue & 0x8) != 0;
    }

    /// <summary>
    /// A lightweight ReadyToRun image reader that exposes direct projections of the sections
    /// present in the image without requiring metadata resolvers or assembly references.
    /// </summary>
    public sealed class ReadyToRunRawReader
    {
        private readonly IBinaryImageReader _binaryReader;
        private readonly NativeReader _imageReader;

        // Header state
        private ReadyToRunHeader? _header;
        private List<ReadyToRunCoreHeader>? _assemblyHeaders;

        // Lazily-initialized section projections
        private string? _compilerIdentifier;
        private bool _compilerIdentifierInitialized;

        private string? _ownerCompositeExecutable;
        private bool _ownerCompositeExecutableInitialized;

        private IReadOnlyList<Guid>? _manifestAssemblyMvids;
        private bool _manifestAssemblyMvidsInitialized;

        private IReadOnlyList<HotColdEntry>? _hotColdMap;
        private bool _hotColdMapInitialized;

        private MethodDefEntryPointsTable? _methodDefEntryPoints;
        private bool _methodDefEntryPointsInitialized;

        // Per-assembly section caches (indexed by assembly index)
        private MethodDefEntryPointsTable?[]? _assemblyMethodDefEntryPoints;
        private IReadOnlyList<bool>?[]? _assemblyMethodIsGenericMaps;
        private IReadOnlyList<ushort>?[]? _assemblyEnclosingTypeMaps;
        private IReadOnlyList<TypeGenericInfo>?[]? _assemblyTypeGenericInfoMaps;

        /// <summary>
        /// Name of the R2R image file.
        /// </summary>
        public string Filename { get; }

        /// <summary>
        /// <see langword="true"/> when the image is a composite R2R image containing multiple component assemblies.
        /// </summary>
        public bool IsComposite { get; }

        /// <summary>
        /// The ReadyToRun header.
        /// </summary>
        public ReadyToRunHeader Header
        {
            get
            {
                EnsureHeader();
                return _header!;
            }
        }

        /// <summary>
        /// The per-assembly core headers for composite R2R images.
        /// Empty for single-file R2R images.
        /// </summary>
        public IReadOnlyList<ReadyToRunCoreHeader> AssemblyHeaders
        {
            get
            {
                EnsureHeader();
                return _assemblyHeaders ?? (IReadOnlyList<ReadyToRunCoreHeader>)Array.Empty<ReadyToRunCoreHeader>();
            }
        }

        /// <summary>
        /// The compiler identifier string from the <see cref="ReadyToRunSectionType.CompilerIdentifier"/> section,
        /// or <see langword="null"/> when the section is absent.
        /// </summary>
        public string? CompilerIdentifier
        {
            get
            {
                if (!_compilerIdentifierInitialized)
                {
                    _compilerIdentifierInitialized = true;
                    if (Header.Sections.TryGetValue(ReadyToRunSectionType.CompilerIdentifier, out ReadyToRunSection section))
                    {
                        int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
                        // Section contains a null-terminated UTF-8 string
                        _compilerIdentifier = ReadUtf8NullTerminated(offset, section.Size);
                    }
                }
                return _compilerIdentifier;
            }
        }

        /// <summary>
        /// The owner composite executable filename from the <see cref="ReadyToRunSectionType.OwnerCompositeExecutable"/> section,
        /// or <see langword="null"/> when the section is absent.
        /// This section is present in component MSIL files that belong to a composite R2R image.
        /// </summary>
        public string? OwnerCompositeExecutable
        {
            get
            {
                if (!_ownerCompositeExecutableInitialized)
                {
                    _ownerCompositeExecutableInitialized = true;
                    if (Header.Sections.TryGetValue(ReadyToRunSectionType.OwnerCompositeExecutable, out ReadyToRunSection section))
                    {
                        int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
                        // Section contains a null-terminated UTF-8 string
                        _ownerCompositeExecutable = ReadUtf8NullTerminated(offset, section.Size);
                    }
                }
                return _ownerCompositeExecutable;
            }
        }

        /// <summary>
        /// The array of assembly MVIDs from the <see cref="ReadyToRunSectionType.ManifestAssemblyMvids"/> section,
        /// or <see langword="null"/> when the section is absent.
        /// Contains one MVID per assembly listed in the manifest metadata.
        /// </summary>
        public IReadOnlyList<Guid>? ManifestAssemblyMvids
        {
            get
            {
                if (!_manifestAssemblyMvidsInitialized)
                {
                    _manifestAssemblyMvidsInitialized = true;
                    if (Header.Sections.TryGetValue(ReadyToRunSectionType.ManifestAssemblyMvids, out ReadyToRunSection section))
                    {
                        _manifestAssemblyMvids = ParseManifestAssemblyMvids(section);
                    }
                }
                return _manifestAssemblyMvids;
            }
        }

        /// <summary>
        /// The array of hot/cold method pairs from the <see cref="ReadyToRunSectionType.HotColdMap"/> section,
        /// or <see langword="null"/> when the section is absent (no methods were split into hot and cold parts).
        /// </summary>
        public IReadOnlyList<HotColdEntry>? HotColdMap
        {
            get
            {
                if (!_hotColdMapInitialized)
                {
                    _hotColdMapInitialized = true;
                    if (Header.Sections.TryGetValue(ReadyToRunSectionType.HotColdMap, out ReadyToRunSection section))
                    {
                        _hotColdMap = ParseHotColdMap(section);
                    }
                }
                return _hotColdMap;
            }
        }

        /// <summary>
        /// The method entry points table from the <see cref="ReadyToRunSectionType.MethodDefEntryPoints"/> section
        /// of the main R2R header, or <see langword="null"/> when the section is absent.
        /// For single-file R2R images, this contains all compiled method entry points.
        /// For composite R2R images, this is typically absent; use
        /// <see cref="GetAssemblyMethodDefEntryPoints"/> to access per-assembly tables.
        /// </summary>
        public MethodDefEntryPointsTable? MethodDefEntryPoints
        {
            get
            {
                if (!_methodDefEntryPointsInitialized)
                {
                    _methodDefEntryPointsInitialized = true;
                    if (Header.Sections.TryGetValue(ReadyToRunSectionType.MethodDefEntryPoints, out ReadyToRunSection section))
                    {
                        int sectionOffset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
                        _methodDefEntryPoints = MethodDefEntryPointsTable.Parse(_imageReader, sectionOffset);
                    }
                }
                return _methodDefEntryPoints;
            }
        }

        /// <summary>
        /// Initializes a new <see cref="ReadyToRunRawReader"/> from a file path.
        /// </summary>
        public ReadyToRunRawReader(string filename)
        {
            Filename = filename;
            byte[] image = File.ReadAllBytes(filename);
            _imageReader = new NativeReader(new MemoryStream(image));

            byte[] imageCopy = image;
            if (MachO.MachObjectFile.IsMachOImage(filename))
            {
                _binaryReader = new MachO.MachOImageReader(image);
            }
            else
            {
                _binaryReader = new PEImageReader(new PEReader(Unsafe.As<byte[], ImmutableArray<byte>>(ref imageCopy)));
            }

            if (!_binaryReader.TryGetReadyToRunHeader(out _, out bool isComposite))
            {
                throw new BadImageFormatException("The file is not a ReadyToRun image");
            }
            IsComposite = isComposite;
        }

        /// <summary>
        /// Initializes a new <see cref="ReadyToRunRawReader"/> from an already-opened PE image.
        /// </summary>
        public ReadyToRunRawReader(PEReader peReader, string filename)
        {
            Filename = filename;
            _binaryReader = new PEImageReader(peReader);

            ImmutableArray<byte> content = _binaryReader.GetEntireImage();
            byte[] image = Unsafe.As<ImmutableArray<byte>, byte[]>(ref content);
            _imageReader = new NativeReader(new MemoryStream(image));

            if (!_binaryReader.TryGetReadyToRunHeader(out _, out bool isComposite))
            {
                throw new BadImageFormatException("The file is not a ReadyToRun image");
            }
            IsComposite = isComposite;
        }

        /// <summary>
        /// Initializes a new <see cref="ReadyToRunRawReader"/> from an in-memory image.
        /// </summary>
        public ReadyToRunRawReader(ReadOnlyMemory<byte> content, string filename)
        {
            Filename = filename;

            byte[] image;
            if (MemoryMarshal.TryGetArray(content, out ArraySegment<byte> segment)
                && segment.Offset == 0
                && segment.Count == content.Length)
            {
                image = segment.Array!;
            }
            else
            {
                image = content.ToArray();
            }

            _imageReader = new NativeReader(new MemoryStream(image));
            byte[] imageCopy = image;
            _binaryReader = new PEImageReader(new PEReader(Unsafe.As<byte[], ImmutableArray<byte>>(ref imageCopy)));

            if (!_binaryReader.TryGetReadyToRunHeader(out _, out bool isComposite))
            {
                throw new BadImageFormatException("The file is not a ReadyToRun image");
            }
            IsComposite = isComposite;
        }

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.MethodDefEntryPoints"/> table for the specified
        /// component assembly (zero-based index), or <see langword="null"/> when the section is absent
        /// for that assembly.
        /// </summary>
        /// <remarks>
        /// For composite R2R images, each component assembly has its own MethodDefEntryPoints section.
        /// For single-file R2R images the <see cref="MethodDefEntryPoints"/> property is preferred.
        /// </remarks>
        public MethodDefEntryPointsTable? GetAssemblyMethodDefEntryPoints(int assemblyIndex)
        {
            EnsureAssemblyArrays();
            if (assemblyIndex < 0 || assemblyIndex >= _assemblyMethodDefEntryPoints!.Length)
                throw new ArgumentOutOfRangeException(nameof(assemblyIndex));

            ref MethodDefEntryPointsTable? cached = ref _assemblyMethodDefEntryPoints[assemblyIndex];
            if (cached is null && AssemblyHeaders[assemblyIndex].Sections.TryGetValue(
                    ReadyToRunSectionType.MethodDefEntryPoints, out ReadyToRunSection section))
            {
                int sectionOffset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
                cached = MethodDefEntryPointsTable.Parse(_imageReader, sectionOffset);
            }
            return cached;
        }

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.MethodIsGenericMap"/> bit vector for the specified
        /// component assembly (zero-based index), or <see langword="null"/> when the section is absent.
        /// The list is indexed by MethodDef RID (0-based), and each entry is <see langword="true"/> when
        /// the corresponding method has generic parameters.
        /// </summary>
        public IReadOnlyList<bool>? GetAssemblyMethodIsGenericMap(int assemblyIndex)
        {
            EnsureAssemblyArrays();
            if (assemblyIndex < 0 || assemblyIndex >= _assemblyMethodIsGenericMaps!.Length)
                throw new ArgumentOutOfRangeException(nameof(assemblyIndex));

            ref IReadOnlyList<bool>? cached = ref _assemblyMethodIsGenericMaps[assemblyIndex];
            if (cached is null && AssemblyHeaders[assemblyIndex].Sections.TryGetValue(
                    ReadyToRunSectionType.MethodIsGenericMap, out ReadyToRunSection section))
            {
                cached = ParseMethodIsGenericMap(section);
            }
            return cached;
        }

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.EnclosingTypeMap"/> for the specified
        /// component assembly (zero-based index), or <see langword="null"/> when the section is absent.
        /// The list is indexed by TypeDef RID (0-based). Each entry is the enclosing type's RID, or 0
        /// when the type is not nested.
        /// </summary>
        public IReadOnlyList<ushort>? GetAssemblyEnclosingTypeMap(int assemblyIndex)
        {
            EnsureAssemblyArrays();
            if (assemblyIndex < 0 || assemblyIndex >= _assemblyEnclosingTypeMaps!.Length)
                throw new ArgumentOutOfRangeException(nameof(assemblyIndex));

            ref IReadOnlyList<ushort>? cached = ref _assemblyEnclosingTypeMaps[assemblyIndex];
            if (cached is null && AssemblyHeaders[assemblyIndex].Sections.TryGetValue(
                    ReadyToRunSectionType.EnclosingTypeMap, out ReadyToRunSection section))
            {
                cached = ParseEnclosingTypeMap(section);
            }
            return cached;
        }

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.TypeGenericInfoMap"/> for the specified
        /// component assembly (zero-based index), or <see langword="null"/> when the section is absent.
        /// The list is indexed by TypeDef RID (0-based).
        /// </summary>
        public IReadOnlyList<TypeGenericInfo>? GetAssemblyTypeGenericInfoMap(int assemblyIndex)
        {
            EnsureAssemblyArrays();
            if (assemblyIndex < 0 || assemblyIndex >= _assemblyTypeGenericInfoMaps!.Length)
                throw new ArgumentOutOfRangeException(nameof(assemblyIndex));

            ref IReadOnlyList<TypeGenericInfo>? cached = ref _assemblyTypeGenericInfoMaps[assemblyIndex];
            if (cached is null && AssemblyHeaders[assemblyIndex].Sections.TryGetValue(
                    ReadyToRunSectionType.TypeGenericInfoMap, out ReadyToRunSection section))
            {
                cached = ParseTypeGenericInfoMap(section);
            }
            return cached;
        }

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.MethodIsGenericMap"/> bit vector from the main
        /// header, or <see langword="null"/> when the section is absent.
        /// </summary>
        public IReadOnlyList<bool>? MethodIsGenericMap => GetSectionFromMainHeader<IReadOnlyList<bool>>(
            ReadyToRunSectionType.MethodIsGenericMap, ParseMethodIsGenericMap);

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.EnclosingTypeMap"/> from the main header,
        /// or <see langword="null"/> when the section is absent.
        /// </summary>
        public IReadOnlyList<ushort>? EnclosingTypeMap => GetSectionFromMainHeader<IReadOnlyList<ushort>>(
            ReadyToRunSectionType.EnclosingTypeMap, ParseEnclosingTypeMap);

        /// <summary>
        /// Returns the <see cref="ReadyToRunSectionType.TypeGenericInfoMap"/> from the main header,
        /// or <see langword="null"/> when the section is absent.
        /// </summary>
        public IReadOnlyList<TypeGenericInfo>? TypeGenericInfoMap => GetSectionFromMainHeader<IReadOnlyList<TypeGenericInfo>>(
            ReadyToRunSectionType.TypeGenericInfoMap, ParseTypeGenericInfoMap);

        private void EnsureHeader()
        {
            if (_header is not null)
                return;

            if (!_binaryReader.TryGetReadyToRunHeader(out int rva, out _))
                throw new BadImageFormatException("The file is not a ReadyToRun image");

            int offset = _binaryReader.GetOffset(rva);
            _header = new ReadyToRunHeader(_imageReader, rva, offset);

            if (IsComposite)
            {
                ParseComponentAssemblies();
            }
        }

        private void ParseComponentAssemblies()
        {
            if (!_header!.Sections.TryGetValue(ReadyToRunSectionType.ComponentAssemblies, out ReadyToRunSection section))
                return;

            _assemblyHeaders = new List<ReadyToRunCoreHeader>();

            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
            int count = section.Size / ComponentAssembly.Size;

            for (int i = 0; i < count; i++)
            {
                ComponentAssembly assembly = new ComponentAssembly(_imageReader, ref offset);
                int headerOffset = _binaryReader.GetOffset(assembly.AssemblyHeaderRVA);
                _assemblyHeaders.Add(new ReadyToRunCoreHeader(_imageReader, ref headerOffset));
            }
        }

        private void EnsureAssemblyArrays()
        {
            EnsureHeader();
            if (_assemblyMethodDefEntryPoints is not null)
                return;

            int count = _assemblyHeaders?.Count ?? 0;
            _assemblyMethodDefEntryPoints = new MethodDefEntryPointsTable?[count];
            _assemblyMethodIsGenericMaps = new IReadOnlyList<bool>?[count];
            _assemblyEnclosingTypeMaps = new IReadOnlyList<ushort>?[count];
            _assemblyTypeGenericInfoMaps = new IReadOnlyList<TypeGenericInfo>?[count];
        }

        private string ReadUtf8NullTerminated(int offset, int maxLength)
        {
            // The section contains a null-terminated string; trim the null terminator
            int length = maxLength > 0 ? maxLength - 1 : 0;
            byte[] buffer = new byte[length];
            _imageReader.ReadSpanAt(ref offset, buffer);
            return Encoding.UTF8.GetString(buffer);
        }

        private IReadOnlyList<Guid> ParseManifestAssemblyMvids(ReadyToRunSection section)
        {
            int count = section.Size / ReadyToRunReader.GuidByteSize;
            var result = new Guid[count];
            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);

            for (int i = 0; i < count; i++)
            {
                byte[] guidBytes = new byte[ReadyToRunReader.GuidByteSize];
                _imageReader.ReadSpanAt(ref offset, guidBytes);
                result[i] = new Guid(guidBytes);
            }

            return result;
        }

        private IReadOnlyList<HotColdEntry> ParseHotColdMap(ReadyToRunSection section)
        {
            // Each entry is two 32-bit integers: cold runtime function index, hot runtime function index
            int count = section.Size / 8;
            var result = new HotColdEntry[count];
            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);

            for (int i = 0; i < count; i++)
            {
                int coldIndex = _imageReader.ReadInt32(ref offset);
                int hotIndex = _imageReader.ReadInt32(ref offset);
                result[i] = new HotColdEntry(coldIndex, hotIndex);
            }

            return result;
        }

        private IReadOnlyList<bool> ParseMethodIsGenericMap(ReadyToRunSection section)
        {
            // Format: uint32 count, then bit vector bytes (LSB = lowest MethodDef RID)
            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
            int bitCount = _imageReader.ReadInt32(ref offset);
            var result = new bool[bitCount];

            for (int i = 0; i < bitCount; )
            {
                byte b = _imageReader.ReadByte(ref offset);
                int remaining = Math.Min(8, bitCount - i);
                for (int bit = 0; bit < remaining; bit++)
                {
                    result[i + bit] = ((b >> bit) & 1) != 0;
                }
                i += remaining;
            }

            return result;
        }

        private IReadOnlyList<ushort> ParseEnclosingTypeMap(ReadyToRunSection section)
        {
            // Format: uint16 count, then uint16[] enclosing type RIDs (0 = not nested)
            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
            int count = _imageReader.ReadUInt16(ref offset);
            var result = new ushort[count];

            for (int i = 0; i < count; i++)
            {
                result[i] = _imageReader.ReadUInt16(ref offset);
            }

            return result;
        }

        private IReadOnlyList<TypeGenericInfo> ParseTypeGenericInfoMap(ReadyToRunSection section)
        {
            // Format: uint32 count, then 4-bit entries packed 2 per byte (MSN = lower RID)
            int offset = _binaryReader.GetOffset(section.RelativeVirtualAddress);
            int count = _imageReader.ReadInt32(ref offset);
            var result = new TypeGenericInfo[count];

            for (int i = 0; i < count; i += 2)
            {
                byte packed = _imageReader.ReadByte(ref offset);
                // The most significant nibble holds the lower RID entry
                result[i] = new TypeGenericInfo((byte)((packed >> 4) & 0xF));
                if (i + 1 < count)
                {
                    result[i + 1] = new TypeGenericInfo((byte)(packed & 0xF));
                }
            }

            return result;
        }

        // Cache for main-header per-assembly sections (single-file images)
        // Key present with null value means "section was not found"; key absent means "not yet checked".
        private readonly Dictionary<ReadyToRunSectionType, object?> _mainHeaderSectionCache = new();

        private T? GetSectionFromMainHeader<T>(ReadyToRunSectionType type, Func<ReadyToRunSection, T> parser)
            where T : class
        {
            if (_mainHeaderSectionCache.TryGetValue(type, out object? cached))
                return (T?)cached;

            T? result = null;
            if (Header.Sections.TryGetValue(type, out ReadyToRunSection section))
                result = parser(section);

            _mainHeaderSectionCache[type] = result;
            return result;
        }
    }
}
