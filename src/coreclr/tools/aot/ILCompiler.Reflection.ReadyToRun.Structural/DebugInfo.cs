// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.PortableExecutable;


namespace ILCompiler.Reflection.ReadyToRun;

/// <summary>
/// Debug information for a single runtime function, parsed without depending on the
/// legacy <see cref="RuntimeFunction"/> or <see cref="ReadyToRunMethod"/> types.
/// Contains sequence-point bounds and native variable locations.
/// </summary>
public sealed class DebugInfo
{
    /// <summary>Sequence-point bounds mapping native offsets to IL offsets.</summary>
    public IReadOnlyList<DebugInfoBoundsEntry> Bounds { get; }

    /// <summary>Native variable location information.</summary>
    public IReadOnlyList<NativeVarInfo> Variables { get; }

    /// <summary>Machine architecture used to interpret register numbers.</summary>
    public Machine Machine { get; }

    public DebugInfo(
        IReadOnlyList<DebugInfoBoundsEntry> bounds,
        IReadOnlyList<NativeVarInfo> variables,
        Machine machine)
    {
        Bounds = bounds;
        Variables = variables;
        Machine = machine;
    }

}

public partial class ReadyToRunReader
{
    private Dictionary<DebugInfoHandle, DebugInfo> _debugInfoCache = new Dictionary<DebugInfoHandle, DebugInfo>();

