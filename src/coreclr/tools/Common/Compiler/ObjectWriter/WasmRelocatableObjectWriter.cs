// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using Internal.JitInterface;
using Internal.Text;
using Internal.TypeSystem.TypesDebugInfo;

namespace ILCompiler.ObjectWriter
{
    internal sealed partial class WasmRelocatableObjectWriter : WasmObjectWriter
    {
        private const byte FunctionNamesSubsection = 1;
        private const byte SegmentInfoSubsection = 5;
        private const byte SymbolTableSubsection = 8;
        private const uint SymbolUndefined = 0x10;
        private const uint SymbolVisibilityHidden = 0x04;
        private const int GeneratedSectionIndex = -1;
        private static readonly Utf8String RestoreContextTagName = new("__dotnet_restore_context_exception_tag");

        private readonly Dictionary<int, int> _sectionAlignments = new();
        private readonly Dictionary<int, List<SymbolicRelocation>> _relocations = new();
        private readonly Dictionary<(int SectionIndex, long Offset), long> _relocationValues = new();
        private readonly Dictionary<Utf8String, ISymbolNode> _relocationTargets = new();
        private readonly HashSet<Utf8String> _globalRelocationTargets = new();
        private readonly Dictionary<Utf8String, int> _linkingSymbolIndices = new();
        private readonly Dictionary<int, int> _dataSectionToSegmentIndex = new();
        private readonly Dictionary<int, uint> _dataSectionContentOffsets = new();
        private readonly List<LinkingSymbol> _linkingSymbols = new();
        private bool _hasRestoreContextTagRelocation;

        private protected override bool UsesSubsectionsViaSymbols => true;

        public WasmRelocatableObjectWriter(
            NodeFactory factory,
            ObjectWritingOptions options,
            OutputInfoBuilder outputInfoBuilder = null)
            : base(factory, options, outputInfoBuilder)
        {
        }

        private protected override void RecordRelocationTarget(
            ISymbolNode relocTarget,
            Utf8String relocSymbolName,
            RelocType relocType)
        {
            if (relocType == RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB)
            {
                _hasRestoreContextTagRelocation = true;
                return;
            }
            if (relocType == RelocType.WASM_GLOBAL_INDEX_LEB)
            {
                _globalRelocationTargets.Add(relocSymbolName);
            }

            if (!_relocationTargets.TryGetValue(relocSymbolName, out ISymbolNode existingTarget) ||
                existingTarget is ExternFunctionSymbolNode && relocTarget is INodeWithTypeSignature)
            {
                _relocationTargets[relocSymbolName] = relocTarget;
            }
        }

        protected internal override unsafe void EmitRelocation(
            int sectionIndex,
            long offset,
            Span<byte> data,
            RelocType relocType,
            Utf8String symbolName,
            long addend)
        {
            long relocationValue;
            fixed (byte* dataPointer = data)
            {
                relocationValue = Relocation.ReadValue(relocType, dataPointer);
            }

            _relocationValues.Add((sectionIndex, offset), relocationValue);
            base.EmitRelocation(sectionIndex, offset, data, relocType, symbolName, addend);
        }

        private protected override void EmitSectionsAndLayout()
        {
            int dataSegmentCount = GetDataSectionCount();
            if (dataSegmentCount == 0)
            {
                return;
            }

            SectionWriter writer = GetOrCreateSection(WasmObjectNodeSection.DataCountSection);
            writer.WriteULEB128((ulong)dataSegmentCount);
        }

        private protected override void EmitSymbolTable(
            IDictionary<Utf8String, SymbolDefinition> definedSymbols,
            SortedSet<Utf8String> undefinedSymbols)
        {
            BuildLinkingSymbols(definedSymbols, undefinedSymbols);
            base.EmitSymbolTable(definedSymbols, undefinedSymbols);
        }

        private protected override void EmitRelocations(int sectionIndex, List<SymbolicRelocation> relocationList)
        {
            if (relocationList.Count != 0)
            {
                _relocations.Add(sectionIndex, relocationList);
            }
        }

