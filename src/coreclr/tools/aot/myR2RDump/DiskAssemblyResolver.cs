// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using ILCompiler.Reflection.ReadyToRun.Structural;
using Internal.Runtime;

using StructuralReader = ILCompiler.Reflection.ReadyToRun.Structural.ReadyToRunReader;

/// <summary>
/// Resolves assembly references by probing for DLLs on disk next to the R2R image.
/// Owns the PEReaders it opens and disposes them when disposed.
/// </summary>
internal sealed class DiskAssemblyResolver : IAssemblyResolver
{
    private readonly string[] _moduleNames;
    private readonly Dictionary<int, MetadataReader> _moduleMetadata;
    private readonly List<(byte[] bytes, PEReader pe)> _keepAlive;
    private readonly string _imageDirectory;
    private readonly int _componentAssemblyIndexOffset;
    private bool _disposed;

    public DiskAssemblyResolver(
        StructuralReader reader,
        PEReader mainPeReader,
        string mainFilePath)
    {
        _imageDirectory = Path.GetDirectoryName(Path.GetFullPath(mainFilePath));
        _keepAlive = new List<(byte[] bytes, PEReader pe)>();
        _moduleMetadata = new Dictionary<int, MetadataReader>();

        _moduleNames = BuildModuleNameTable(reader, mainPeReader);
        LoadModuleMetadata(mainPeReader);

        // For composite images with startAtTwo, index 1 = manifest metadata
        var header = reader.GetHeader();
        bool startAtTwo = header.MajorVersion > 6
            || (header.MajorVersion == 6 && header.MinorVersion >= 3);
        _componentAssemblyIndexOffset = startAtTwo ? 2 : 1;
        if (reader.Composite && _componentAssemblyIndexOffset >= 2)
        {
            ReadyToRunSectionHandle? manifestHandle = null;
            foreach (var s in reader.GetSections())
            {
                if (s.Type == ReadyToRunSectionType.ManifestMetadata)
                {
                    manifestHandle = s;
                    break;
                }
            }
            if (manifestHandle is not null)
            {
                MetadataReader manifestReader = reader.GetManifestMetadataReader(manifestHandle.Value);
                if (manifestReader is not null)
                    _moduleMetadata[1] = manifestReader;
            }
        }
    }

    /// <summary>Module names indexed by module index.</summary>
    public string[] ModuleNames => _moduleNames;

    /// <summary>Number of modules (including module 0 = self).</summary>
    public int ModuleCount => _moduleNames.Length;

    /// <summary>
    /// Offset to add to a component assembly index to get the corresponding module index.
    /// For R2R version ≥ 6.3 this is 2 (index 0=self, 1=manifest); for earlier versions it is 1.
    /// </summary>
    public int ComponentAssemblyIndexOffset => _componentAssemblyIndexOffset;

    /// <summary>
    /// Gets the MetadataReader for the given module index, or null if unavailable.
    /// </summary>
    public MetadataReader GetMetadataReader(int moduleIndex)
    {
        _moduleMetadata.TryGetValue(moduleIndex, out MetadataReader reader);
        return reader;
    }

    public AssemblyMetadata FindAssembly(MetadataReader metadataReader, AssemblyReferenceHandle assemblyReferenceHandle, string parentFile)
    {
        string name = metadataReader.GetString(metadataReader.GetAssemblyReference(assemblyReferenceHandle).Name);
        return FindAssembly(name, parentFile);
    }

