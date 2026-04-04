// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using ILCompiler.Reflection.ReadyToRun;
using ILCompiler.Reflection.ReadyToRun.Format;

namespace R2RDump;

/// <summary>
/// Dumps cross-referenced parsed R2R data produced by <see cref="RawReadyToRunParser"/>.
/// Shows methods with their runtime functions, unwind info, GC info, fixups, and import sections.
/// </summary>
internal sealed class ParsedDumper
{
    private readonly RawReadyToRunParser _parser;
    private readonly ReadyToRunMetadataResolver _resolver;
    private readonly TextWriter _writer;

    public ParsedDumper(RawReadyToRunParser parser, ReadyToRunMetadataResolver resolver, TextWriter writer)
    {
        _parser = parser;
        _resolver = resolver;
        _writer = writer;
    }

    public void Dump()
    {
        DumpMethods();
        DumpImportSections();
        DumpAvailableTypes();
    }

    private void DumpMethods()
    {
        var methods = _parser.GetMethods();
        _writer.WriteLine($"=== Methods ({methods.Count}) ===");
        _writer.WriteLine();

        foreach (var method in methods)
        {
            if (method.IsInstanceMethod)
            {
                _writer.Write($"  Instance Method");
                if (method.MethodRef != null)
                    _writer.Write($" {_resolver.ResolveMethod(method.MethodRef)}");
            }
            else
            {
                _writer.Write($"  MethodDef RID={method.Rid} {_resolver.ResolveMethodDef(method.Rid)}");
            }

            _writer.WriteLine($"  EntryPoint=rtf#{method.EntryPointRuntimeFunctionIndex}  Component={method.ComponentIndex}");

            // Runtime functions
            if (method.RuntimeFunctions.Count > 0)
            {
                _writer.WriteLine($"    Hot Runtime Functions ({method.RuntimeFunctions.Count}):");
                foreach (var rtf in method.RuntimeFunctions)
                {
                    DumpRuntimeFunction(rtf, "      ");
                }
            }

            if (method.ColdRuntimeFunctions.Count > 0)
            {
                _writer.WriteLine($"    Cold Runtime Functions ({method.ColdRuntimeFunctions.Count}):");
                foreach (var rtf in method.ColdRuntimeFunctions)
                {
                    DumpRuntimeFunction(rtf, "      ");
                }
            }

            // GC info
            if (method.GcInfo != null)
            {
                _writer.WriteLine($"    GC Info: Size={method.GcInfo.Size}");
            }

            // Fixups
            if (method.Fixups.Count > 0)
            {
                _writer.WriteLine($"    Fixups ({method.Fixups.Count}):");
                foreach (var fixup in method.Fixups)
                {
                    _writer.Write($"      [{fixup.TableIndex}:{fixup.CellIndex}]");
                    if (fixup.Signature != null)
                    {
                        _writer.Write($" {_resolver.ResolveFixupSignature(fixup.Signature)}");
                    }
                    _writer.WriteLine();
                }
            }

            _writer.WriteLine();
        }
    }

    private void DumpRuntimeFunction(ParsedRuntimeFunction rtf, string indent)
    {
        _writer.Write($"{indent}rtf#{rtf.Index}: RVA=[0x{rtf.StartRva:X4}");
        if (rtf.EndRva > 0)
            _writer.Write($"..0x{rtf.EndRva:X4}");
        _writer.Write($"] Size={rtf.Size} UnwindRVA=0x{rtf.UnwindRva:X4} CodeOffset=0x{rtf.CodeOffset:X}");

        if (rtf.UnwindInfo != null)
            _writer.Write($" UnwindSize={rtf.UnwindInfo.Size}");

        if (rtf.EHInfo != null)
            _writer.Write($" EHClauses={rtf.EHInfo.EHClauses.Count}");

        int debugOffset = _parser.GetDebugInfoOffset(rtf.Index);
        if (debugOffset >= 0)
            _writer.Write($" DebugOffset=0x{debugOffset:X}");

        _writer.WriteLine();
    }

    private void DumpImportSections()
    {
        var sections = _parser.GetImportSections();
        _writer.WriteLine($"=== Import Sections ({sections.Count}) ===");
        _writer.WriteLine();

        foreach (var section in sections)
        {
            var raw = section.RawEntry;
            _writer.WriteLine($"  Section [{section.Index}]: RVA=0x{raw.SectionRva:X4} Size={raw.SectionSize} Flags={raw.Flags} Type={raw.Type} EntrySize={raw.EntrySize}");

            if (section.Entries.Count > 0)
            {
                int displayCount = section.Entries.Count > 20 ? 20 : section.Entries.Count;
                for (int i = 0; i < displayCount; i++)
                {
                    var entry = section.Entries[i];
                    _writer.Write($"    [{entry.Index}] RVA=0x{entry.Rva:X4}");
                    if (entry.Signature != null)
                    {
                        _writer.Write($" {_resolver.ResolveFixupSignature(entry.Signature)}");
                    }
                    _writer.WriteLine();
                }
                if (section.Entries.Count > 20)
                    _writer.WriteLine($"    ... and {section.Entries.Count - 20} more entries");
            }

            _writer.WriteLine();
        }
    }

    private void DumpAvailableTypes()
    {
        var types = _parser.GetAvailableTypes();
        _writer.WriteLine($"=== Available Types ({types.Count}) ===");
        _writer.WriteLine();

        int displayCount = types.Count > 50 ? 50 : types.Count;
        for (int i = 0; i < displayCount; i++)
        {
            var t = types[i];
            string name = t.IsExported
                ? _resolver.ResolveExportedType(t.Rid, moduleIndex: -1)
                : _resolver.ResolveTypeDef(t.Rid, moduleIndex: -1);
            _writer.WriteLine($"  RID={t.Rid} {(t.IsExported ? "Exported" : "TypeDef")} {name}");
        }
        if (types.Count > 50)
            _writer.WriteLine($"  ... and {types.Count - 50} more types");
        _writer.WriteLine();
    }
}