        private protected override void EmitObjectFile(Stream outputFileStream)
        {
            Debug.Assert(outputFileStream.CanSeek);

            FinalizeSectionEntryCounts();
            WasmSection dataSection = CreateDataSection();

            EmitWasmHeader(outputFileStream);

            uint wasmSectionIndex = 0;
            uint codeSectionIndex = uint.MaxValue;
            foreach (int sectionIndex in SectionEmitOrder)
            {
                WasmSection section = _sections.GetSection<WasmSection>(sectionIndex);
                ResolveStructuralRelocations(sectionIndex, section);
                section.EmitToStream(outputFileStream);

                if (section.Type == WasmSectionType.Code)
                {
                    codeSectionIndex = wasmSectionIndex;
                }

                wasmSectionIndex++;
            }

            uint dataSectionIndex = uint.MaxValue;
            if (dataSection is not null)
            {
                dataSectionIndex = wasmSectionIndex++;
                dataSection.EmitToStream(outputFileStream);
            }

            CreateLinkingSection().EmitToStream(outputFileStream);

            EmitRelocationSection(
                outputFileStream,
                ObjectNodeSection.WasmCodeSection,
                WasmObjectNodeSection.CodeRelocationSection,
                codeSectionIndex);

            if (dataSection is not null)
            {
                EmitDataRelocationSection(outputFileStream, dataSectionIndex);
            }

            CreateNameSection().EmitToStream(outputFileStream);
        }

        private protected override SectionDataEmitter CreateDataSection(
            ObjectNodeSection section,
            int sectionIndex,
            Stream sectionStream)
        {
            return new WasmDataSegment(WasmSectionType.Data, sectionStream, new Utf8String(section.Name), sectionIndex);
        }

        protected internal override void UpdateSectionAlignment(int sectionIndex, int alignment)
        {
            if (_sectionAlignments.TryGetValue(sectionIndex, out int currentAlignment))
            {
                _sectionAlignments[sectionIndex] = Math.Max(currentAlignment, alignment);
            }
            else
            {
                _sectionAlignments.Add(sectionIndex, alignment);
            }
        }

        private protected override void WriteImports()
        {
            WriteGlobalImportIfReferenced(
                WasmWellKnownGlobalSymbolNode.StackPointerName,
                WasmMutabilityType.Mut);
            WriteGlobalImportIfReferenced(
                WasmWellKnownGlobalSymbolNode.ImageBaseName,
                WasmMutabilityType.Const);
            WriteGlobalImportIfReferenced(
                WasmWellKnownGlobalSymbolNode.TableBaseName,
                WasmMutabilityType.Const);
            WriteGlobalImportIfReferenced(
                WasmWellKnownGlobalSymbolNode.AsyncContinuationName,
                WasmMutabilityType.Mut);

            foreach (LinkingSymbol symbol in _linkingSymbols)
            {
                if (symbol.IsDefined)
                {
                    continue;
                }

                if (symbol.Kind == WasmLinkingSymbolKind.Function)
                {
                    WriteImport(new WasmImport(
                        "env",
                        symbol.Name.ToString(),
                        new WasmFunctionImportType(symbol.FunctionTypeIndex)));
                }
                else if (symbol.Kind == WasmLinkingSymbolKind.Tag)
                {
                    WriteImport(new WasmImport(
                        "env",
                        symbol.Name.ToString(),
                        new WasmTagImportType(symbol.FunctionTypeIndex)));
                }
            }

            void WriteGlobalImportIfReferenced(string name, WasmMutabilityType mutability)
            {
                Utf8String symbolName = new(name);
                if (_globalRelocationTargets.Contains(symbolName))
                {
                    WriteImport(new WasmImport(
                        "env",
                        name,
                        new WasmGlobalImportType(WasmValueType.I32, mutability)));
                }
            }
        }

        private protected override void WriteGlobalSection()
        {
        }

        private protected override void WriteExports()
        {
        }

        private protected override void WriteElements()
        {
        }

        private int GetDataSectionCount()
        {
            int count = 0;
            foreach (SectionDataEmitter section in _sections.Sections)
            {
                if (section is WasmSection { Type: WasmSectionType.Data })
                {
                    count++;
                }
            }

            return count;
        }

        private WasmSection CreateWasmDataSection()
        {
            List<WasmDataSegment> sectionIndices = new();

            for (int sectionIndex = 0; sectionIndex < _sections.Count; sectionIndex++)
            {
                SectionDataEmitter section = _sections.GetSection<SectionDataEmitter>(sectionIndex);
                if (section is not WasmDataSegment wasmSegment)
                {
                    continue;
                }

                sectionIndices.Add(wasmSegment);
            }

            return new WasmDataSection(sectionIndices, new Utf8String("data"));
        }

