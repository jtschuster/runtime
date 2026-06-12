// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using ILCompiler.DependencyAnalysis;
using Internal.Text;
using Internal.TypeSystem.TypesDebugInfo;
using CodeDataLayout = CodeDataLayoutMode.CodeDataLayout;

namespace ILCompiler.ObjectWriter;

/// <summary>
/// NativeAOT relocatable WebAssembly object writer.
/// Emits a wasm object scaffold that will eventually carry relocations and a linking section
/// suitable for wasm-ld consumption.
/// </summary>
internal sealed class WasmNativeObjectWriter : ObjectWriter
{
    protected override CodeDataLayout LayoutMode => CodeDataLayout.Separate;

    public WasmNativeObjectWriter(NodeFactory factory, ObjectWritingOptions options)
        : base(factory, options)
    {
    }

    private protected override void CreateSection(ObjectNodeSection section, Utf8String comdatName, Utf8String symbolName, int sectionIndex, Stream sectionStream)
    {
    }

    protected internal override void UpdateSectionAlignment(int sectionIndex, int alignment)
    {
    }

    private protected override void EmitRelocations(int sectionIndex, List<SymbolicRelocation> relocationList)
    {
    }

    private protected override void EmitSymbolTable(IDictionary<Utf8String, SymbolDefinition> definedSymbols, SortedSet<Utf8String> undefinedSymbols)
    {
    }

    private protected override void EmitObjectFile(Stream outputFileStream)
    {
        outputFileStream.Write("\0asm"u8);
        outputFileStream.Write([0x1, 0x0, 0x0, 0x0]);

        // TODO-Wasm: Emit relocatable wasm sections, reloc.* custom sections, and the linking section.
    }

    // Debug info is not yet implemented for the Wasm NativeAOT writer.
    private protected override ITypesDebugInfoWriter CreateDebugInfoBuilder() =>
        throw new NotImplementedException("Wasm NativeAOT debug info is not yet implemented");

    private protected override void EmitDebugFunctionInfo(
        uint methodTypeIndex,
        Utf8String methodName,
        SymbolDefinition methodSymbol,
        INodeWithDebugInfo debugNode,
        bool hasSequencePoints)
    {
    }

    private protected override void EmitDebugSections(IDictionary<Utf8String, SymbolDefinition> definedSymbols)
    {
    }

    private protected override void CreateEhSections()
    {
    }

    private protected override void EmitUnwindInfo(
        SectionWriter sectionWriter,
        INodeWithCodeInfo nodeWithCodeInfo,
        Utf8String currentSymbolName)
    {
    }
}
