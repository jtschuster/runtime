// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Internal.Pgo;

using ILCompiler.Reflection.ReadyToRun;

namespace ILCompiler.Reflection.ReadyToRun.Structural.Parsed;

/// <summary>
/// PGO (Profile-Guided Optimization) instrumentation data for a single method,
/// parsed without requiring the legacy <see cref="ReadyToRunMethod"/> type.
/// </summary>
public sealed class ParsedPgoInfo
{
    /// <summary>Structural method reference identifying the method this PGO data belongs to.</summary>
    public R2RMethodRef MethodRef { get; }

    /// <summary>PGO data format version.</summary>
    public int FormatVersion { get; }

    /// <summary>Parsed PGO schema elements.</summary>
    public IReadOnlyList<PgoSchemaElem> SchemaElements { get; }

    /// <summary>Size of the PGO data blob in bytes.</summary>
    public int Size { get; }

    public ParsedPgoInfo(
        R2RMethodRef methodRef,
        int formatVersion,
        IReadOnlyList<PgoSchemaElem> schemaElements,
        int size)
    {
        MethodRef = methodRef;
        FormatVersion = formatVersion;
        SchemaElements = schemaElements;
        Size = size;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var elem in SchemaElements)
        {
            sb.AppendLine($"ILOffset: {elem.ILOffset} Kind: {elem.InstrumentationKind} Other: {elem.Other} Count: {elem.Count}");
            if (elem.DataHeldInDataLong)
            {
                sb.AppendLine($"  {elem.DataLong}");
            }
            else if (elem.DataObject is not null)
            {
                foreach (object o in elem.DataObject)
                {
                    sb.AppendLine($"  {o}");
                }
            }
        }

        return sb.ToString();
    }

    // ── Static parsing ───────────────────────────────────────────────

    /// <summary>
    /// Parse all PGO entries from the PgoInstrumentationData section.
    /// </summary>
    internal static List<ParsedPgoInfo> ParseAll(
        ReadyToRunReader formatReader,
        ILCompiler.Reflection.ReadyToRun.ReadyToRunReader legacyReader,
        Func<int, StructuralSignatureDecoder> decoderFactory)
    {
        var result = new List<ParsedPgoInfo>();
        var table = formatReader.PgoInstrumentationData;
        if (table is null)
            return result;

        var imageReader = legacyReader.ImageReader;
        var image = legacyReader.Image;

        foreach (var entry in table.Entries)
        {
            try
            {
                // Parse the method signature from the blob
                var decoder = decoderFactory(entry.SignatureBlobOffset);
                R2RMethodRef methodRef = decoder.ParseMethod();

                // Decode PGO version and data offset (same logic as GetPgoOffsetAndVersion)
                int offset = decoder.Offset;
                uint versionAndFlags = 0;
                offset = (int)imageReader.DecodeUnsigned((uint)offset, ref versionAndFlags);

                switch (versionAndFlags & 3)
                {
                    case 3:
                        uint val = 0;
                        imageReader.DecodeUnsigned((uint)offset, ref val);
                        offset -= (int)val;
                        break;
                    case 1:
                        // Offset already correct
                        break;
                    default:
                        continue; // Invalid PGO format
                }

                int version = (int)(versionAndFlags >> 2);

                // Parse the PGO schema data
                var compressedIntParser = new PgoProcessor.PgoEncodedCompressedIntParser(image, offset);
                var loader = new StructuralPgoDataLoader(legacyReader);
                var schemaElements = PgoProcessor.ParsePgoData<string, string>(loader, compressedIntParser, true).ToArray();
                int size = compressedIntParser.Offset - offset;

                result.Add(new ParsedPgoInfo(methodRef, version, schemaElements, size));
            }
            catch
            {
                // Skip entries that fail to parse
            }
        }

        return result;
    }

    /// <summary>
    /// Data loader that resolves type/method handles from import section references.
    /// Uses the legacy reader's import sections for lookup, returning string descriptions.
    /// </summary>
    private sealed class StructuralPgoDataLoader : IPgoSchemaDataLoader<string, string>
    {
        private readonly ILCompiler.Reflection.ReadyToRun.ReadyToRunReader _legacyReader;
        private readonly SignatureFormattingOptions _formatOptions;

        public StructuralPgoDataLoader(ILCompiler.Reflection.ReadyToRun.ReadyToRunReader legacyReader)
        {
            _legacyReader = legacyReader;
            _formatOptions = new SignatureFormattingOptions();
        }

        string IPgoSchemaDataLoader<string, string>.TypeFromLong(long input)
        {
            int tableIndex = checked((int)(input & 0xF));
            int fixupIndex = checked((int)(input >> 4));
            if (tableIndex == 0xF)
            {
                return $"Unknown type {fixupIndex}";
            }

            try
            {
                return _legacyReader.ImportSections[tableIndex].Entries[fixupIndex].Signature.ToString(_formatOptions);
            }
            catch
            {
                return $"Type[{tableIndex}:{fixupIndex}]";
            }
        }

        string IPgoSchemaDataLoader<string, string>.MethodFromLong(long input)
        {
            int tableIndex = checked((int)(input & 0xF));
            int fixupIndex = checked((int)(input >> 4));
            if (tableIndex == 0xF)
            {
                return $"Unknown method {fixupIndex}";
            }

            try
            {
                return _legacyReader.ImportSections[tableIndex].Entries[fixupIndex].Signature.ToString(_formatOptions);
            }
            catch
            {
                return $"Method[{tableIndex}:{fixupIndex}]";
            }
        }
    }
}