        private void BuildLinkingSymbols(
            IDictionary<Utf8String, SymbolDefinition> definedSymbols,
            SortedSet<Utf8String> undefinedSymbols)
        {
            List<Utf8String> names = new(definedSymbols.Keys);
            names.Sort();

            foreach (Utf8String name in names)
            {
                SymbolDefinition definition = definedSymbols[name];
                WasmSectionType sectionType = _sections.GetSection<WasmSection>(definition.SectionIndex).Type;
                WasmLinkingSymbolKind? kind = sectionType switch
                {
                    WasmSectionType.Code => WasmLinkingSymbolKind.Function,
                    WasmSectionType.Data => WasmLinkingSymbolKind.Data,
                    WasmSectionType.Global => WasmLinkingSymbolKind.Global,
                    _ => null,
                };

                if (kind.HasValue)
                {
                    AddLinkingSymbol(new LinkingSymbol(
                        name,
                        kind.Value,
                        isDefined: true,
                        definition.SectionIndex,
                        definition.Value,
                        definition.Size,
                        GetRelocationTarget(name),
                        definition.Global));
                }
            }

            foreach (Utf8String name in undefinedSymbols)
            {
                ISymbolNode target = GetRelocationTarget(name);
                if (target is WasmTypeNode)
                {
                    continue;
                }

                WasmLinkingSymbolKind kind = target switch
                {
                    _ when _globalRelocationTargets.Contains(name) => WasmLinkingSymbolKind.Global,
                    WasmWellKnownGlobalSymbolNode => WasmLinkingSymbolKind.Global,
                    ExternFunctionSymbolNode => WasmLinkingSymbolKind.Function,
                    INodeWithTypeSignature => WasmLinkingSymbolKind.Function,
                    IMethodNode => WasmLinkingSymbolKind.Function,
                    _ => WasmLinkingSymbolKind.Data,
                };

                AddLinkingSymbol(new LinkingSymbol(
                    name,
                    kind,
                    isDefined: false,
                    sectionIndex: -1,
                    value: 0,
                    size: 0,
                    target,
                    isGlobal: true));
            }

            if (_hasRestoreContextTagRelocation)
            {
                LinkingSymbol tagSymbol = new(
                    RestoreContextTagName,
                    WasmLinkingSymbolKind.Tag,
                    isDefined: false,
                    sectionIndex: -1,
                    value: 0,
                    size: 0,
                    target: null,
                    isGlobal: true);
                tagSymbol.FunctionTypeIndex = RegisterSignature(new WasmFuncType(new([]), new([])));
                AddLinkingSymbol(tagSymbol);
            }

            foreach (LinkingSymbol symbol in _linkingSymbols)
            {
                if (symbol.Kind != WasmLinkingSymbolKind.Function)
                {
                    continue;
                }

                symbol.FunctionTypeIndex = GetFunctionTypeIndex(symbol, definedSymbols);
            }
        }

        private ISymbolNode GetRelocationTarget(Utf8String name)
        {
            _relocationTargets.TryGetValue(name, out ISymbolNode target);
            return target;
        }

        private void AddLinkingSymbol(LinkingSymbol symbol)
        {
            if (_linkingSymbolIndices.ContainsKey(symbol.Name))
            {
                return;
            }

            _linkingSymbolIndices.Add(symbol.Name, _linkingSymbols.Count);
            _linkingSymbols.Add(symbol);
        }

        private int GetFunctionTypeIndex(
            LinkingSymbol symbol,
            IDictionary<Utf8String, SymbolDefinition> definedSymbols)
        {
            if (_functionTypeIndices.TryGetValue(symbol.Name, out int functionTypeIndex))
            {
                return functionTypeIndex;
            }

            if (symbol.IsDefined)
            {
                foreach (KeyValuePair<Utf8String, int> candidate in _functionTypeIndices)
                {
                    if (definedSymbols.TryGetValue(candidate.Key, out SymbolDefinition definition) &&
                        definition.SectionIndex == symbol.SectionIndex &&
                        definition.Value == symbol.Value)
                    {
                        return candidate.Value;
                    }
                }
            }

            if (symbol.Target is INodeWithTypeSignature nodeWithSignature)
            {
                return RegisterSignature(GetFunctionSignature(nodeWithSignature));
            }
            if (symbol.Target is IMethodNode methodNode)
            {
                return RegisterSignature(GetFunctionSignature(methodNode));
            }

            throw new InvalidOperationException(
                $"No WebAssembly function signature is available for undefined symbol '{symbol.Name}'.");
        }

