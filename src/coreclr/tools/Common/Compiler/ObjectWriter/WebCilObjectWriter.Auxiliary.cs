// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if READYTORUN
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.ReadyToRun;
using ILCompiler.DependencyAnalysis.Wasm;
using ILCompiler.DependencyAnalysisFramework;
using ILCompiler.ObjectWriter.WasmInstructions;
using Internal.Text;

namespace ILCompiler.ObjectWriter;

internal sealed partial class WebCilObjectWriter
{
    private readonly List<HeaderTableNode> _entryPointTables = new();
    private int _activationSignature;

    public override void EmitObject(Stream stream, IReadOnlyCollection<DependencyNode> nodes, IObjectDumper dumper, Logger logger)
    {
        if (_nodeFactory.UsesWasmAuxiliaryModules)
        {
            foreach (DependencyNode node in nodes)
            {
                if (node is MethodEntryPointTableNode or InstanceEntryPointTableNode)
                    _entryPointTables.Add((HeaderTableNode)node);
            }
        }
        base.EmitObject(stream, nodes, dumper, logger);
    }

    private void EmitAuxiliaryEntryPointsModule(WebcilSection[] sections)
    {
        List<IWasmDataSegment> segments = new();
        int metadataOffset = checked((int)ResolveSymbolRVA(sections,
            _definedSymbols[GetMangledName(_nodeFactory.AuxiliaryModuleMetadata)]));
        List<WasmExpr> instructions = new()
        {
            Global.Get(ImageBaseGlobalIndex),
            I32.Load((ulong)metadataOffset + AuxiliaryModuleMetadataNode.EntryPointsStatusOffset),
            I32.Eqz,
            Block.If(WasmBlockType.Empty),
        };
        foreach (HeaderTableNode table in _entryPointTables)
        {
            SymbolDefinition definition = _definedSymbols[GetMangledName(table)];
            WebcilSection section = (WebcilSection)_sections[definition.SectionIndex];
            if (!section.ContentReadStream.CanWrite)
            {
                using Stream original = section.ContentReadStream;
                MemoryStream contents = new(checked((int)original.Length));
                original.Position = 0;
                original.CopyTo(contents);
                section.ContentReadStream = contents;
            }
            byte[] data = new byte[definition.Size];
            section.ContentReadStream.Position = definition.Value;
            section.ContentReadStream.ReadExactly(data);
            segments.Add(new WasmByteArrayDataSegment(data, GetMangledName(table), fileAlignment: 1));

            // Fixup blobs precede the symbol and stay resident. Only the lookup records move.
            section.ContentReadStream.Position = definition.Value;
            section.ContentReadStream.Write(new byte[data.Length]);
            instructions.Add(Global.Get(ImageBaseGlobalIndex));
            instructions.Add(I32.Const(checked((int)ResolveSymbolRVA(sections, definition))));
            instructions.Add(I32.Add);
            instructions.Add(I32.Const(0));
            instructions.Add(I32.Const(data.Length));
            instructions.Add(Memory.Init(segments.Count - 1));
        }
        instructions.Add(Global.Get(ImageBaseGlobalIndex));
        instructions.Add(I32.Const(1));
        instructions.Add(I32.Store((ulong)metadataOffset + AuxiliaryModuleMetadataNode.EntryPointsStatusOffset));
        instructions.Add(Block.End);
        WasmFunctionBody activation = new(new WasmFuncType(new([]), new([])), instructions.ToArray());

        string outputPath = Path.ChangeExtension(_nodeFactory.AuxiliaryModuleMetadata.OutputFilePath, ".entrypoints.wasm");
        using FileStream output = File.Create(outputPath);
        EmitWasmHeader(output);
        _sections.GetSection<WasmExternallyCountedSection>(ObjectNodeSection.WasmTypeSection.Name).EmitToStream(output);
        _sections.GetSection<WasmImportSection>(WasmObjectNodeSection.ImportSection.Name).EmitToStream(output);
        WriteSection(WasmSectionType.Function, content =>
        {
            WriteUnsigned(content, 1);
            WriteUnsigned(content, _activationSignature);
        });
        WriteSection(WasmSectionType.Export, content =>
        {
            WriteUnsigned(content, 1);
            byte[] name = Encoding.UTF8.GetBytes("ActivateModule");
            WriteUnsigned(content, name.Length);
            content.Write(name);
            content.WriteByte(0); // function
            WriteUnsigned(content, _wasmSymbolManager.GetImportCount(WasmIndexSpace.Function));
        });
        WriteSection(WasmSectionType.DataCount, content => WriteUnsigned(content, segments.Count));
        WriteSection(WasmSectionType.Code, content =>
        {
            WriteUnsigned(content, 1);
            byte[] body = new byte[activation.EncodeSize()];
            activation.Encode(body);
            content.Write(body);
        });
        new WasmDataSection(segments, new Utf8String("data")).EmitToStream(output);
        if (_emitNameSection)
        {
            new WasmNameSection([
                new WasmSymbol(new Utf8String("ActivateModule"), WasmIndexSpace.Function,
                    _wasmSymbolManager.GetImportCount(WasmIndexSpace.Function), IsImport: false, HasExplicitName: true),
            ]).EmitToStream(output);
        }

        void WriteSection(WasmSectionType type, Action<MemoryStream> emit)
        {
            using MemoryStream content = new();
            emit(content);
            new WasmSection(type, content, default, sectionIndex: -1).EmitToStream(output);
        }
    }

    private static void WriteUnsigned(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[5];
        int length = DwarfHelper.WriteULEB128(bytes, checked((uint)value));
        stream.Write(bytes.Slice(0, length));
    }
}
#endif