    public AssemblyMetadata FindAssembly(string simpleName, string parentFile)
    {
        string dir = !string.IsNullOrEmpty(parentFile)
            ? Path.GetDirectoryName(Path.GetFullPath(parentFile))
            : _imageDirectory;

        string dllPath = Path.Combine(dir, simpleName + ".dll");
        if (!File.Exists(dllPath))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(dllPath);
            var pe = new PEReader(new MemoryStream(bytes));
            if (pe.HasMetadata)
            {
                _keepAlive.Add((bytes, pe));
                return new AssemblyMetadata(pe, pe.GetMetadataReader());
            }
            pe.Dispose();
        }
        catch
        {
            // Best effort
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var (_, pe) in _keepAlive)
        {
            pe.Dispose();
        }
        _keepAlive.Clear();
        _moduleMetadata.Clear();
    }

    private static string[] BuildModuleNameTable(StructuralReader reader, PEReader peReader)
    {
        MetadataReader mainMetadata = peReader.GetMetadataReader();
        bool isComposite = reader.Composite;
        int mainAssemblyRefCount = isComposite
            ? 0
            : mainMetadata.GetTableRowCount(TableIndex.AssemblyRef);

        ReadyToRunSectionHandle? manifestHandle = null;
        foreach (var s in reader.GetSections())
        {
            if (s.Type == ReadyToRunSectionType.ManifestMetadata)
            {
                manifestHandle = s;
                break;
            }
        }

        MetadataReader manifestReader = manifestHandle is not null
            ? reader.GetManifestMetadataReader(manifestHandle.Value)
            : null;

        int manifestAssemblyRefCount = manifestReader?.GetTableRowCount(TableIndex.AssemblyRef) ?? 0;

        // Starting with R2R version 6.3, component assembly indices start at 2
        // (index 1 = manifest metadata itself). Earlier versions start at 1.
        var header = reader.GetHeader();
        bool startAtTwo = header.MajorVersion > 6
            || (header.MajorVersion == 6 && header.MinorVersion >= 3);
        int manifestSlotOffset = startAtTwo ? 2 : 1;

        int totalSlots = mainAssemblyRefCount + manifestSlotOffset + manifestAssemblyRefCount;
        var names = new string[totalSlots];

        // Module 0 = self
        names[0] = mainMetadata.GetString(mainMetadata.GetAssemblyDefinition().Name) + " (self)";

        // Modules 1..mainAssemblyRefCount come from the main assembly's AssemblyRef table
        for (int i = 1; i <= mainAssemblyRefCount; i++)
        {
            var handle = MetadataTokens.AssemblyReferenceHandle(i);
            names[i] = mainMetadata.GetString(mainMetadata.GetAssemblyReference(handle).Name);
        }

        // For startAtTwo, index (mainAssemblyRefCount + 1) = manifest metadata itself
        if (startAtTwo)
        {
            int manifestSelfSlot = mainAssemblyRefCount + 1;
            if (manifestReader is not null)
                names[manifestSelfSlot] = manifestReader.GetString(manifestReader.GetAssemblyDefinition().Name) + " (manifest)";
            else
                names[manifestSelfSlot] = "(manifest)";
        }

        // Remaining slots come from the manifest metadata's AssemblyRef table
        if (manifestReader is not null)
        {
            for (int i = 0; i < manifestAssemblyRefCount; i++)
            {
                int slot = mainAssemblyRefCount + manifestSlotOffset + i;
                var handle = MetadataTokens.AssemblyReferenceHandle(i + 1);
                string name = manifestReader.GetString(manifestReader.GetAssemblyReference(handle).Name);
                if (slot < names.Length)
                    names[slot] = name;
            }
        }

        for (int i = 0; i < names.Length; i++)
            names[i] ??= $"(unresolved #{i})";

        return names;
    }

    private void LoadModuleMetadata(PEReader mainPeReader)
    {
        // Module 0: main assembly
        if (mainPeReader.HasMetadata)
            _moduleMetadata[0] = mainPeReader.GetMetadataReader();

        for (int i = 1; i < _moduleNames.Length; i++)
        {
            string name = _moduleNames[i];
            if (name.StartsWith('('))
                continue;

            // Strip " (self)" suffix if present
            int parenIdx = name.IndexOf(" (");
            if (parenIdx >= 0)
                name = name[..parenIdx];

            string dllPath = Path.Combine(_imageDirectory, name + ".dll");
            if (!File.Exists(dllPath))
                continue;

            try
            {
                byte[] bytes = File.ReadAllBytes(dllPath);
                var pe = new PEReader(new MemoryStream(bytes));
                if (pe.HasMetadata)
                {
                    _keepAlive.Add((bytes, pe));
                    _moduleMetadata[i] = pe.GetMetadataReader();
                }
            }
            catch
            {
                // Best effort — skip DLLs we can't open
            }
        }
    }
}