        private WasmLinkingSection CreateLinkingSection()
        {
            SectionWriter sectionWriter = CreateGeneratedSectionWriter(out Stream sectionStream);
            WasmLinkingSection section = new(
                sectionStream,
                new Utf8String(WasmObjectNodeSection.LinkingSection.Name),
                GeneratedSectionIndex);

            WasmSegmentInfoSubsection segmentInfo = CreateSegmentInfoSubsection();
            section.WriteSubsection(sectionWriter, segmentInfo);

            WasmSymbolTableSubsection symbolTable = CreateSymbolTableSubsection();
            section.WriteSubsection(sectionWriter, symbolTable);

            return section;
        }

        private WasmNameSection CreateNameSection()
        {
            SectionWriter sectionWriter = CreateGeneratedSectionWriter(out Stream sectionStream);
            WasmNameSection section = new(sectionStream, GeneratedSectionIndex);

            SectionWriter functionNamesWriter = CreateGeneratedSectionWriter(out Stream functionNamesStream);
            WasmFunctionNamesSubsection functionNames = new(
                FunctionNamesSubsection,
                functionNamesStream,
                GeneratedSectionIndex);

            foreach (WasmSymbol symbol in _wasmSymbolManager.GetDefinitions(WasmIndexSpace.Function))
            {
                functionNames.WriteEntry(
                    functionNamesWriter,
                    new WasmFunctionName(symbol.Index, symbol.Name));
            }

            section.WriteSubsection(sectionWriter, functionNames);
            return section;
        }

        private WasmSegmentInfoSubsection CreateSegmentInfoSubsection()
        {
            SectionWriter writer = CreateGeneratedSectionWriter(out Stream stream);
            WasmSegmentInfoSubsection subsection = new(
                SegmentInfoSubsection,
                stream,
                GeneratedSectionIndex);

            for (int sectionIndex = 0; sectionIndex < _sections.Count; sectionIndex++)
            {
                if (!_dataSectionToSegmentIndex.ContainsKey(sectionIndex))
                {
                    continue;
                }

                int alignment = _sectionAlignments.TryGetValue(sectionIndex, out int value) ? value : 1;
                Debug.Assert(int.IsPow2(alignment));
                subsection.WriteEntry(
                    writer,
                    new WasmSegmentInfo(
                        GetSegmentName(_sections[sectionIndex].SectionName),
                        int.Log2(alignment),
                        flags: 0));
            }

            return subsection;
        }

        private WasmSymbolTableSubsection CreateSymbolTableSubsection()
        {
            SectionWriter writer = CreateGeneratedSectionWriter(out Stream stream);
            WasmSymbolTableSubsection subsection = new(
                SymbolTableSubsection,
                stream,
                GeneratedSectionIndex);

            foreach (LinkingSymbol symbol in _linkingSymbols)
            {
                uint flags = symbol.IsDefined && !symbol.IsGlobal ? SymbolVisibilityHidden : 0;
                if (!symbol.IsDefined)
                {
                    flags |= SymbolUndefined;
                }

                int index = symbol.Kind switch
                {
                    WasmLinkingSymbolKind.Function => GetFunctionIndex(symbol),
                    WasmLinkingSymbolKind.Global or WasmLinkingSymbolKind.Tag =>
                        _wasmSymbolManager.GetSymbol(symbol.Name).Index,
                    WasmLinkingSymbolKind.Data => -1,
                    _ => throw new UnreachableException(),
                };
                int segmentIndex = symbol.IsDefined && symbol.Kind == WasmLinkingSymbolKind.Data
                    ? _dataSectionToSegmentIndex[symbol.SectionIndex]
                    : -1;

                subsection.WriteEntry(
                    writer,
                    new WasmLinkingSymbol(
                        symbol.Kind,
                        flags,
                        symbol.Name,
                        index,
                        symbol.IsDefined,
                        segmentIndex,
                        symbol.Value,
                        symbol.Size));
            }

            return subsection;
        }

