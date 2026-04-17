// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Internal.Runtime;

using StructuralReader = ILCompiler.Reflection.ReadyToRun.ReadyToRunReader;

namespace ILCompiler.Reflection.ReadyToRun.Assertions
{
    /// <summary>
    /// Resolves assembly references for <see cref="R2RAssertions"/>. Probes the
    /// image directory plus any caller-supplied probe directories for referenced
    /// DLLs. Owns the <see cref="PEReader"/> instances it opens.
    /// Modeled on myR2RDump's DiskAssemblyResolver with extra-probe-dir support.
    /// </summary>
    internal sealed class AssertionsAssemblyResolver : IDisposable
    {
        private readonly string[] _moduleNames;
        private readonly Dictionary<int, MetadataReader> _moduleMetadata;
        private readonly List<PEReader> _keepAlive;
        private readonly string _imageDirectory;
        private readonly string[] _extraProbeDirs;
        private readonly int _componentAssemblyIndexOffset;
        private bool _disposed;

        public AssertionsAssemblyResolver(
            StructuralReader reader,
            IPlatformBinaryReader imageReader,
            string mainFilePath,
            string[] extraProbeDirs)
        {
            _imageDirectory = !string.IsNullOrEmpty(mainFilePath)
                ? Path.GetDirectoryName(Path.GetFullPath(mainFilePath))
                : null;
            _extraProbeDirs = extraProbeDirs ?? Array.Empty<string>();
            _keepAlive = new List<PEReader>();
            _moduleMetadata = new Dictionary<int, MetadataReader>();

            _moduleNames = BuildModuleNameTable(reader, imageReader);
            LoadModuleMetadata(imageReader);

            var header = reader.GetHeader();
            bool startAtTwo = header.MajorVersion > 6
                || (header.MajorVersion == 6 && header.MinorVersion >= 3);
            _componentAssemblyIndexOffset = startAtTwo ? 2 : 1;

            if (startAtTwo)
            {
                ReadyToRunSection? manifestHandle = null;
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
                    {
                        // The manifest-self slot sits immediately after the main module's AssemblyRefs.
                        // For composite images there are no main AssemblyRefs so this is slot 1.
                        MetadataReader mainMetadata = imageReader.GetStandaloneAssemblyMetadata();
                        int mainAsmRefCount = reader.Composite || mainMetadata is null
                            ? 0
                            : mainMetadata.GetTableRowCount(TableIndex.AssemblyRef);
                        int manifestSelfSlot = mainAsmRefCount + 1;
                        _moduleMetadata[manifestSelfSlot] = manifestReader;
                    }
                }
            }
        }

        public string[] ModuleNames => _moduleNames;
        public int ModuleCount => _moduleNames.Length;
        public int ComponentAssemblyIndexOffset => _componentAssemblyIndexOffset;

        public MetadataReader GetMetadataReader(int moduleIndex)
        {
            _moduleMetadata.TryGetValue(moduleIndex, out MetadataReader reader);
            return reader;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (PEReader pe in _keepAlive)
            {
                pe.Dispose();
            }
            _keepAlive.Clear();
            _moduleMetadata.Clear();
        }

        private IEnumerable<string> EnumerateProbeDirs()
        {
            if (!string.IsNullOrEmpty(_imageDirectory))
                yield return _imageDirectory;

            foreach (string dir in _extraProbeDirs)
                yield return dir;
        }

        private MetadataReader TryOpen(string dllPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(dllPath);
                var pe = new PEReader(new MemoryStream(bytes));
                if (pe.HasMetadata)
                {
                    _keepAlive.Add(pe);
                    return pe.GetMetadataReader();
                }
                pe.Dispose();
            }
            catch
            {
                // Best effort
            }
            return null;
        }

        private static string[] BuildModuleNameTable(StructuralReader reader, IPlatformBinaryReader imageReader)
        {
            MetadataReader mainMetadata = imageReader.GetStandaloneAssemblyMetadata();
            bool isComposite = reader.Composite;
            int mainAssemblyRefCount = isComposite || mainMetadata is null
                ? 0
                : mainMetadata.GetTableRowCount(TableIndex.AssemblyRef);

            ReadyToRunSection? manifestHandle = null;
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

            var header = reader.GetHeader();
            bool startAtTwo = header.MajorVersion > 6
                || (header.MajorVersion == 6 && header.MinorVersion >= 3);
            int manifestSlotOffset = startAtTwo ? 2 : 1;

            int totalSlots = mainAssemblyRefCount + manifestSlotOffset + manifestAssemblyRefCount;
            var names = new string[totalSlots];

            if (mainMetadata is not null)
                names[0] = mainMetadata.GetString(mainMetadata.GetAssemblyDefinition().Name) + " (self)";
            else
                names[0] = "ManifestMetadata (self)";

            for (int i = 1; i <= mainAssemblyRefCount; i++)
            {
                var handle = MetadataTokens.AssemblyReferenceHandle(i);
                names[i] = mainMetadata.GetString(mainMetadata.GetAssemblyReference(handle).Name);
            }

            if (startAtTwo)
            {
                int manifestSelfSlot = mainAssemblyRefCount + 1;
                if (manifestReader is not null)
                    names[manifestSelfSlot] = manifestReader.GetString(manifestReader.GetAssemblyDefinition().Name) + " (manifest)";
                else
                    names[manifestSelfSlot] = "(manifest)";
            }

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

        private void LoadModuleMetadata(IPlatformBinaryReader imageReader)
        {
            MetadataReader standaloneMetadata = imageReader.GetStandaloneAssemblyMetadata();
            if (standaloneMetadata is not null)
                _moduleMetadata[0] = standaloneMetadata;

            for (int i = 1; i < _moduleNames.Length; i++)
            {
                string name = _moduleNames[i];
                if (name.StartsWith('('))
                    continue;

                int parenIdx = name.IndexOf(" (");
                if (parenIdx >= 0)
                    name = name[..parenIdx];

                foreach (string dir in EnumerateProbeDirs())
                {
                    string dllPath = Path.Combine(dir, name + ".dll");
                    if (!File.Exists(dllPath))
                        continue;

                    MetadataReader md = TryOpen(dllPath);
                    if (md is not null)
                    {
                        _moduleMetadata[i] = md;
                        break;
                    }
                }
            }
        }
    }
}