    /// <summary>
    /// Parse debug information from an R2R image at the given file offset.
    /// This is a standalone parser that does not depend on <see cref="RuntimeFunction"/>.
    /// </summary>
    /// <param name="imageReader">The NativeReader for the PE image.</param>
    /// <param name="machine">Target architecture.</param>
    /// <param name="majorVersion">R2R header major version (affects encoding format).</param>
    /// <param name="offset">File offset pointing into the debug info NativeArray.</param>
    /// <returns>Parsed debug info, or null if parsing fails.</returns>
    public DebugInfo GetDebugInfo(DebugInfoHandle offset)
    {
        if (_debugInfoCache.TryGetValue(offset, out DebugInfo cached))
        {
            return cached;
        }

        NativeReader imageReader = ImageReader;
        Machine machine = Machine;
        int majorVersion = _header.MajorVersion;
        try
        {
            // Resolve the NativeArray indirection (lookback encoding)
            uint lookback = 0;
            uint debugInfoOffset = imageReader.DecodeUnsigned((uint)offset, ref lookback);

            if (lookback != 0)
            {
                Debug.Assert(0 < lookback && lookback < (int)offset);
                debugInfoOffset = (uint)offset - lookback;
            }

            NibbleReader reader = new NibbleReader(imageReader, (int)debugInfoOffset);

            uint boundsByteCountOrIndicator = reader.ReadUInt();

            uint boundsByteCount;
            uint variablesByteCount;

            const int DebugInfoFat = 0;
            if (majorVersion >= 17 && boundsByteCountOrIndicator == DebugInfoFat)
            {
                boundsByteCount = reader.ReadUInt();
                variablesByteCount = reader.ReadUInt();
                reader.ReadUInt(); // uninstrumented bounds
                reader.ReadUInt(); // patchpoint info
                reader.ReadUInt(); // rich debug info
                reader.ReadUInt(); // async info
            }
            else
            {
                boundsByteCount = boundsByteCountOrIndicator;
                variablesByteCount = reader.ReadUInt();
            }

            int boundsOffset = reader.GetNextByteOffset();
            int variablesOffset = (int)(boundsOffset + boundsByteCount);

            var bounds = new List<DebugInfoBoundsEntry>();
            if (boundsByteCount > 0)
            {
                ParseBounds(imageReader, boundsOffset, majorVersion, bounds);
            }

            var variables = new List<NativeVarInfo>();
            if (variablesByteCount > 0)
            {
                ParseNativeVarInfo(imageReader, variablesOffset, machine, variables);
            }

            return new DebugInfo(bounds, variables, machine);
        }
        catch
        {
            return null;
        }

        static void ParseBounds(
            NativeReader imageReader,
            int offset,
            int majorVersion,
            List<DebugInfoBoundsEntry> bounds)
        {
            if (majorVersion >= 16)
            {
                NibbleReader reader = new NibbleReader(imageReader, offset);
                uint boundsEntryCount = reader.ReadUInt();
                Debug.Assert(boundsEntryCount > 0);
                uint bitsForNativeDelta = reader.ReadUInt() + 1;
                uint bitsForILOffsets = reader.ReadUInt() + 1;

                uint bitsForSourceType = majorVersion >= 17 ? 3u : 2u;
                uint bitsPerEntry = bitsForNativeDelta + bitsForILOffsets + bitsForSourceType;
                ulong bitsMeaningfulMask = (1UL << ((int)bitsPerEntry)) - 1;
                int offsetOfActualBoundsData = reader.GetNextByteOffset();

                uint bitsCollected = 0;
                ulong bitTemp = 0;
                uint curBoundsProcessed = 0;

                uint previousNativeOffset = 0;

                while (curBoundsProcessed < boundsEntryCount)
                {
                    bitTemp |= ((uint)imageReader[offsetOfActualBoundsData++]) << (int)bitsCollected;
                    bitsCollected += 8;
                    while (bitsCollected >= bitsPerEntry)
                    {
                        ulong mappingDataEncoded = bitsMeaningfulMask & bitTemp;
                        bitTemp >>= (int)bitsPerEntry;
                        bitsCollected -= bitsPerEntry;

                        var entry = new DebugInfoBoundsEntry();
                        if ((mappingDataEncoded & 0x1) != 0)
                            entry.SourceTypes |= SourceTypes.CallInstruction;
                        if ((mappingDataEncoded & 0x2) != 0)
                            entry.SourceTypes |= SourceTypes.StackEmpty;
                        if (majorVersion >= 17 && (mappingDataEncoded & 0x4) != 0)
                            entry.SourceTypes |= SourceTypes.Async;

                        mappingDataEncoded >>= (int)bitsForSourceType;
                        uint nativeOffsetDelta = (uint)(mappingDataEncoded & ((1UL << (int)bitsForNativeDelta) - 1));
                        previousNativeOffset += nativeOffsetDelta;
                        entry.NativeOffset = previousNativeOffset;

                        mappingDataEncoded >>= (int)bitsForNativeDelta;
                        entry.ILOffset = (uint)(mappingDataEncoded) + (uint)DebugInfoBoundsType.MaxMappingValue;

                        bounds.Add(entry);
                        curBoundsProcessed++;
                    }
                }
            }
            else
            {
                NibbleReader reader = new NibbleReader(imageReader, offset);
                uint boundsEntryCount = reader.ReadUInt();
                Debug.Assert(boundsEntryCount > 0);

                uint previousNativeOffset = 0;
                for (int i = 0; i < boundsEntryCount; ++i)
                {
                    var entry = new DebugInfoBoundsEntry();
                    previousNativeOffset += reader.ReadUInt();
                    entry.NativeOffset = previousNativeOffset;
                    entry.ILOffset = reader.ReadUInt() + (uint)DebugInfoBoundsType.MaxMappingValue;
                    entry.SourceTypes = (SourceTypes)reader.ReadUInt();
                    bounds.Add(entry);
                }
            }
        }

        static void ParseNativeVarInfo(
            NativeReader imageReader,
            int offset,
            Machine machine,
            List<NativeVarInfo> variables)
        {
            NibbleReader reader = new NibbleReader(imageReader, offset);
            uint nativeVarCount = reader.ReadUInt();

            for (int i = 0; i < nativeVarCount; ++i)
            {
                var entry = new NativeVarInfo();
                entry.StartOffset = reader.ReadUInt();
                entry.EndOffset = entry.StartOffset + reader.ReadUInt();
                entry.VariableNumber = (uint)(reader.ReadUInt() + (int)ImplicitILArguments.Max);

                // We don't have method signature info here, so we can't distinguish
                // parameters from locals. Leave Variable at default.
                entry.Variable = new Variable();

                var varLoc = new VarLoc();
                varLoc.VarLocType = (VarLocType)reader.ReadUInt();
                switch (varLoc.VarLocType)
                {
                    case VarLocType.VLT_REG:
                    case VarLocType.VLT_REG_FP:
                    case VarLocType.VLT_REG_BYREF:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        break;
                    case VarLocType.VLT_STK:
                    case VarLocType.VLT_STK_BYREF:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        varLoc.Data2 = ReadEncodedStackOffset(reader, machine);
                        break;
                    case VarLocType.VLT_REG_REG:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        varLoc.Data2 = (int)reader.ReadUInt();
                        break;
                    case VarLocType.VLT_REG_STK:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        varLoc.Data2 = (int)reader.ReadUInt();
                        varLoc.Data3 = ReadEncodedStackOffset(reader, machine);
                        break;
                    case VarLocType.VLT_STK_REG:
                        varLoc.Data1 = ReadEncodedStackOffset(reader, machine);
                        varLoc.Data2 = (int)reader.ReadUInt();
                        varLoc.Data3 = (int)reader.ReadUInt();
                        break;
                    case VarLocType.VLT_STK2:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        varLoc.Data2 = ReadEncodedStackOffset(reader, machine);
                        break;
                    case VarLocType.VLT_FPSTK:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        break;
                    case VarLocType.VLT_FIXED_VA:
                        varLoc.Data1 = (int)reader.ReadUInt();
                        break;
                    default:
                        throw new BadImageFormatException("Unexpected var loc type");
                }

                entry.VariableLocation = varLoc;
                variables.Add(entry);
            }
        }

        static int ReadEncodedStackOffset(NibbleReader reader, Machine machine)
        {
            int offset = reader.ReadInt();
            if (machine == Machine.I386)
            {
                offset *= 4; // sizeof(DWORD)
            }

            return offset;
        }
    }
}