        private int GetFunctionIndex(LinkingSymbol symbol)
        {
            if (_wasmSymbolManager.TryGetSymbol(symbol.Name, out WasmSymbol wasmSymbol))
            {
                return wasmSymbol.Index;
            }

            if (symbol.IsDefined)
            {
                foreach (KeyValuePair<Utf8String, int> candidate in _functionTypeIndices)
                {
                    if (!_wasmSymbolManager.TryGetSymbol(candidate.Key, out wasmSymbol))
                    {
                        continue;
                    }

                    if (_definedSymbols.TryGetValue(candidate.Key, out SymbolDefinition definition) &&
                        definition.SectionIndex == symbol.SectionIndex &&
                        definition.Value == symbol.Value)
                    {
                        return wasmSymbol.Index;
                    }
                }
            }

            throw new InvalidOperationException($"Function symbol '{symbol.Name}' has no WebAssembly function index.");
        }

        private void EmitDataRelocationSection(Stream outputFileStream, uint dataSectionIndex)
        {
            SectionWriter writer = CreateGeneratedSectionWriter(out Stream stream);
            WasmRelocationSection section = new(
                stream,
                new Utf8String(WasmObjectNodeSection.DataRelocationSection.Name),
                GeneratedSectionIndex,
                dataSectionIndex);

            for (int sectionIndex = 0; sectionIndex < _sections.Count; sectionIndex++)
            {
                if (!_dataSectionContentOffsets.TryGetValue(sectionIndex, out uint contentOffset) ||
                    !_relocations.TryGetValue(sectionIndex, out List<SymbolicRelocation> relocations))
                {
                    continue;
                }

                WriteRelocations(section, writer, sectionIndex, relocations, contentOffset);
            }

            if (section.EntryCount != 0)
            {
                section.EmitToStream(outputFileStream);
            }
        }

        private void EmitRelocationSection(
            Stream outputFileStream,
            ObjectNodeSection targetSection,
            ObjectNodeSection relocationSection,
            uint wasmSectionIndex)
        {
            if (!_sections.Contains(targetSection.Name))
            {
                return;
            }

            int sectionIndex = _sections.GetSectionIndex(targetSection.Name);
            if (!_relocations.TryGetValue(sectionIndex, out List<SymbolicRelocation> relocations) ||
                relocations.Count == 0)
            {
                return;
            }

            if (wasmSectionIndex == uint.MaxValue)
            {
                throw new InvalidOperationException($"Cannot emit relocations for omitted section '{targetSection.Name}'.");
            }

            uint contentOffset = DwarfHelper.SizeOfULEB128((ulong)MethodCount);
            SectionWriter writer = CreateGeneratedSectionWriter(out Stream stream);
            WasmRelocationSection section = new(
                stream,
                new Utf8String(relocationSection.Name),
                GeneratedSectionIndex,
                wasmSectionIndex);
            WriteRelocations(section, writer, sectionIndex, relocations, contentOffset);
            section.EmitToStream(outputFileStream);
        }

        private void WriteRelocations(
            WasmRelocationSection section,
            SectionWriter writer,
            int sectionIndex,
            List<SymbolicRelocation> relocations,
            uint contentOffset)
        {
            foreach (SymbolicRelocation relocation in relocations)
            {
                Utf8String symbolName = relocation.Type == RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB
                    ? RestoreContextTagName
                    : relocation.SymbolName;
                WasmRelocationKind kind = GetRelocationKind(relocation);
                int index;
                if (kind == WasmRelocationKind.TypeIndexLeb)
                {
                    index = _wasmSymbolManager.GetSymbol(symbolName).Index;
                }
                else
                {
                    if (!_linkingSymbolIndices.TryGetValue(symbolName, out int symbolIndex))
                    {
                        throw new InvalidOperationException(
                            $"Relocation target '{symbolName}' has no linking symbol.");
                    }

                    index = symbolIndex;
                }

                long addend = relocation.Addend + GetRelocationValue(sectionIndex, relocation);
                section.WriteEntry(
                    writer,
                    new WasmRelocation(
                        kind,
                        contentOffset + checked((ulong)relocation.Offset),
                        checked((ulong)index),
                        addend));
            }
        }

        private long GetRelocationValue(int sectionIndex, SymbolicRelocation relocation)
        {
            if (!_relocationValues.TryGetValue((sectionIndex, relocation.Offset), out long value))
            {
                throw new InvalidOperationException(
                    $"Relocation value for section {sectionIndex} at offset {relocation.Offset} was not recorded.");
            }

            return value;
        }

