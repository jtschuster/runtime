// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;

using Internal.ReadyToRunConstants;
using Internal.Runtime;

namespace ILCompiler.Reflection.ReadyToRun
{
    /// <summary>
    /// Parser for the ExternalTypeMaps section (ReadyToRunSectionType 124).
    /// The section is a NativeHashtable keyed by the group type's version-resilient hash.
    /// Each entry contains a group type reference, a validity flag, and a nested NativeHashtable
    /// of string keys mapped to target type references.
    /// </summary>
    public sealed class ExternalTypeMapSection
    {
        public sealed class GroupEntry
        {
            internal GroupEntry(string groupTypeName, bool isValid, List<string> rawKeys, Dictionary<string, string> entries)
            {
                GroupTypeName = groupTypeName;
                IsValid = isValid;
                RawKeys = rawKeys;
                Entries = entries;
            }

            public string GroupTypeName { get; }

            public bool IsValid { get; }

            public IReadOnlyList<string> RawKeys { get; }

            public IReadOnlyDictionary<string, string> Entries { get; }
        }

        private readonly Dictionary<string, GroupEntry> _groups;

        private ExternalTypeMapSection(Dictionary<string, GroupEntry> groups) => _groups = groups;

        public IReadOnlyCollection<GroupEntry> Groups => _groups.Values;

        public static bool TryGet(ReadyToRunReader reader, out ExternalTypeMapSection section)
        {
            if (!reader.ReadyToRunHeader.Sections.TryGetValue(ReadyToRunSectionType.ExternalTypeMaps, out ReadyToRunSection r2rSection))
            {
                section = null;
                return false;
            }

            int offset = reader.GetOffset(r2rSection.RelativeVirtualAddress);
            int endOffset = offset + r2rSection.Size;
            section = new ExternalTypeMapSection(ParseGroups(reader, offset, endOffset));
            return true;
        }

        public bool TryGetGroup(string groupTypeName, out GroupEntry group) => _groups.TryGetValue(groupTypeName, out group!);

        private static Dictionary<string, GroupEntry> ParseGroups(ReadyToRunReader reader, int offset, int endOffset)
        {
            var parser = new NativeParser(reader.ImageReader, (uint)offset);
            var hashtable = new NativeHashtable(reader.ImageReader, parser, (uint)endOffset);
            var groups = new Dictionary<string, GroupEntry>(StringComparer.Ordinal);

            NativeHashtable.AllEntriesEnumerator enumerator = hashtable.EnumerateAllEntries();
            for (NativeParser entryParser = enumerator.GetNext(); !entryParser.IsNull(); entryParser = enumerator.GetNext())
            {
                string groupTypeName = ReadTypeReference(reader, ref entryParser);
                bool isValid = entryParser.GetUnsigned() != 0;

                var rawKeys = new List<string>();
                var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                if (isValid)
                {
                    var groupHashtable = new NativeHashtable(reader.ImageReader, entryParser, (uint)endOffset);
                    NativeHashtable.AllEntriesEnumerator groupEnumerator = groupHashtable.EnumerateAllEntries();
                    for (NativeParser groupEntryParser = groupEnumerator.GetNext(); !groupEntryParser.IsNull(); groupEntryParser = groupEnumerator.GetNext())
                    {
                        string key = ReadString(reader, ref groupEntryParser);
                        string targetTypeName = ReadTypeReference(reader, ref groupEntryParser);
                        rawKeys.Add(key);
                        entries[key] = targetTypeName;
                    }
                }

                groups[groupTypeName] = new GroupEntry(groupTypeName, isValid, rawKeys, entries);
            }

            return groups;
        }

        private static string ReadString(ReadyToRunReader reader, ref NativeParser parser)
        {
            uint byteCount = parser.GetUnsigned();
            string value = Encoding.UTF8.GetString(reader.Image, checked((int)parser.Offset), checked((int)byteCount));
            parser.Offset += byteCount;
            return value;
        }

        private static string ReadTypeReference(ReadyToRunReader reader, ref NativeParser parser)
        {
            uint importSectionIndex = parser.GetUnsigned();
            uint fixupIndex = parser.GetUnsigned();
            return ResolveTypeReference(reader, importSectionIndex, fixupIndex);
        }

        private static string ResolveTypeReference(ReadyToRunReader reader, uint importSectionIndex, uint fixupIndex)
        {
            if (importSectionIndex >= reader.ImportSections.Count)
            {
                throw new BadImageFormatException($"ExternalTypeMaps import section index {importSectionIndex} is out of range.");
            }

            ReadyToRunImportSection importSection = reader.ImportSections[checked((int)importSectionIndex)];
            if (fixupIndex >= importSection.Entries.Count)
            {
                throw new BadImageFormatException($"ExternalTypeMaps fixup index {fixupIndex} is out of range for import section {importSectionIndex}.");
            }

            string signature = importSection.Entries[checked((int)fixupIndex)].Signature.ToString(new SignatureFormattingOptions());
            const string TypeHandleSuffix = " (TYPE_HANDLE)";
            return signature.EndsWith(TypeHandleSuffix, StringComparison.Ordinal)
                ? signature[..^TypeHandleSuffix.Length]
                : signature;
        }
    }
}
