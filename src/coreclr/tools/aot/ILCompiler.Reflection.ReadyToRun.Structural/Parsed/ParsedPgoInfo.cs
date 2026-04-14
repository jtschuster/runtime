// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ILCompiler.Reflection.ReadyToRun.Structural;
using Internal.Pgo;


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
    /// <param name="formatReader">The structural format reader.</param>
    /// <param name="decoderFactory">Factory for creating signature decoders at given offsets.</param>
    /// <param name="importSignatureLookup">Delegate to look up a parsed import signature by (tableIndex, fixupIndex).</param>
    internal static List<ParsedPgoInfo> ParseAll(
        ReadyToRunReader formatReader,
        Func<int, StructuralSignatureDecoder> decoderFactory,
        Func<int, int, R2RFixupSignature> importSignatureLookup)
    {
        var result = new List<ParsedPgoInfo>();
        var table = formatReader.PgoInstrumentationData;
        if (table is null)
            return result;

        var imageReader = formatReader.ImageReader;
        var image = formatReader.Image;

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
                var loader = new StructuralPgoDataLoader(importSignatureLookup);
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
    /// Uses a delegate to look up parsed fixup signatures by (tableIndex, fixupIndex).
    /// </summary>
    private sealed class StructuralPgoDataLoader : IPgoSchemaDataLoader<string, string>
    {
        private readonly Func<int, int, R2RFixupSignature> _importSignatureLookup;

        public StructuralPgoDataLoader(Func<int, int, R2RFixupSignature> importSignatureLookup)
        {
            _importSignatureLookup = importSignatureLookup;
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
                var sig = _importSignatureLookup(tableIndex, fixupIndex);
                return sig?.ToString() ?? $"Type[{tableIndex}:{fixupIndex}]";
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
                var sig = _importSignatureLookup(tableIndex, fixupIndex);
                return sig?.ToString() ?? $"Method[{tableIndex}:{fixupIndex}]";
            }
            catch
            {
                return $"Method[{tableIndex}:{fixupIndex}]";
            }
        }
    }
}