        private WasmRelocationKind GetRelocationKind(SymbolicRelocation relocation)
        {
            return relocation.Type switch
            {
                RelocType.WASM_FUNCTION_INDEX_LEB => WasmRelocationKind.FunctionIndexLeb,
                RelocType.WASM_TABLE_INDEX_SLEB => WasmRelocationKind.TableIndexSleb,
                RelocType.WASM_MEMORY_ADDR_LEB => WasmRelocationKind.MemoryAddressLeb,
                RelocType.WASM_MEMORY_ADDR_SLEB => WasmRelocationKind.MemoryAddressSleb,
                RelocType.WASM_MEMORY_ADDR_REL_LEB => WasmRelocationKind.MemoryAddressLeb,
                RelocType.WASM_MEMORY_ADDR_REL_SLEB => GetAddressSlebRelocationKind(relocation),
                RelocType.WASM_TYPE_INDEX_LEB => WasmRelocationKind.TypeIndexLeb,
                RelocType.WASM_GLOBAL_INDEX_LEB => WasmRelocationKind.GlobalIndexLeb,
                RelocType.WASM_TABLE_INDEX_I32 => WasmRelocationKind.TableIndexI32,
                RelocType.WASM_TABLE_INDEX_I64 => WasmRelocationKind.TableIndexI64,
                RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB => WasmRelocationKind.TagIndexLeb,
                RelocType.IMAGE_REL_BASED_HIGHLOW => GetAddressRelocationKind(relocation, is64Bit: false),
                RelocType.IMAGE_REL_BASED_DIR64 => GetAddressRelocationKind(relocation, is64Bit: true),
                RelocType.IMAGE_REL_BASED_RELPTR32 => GetRelativePointerRelocationKind(relocation),
                _ => throw new NotSupportedException($"Unsupported WebAssembly relocation type: {relocation.Type}: {relocation}."),
            };
        }

        private WasmRelocationKind GetAddressRelocationKind(SymbolicRelocation relocation, bool is64Bit)
        {
            if (!_linkingSymbolIndices.TryGetValue(relocation.SymbolName, out int symbolIndex))
            {
                throw new InvalidOperationException(
                    $"Relocation target '{relocation.SymbolName}' has no linking symbol.");
            }

            return _linkingSymbols[symbolIndex].Kind == WasmLinkingSymbolKind.Function
                ? is64Bit ? WasmRelocationKind.TableIndexI64 : WasmRelocationKind.TableIndexI32
                : is64Bit ? WasmRelocationKind.MemoryAddressI64 : WasmRelocationKind.MemoryAddressI32;
        }

        private WasmRelocationKind GetAddressSlebRelocationKind(SymbolicRelocation relocation)
        {
            if (!_linkingSymbolIndices.TryGetValue(relocation.SymbolName, out int symbolIndex))
            {
                throw new InvalidOperationException(
                    $"Relocation target '{relocation.SymbolName}' has no linking symbol.");
            }

            if (_linkingSymbols[symbolIndex].Kind != WasmLinkingSymbolKind.Function)
            {
                return WasmRelocationKind.MemoryAddressRelativeSleb;
            }

            // The JIT uses an image-relative address relocation for portable function entry points.
            // Static wasm-ld links resolve the image base to zero and require a table-index relocation.
            return WasmRelocationKind.TableIndexSleb;
        }

        private WasmRelocationKind GetRelativePointerRelocationKind(SymbolicRelocation relocation)
        {
            if (!_linkingSymbolIndices.TryGetValue(relocation.SymbolName, out int symbolIndex))
            {
                throw new InvalidOperationException(
                    $"Relocation target '{relocation.SymbolName}' has no linking symbol.");
            }

            WasmLinkingSymbolKind symbolKind = _linkingSymbols[symbolIndex].Kind;
            if (symbolKind != WasmLinkingSymbolKind.Data)
            {
                throw new NotSupportedException(
                    $"WebAssembly relative pointer relocation target '{relocation.SymbolName}' is a {symbolKind} symbol.");
            }

            return WasmRelocationKind.MemoryAddressLocRelativeI32;
        }

        private unsafe void ResolveStructuralRelocations(int sectionIndex, WasmSection section)
        {
            if (!_relocations.TryGetValue(sectionIndex, out List<SymbolicRelocation> relocations))
            {
                return;
            }

            MemoryStream resolvedStream = new(checked((int)section.ContentReadStream.Length));
            section.ContentReadStream.Position = 0;
            section.ContentReadStream.CopyTo(resolvedStream);

            byte[] relocationBuffer = new byte[Relocation.MaxSize];
            foreach (SymbolicRelocation relocation in relocations)
            {
                if (relocation.Type is not RelocType.WASM_FUNCTION_INDEX_LEB and
                    not RelocType.WASM_TYPE_INDEX_LEB and
                    not RelocType.WASM_GLOBAL_INDEX_LEB and
                    not RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB)
                {
                    continue;
                }

                int relocationSize = Relocation.GetSize(relocation.Type);
                Span<byte> relocationContents = relocationBuffer.AsSpan(0, relocationSize);
                relocationContents.Clear();

                fixed (byte* data = relocationContents)
                {
                    Utf8String symbolName = relocation.Type == RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB
                        ? RestoreContextTagName
                        : relocation.SymbolName;
                    int index = _wasmSymbolManager.GetSymbol(symbolName).Index;
                    Relocation.WriteValue(relocation.Type, data, index + GetRelocationValue(sectionIndex, relocation));
                }

                resolvedStream.Position = relocation.Offset;
                resolvedStream.Write(relocationContents);
            }

            section.ContentReadStream = resolvedStream;
        }

        private static Utf8String GetSegmentName(Utf8String sectionName)
        {
            string name = sectionName.ToString();
            return name switch
            {
                "rdata" => new Utf8String(".rodata"),
                _ when name.StartsWith('_') || name.StartsWith('.') => sectionName,
                _ => new Utf8String("." + name),
            };
        }

        private SectionWriter CreateGeneratedSectionWriter(out Stream contentReadStream)
        {
            SectionData sectionData = new();
            contentReadStream = sectionData.GetReadStream();
            return new SectionWriter(this, GeneratedSectionIndex, sectionData);
        }

        private static void WriteULEB128(Stream output, ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            int bytesWritten = DwarfHelper.WriteULEB128(buffer, value);
            output.Write(buffer.Slice(0, bytesWritten));
        }

        private sealed class LinkingSymbol
        {
            public Utf8String Name { get; }
            public WasmLinkingSymbolKind Kind { get; }
            public bool IsDefined { get; }
            public int SectionIndex { get; }
            public long Value { get; }
            public int Size { get; }
            public ISymbolNode Target { get; }
            public bool IsGlobal { get; }
            public int FunctionTypeIndex { get; set; } = -1;

            public LinkingSymbol(
                Utf8String name,
                WasmLinkingSymbolKind kind,
                bool isDefined,
                int sectionIndex,
                long value,
                int size,
                ISymbolNode target,
                bool isGlobal)
            {
                Name = name;
                Kind = kind;
                IsDefined = isDefined;
                SectionIndex = sectionIndex;
                Value = value;
                Size = size;
                Target = target;
                IsGlobal = isGlobal;
            }
        }
    }

    internal sealed partial class WasmRelocatableObjectWriter
    {
        private protected override void EmitUnwindInfo(
            SectionWriter sectionWriter,
            INodeWithCodeInfo nodeWithCodeInfo,
            Utf8String currentSymbolName)
        {
        }

        private protected override ITypesDebugInfoWriter CreateDebugInfoBuilder() => null;

        private protected override void EmitDebugFunctionInfo(
            uint methodTypeIndex,
            Utf8String methodName,
            SymbolDefinition methodSymbol,
            INodeWithDebugInfo debugNode,
            bool hasSequencePoints)
        {
        }

        private protected override void EmitDebugSections(
            IDictionary<Utf8String, SymbolDefinition> definedSymbols)
        {
        }

        private protected override void CreateEhSections()
        {
        }
    }

    internal partial static class WasmObjectNodeSection
    {
        public static readonly ObjectNodeSection CodeRelocationSection = new("reloc.CODE", SectionType.ReadOnly, needsAlign: false);
        public static readonly ObjectNodeSection DataRelocationSection = new("reloc.DATA", SectionType.ReadOnly, needsAlign: false);
        public static readonly ObjectNodeSection LinkingSection = new("linking", SectionType.ReadOnly, needsAlign: false);
    }
}
