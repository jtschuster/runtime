// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using ILCompiler.ReadyToRun.TypeSystem;
using Internal;
using Internal.JitInterface;
using Internal.NativeFormat;
using Internal.Runtime;
using Internal.Text;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace ILCompiler.DependencyAnalysis.ReadyToRun
{
    public class InstanceEntryPointTableNode : HeaderTableNode, ISignatureEmitter
    {
        private readonly NodeFactory _factory;
        private bool _materializedSignature;

        public InstanceEntryPointTableNode(NodeFactory factory)
        {
            _factory = factory;
            _factory.ManifestMetadataTable.RegisterEmitter(this);
        }

        public void MaterializeSignature()
        {
            if (!_materializedSignature)
            {
                if (_factory.CompilationModuleGroup.IsInputBubble)
                {
                    foreach (MethodWithGCInfo method in _factory.EnumerateCompiledMethods(null, CompiledMethodCategory.Instantiated))
                    {
                        if (method.Method is AsyncResumptionStub)
                            continue;

                        BuildSignatureForMethod(method, _factory);
                    }
                }

                _materializedSignature = true;
            }
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix);
            sb.Append("__ReadyToRunInstanceEntryPointTable"u8);
        }

        public static byte[] BuildSignatureForMethodDefinedInModule(MethodDesc method, NodeFactory factory)
        {
            MethodDesc primaryMethod = method.GetPrimaryMethodDesc();
            EcmaMethod typicalMethod = (EcmaMethod)primaryMethod.GetTypicalMethodDefinition();

            // The unboxing stub (a MethodDelegator) flows through as MethodWithToken.Method: its
            // OwningType/signature delegate to the underlying value-type method, and its unboxing-ness
            // is recovered from identity in EmitMethodSignature (mirroring the async-variant flag) —
            // so we no longer thread a side-channel `unboxing` bool here. The moduleToken below is
            // still computed from the underlying value-type method because the stub's synthetic boxed
            // owning type (BoxedValueType) has no ECMA token of its own.
            ModuleToken moduleToken;
            if (factory.CompilationModuleGroup.VersionsWithMethodBody(typicalMethod))
            {
                moduleToken = new ModuleToken(typicalMethod.Module, typicalMethod.Handle);
            }
            else
            {
                MutableModule manifestMetadata = factory.ManifestMetadataTable._mutableModule;
                var handle = manifestMetadata.TryGetExistingEntityHandle(typicalMethod);
                Debug.Assert(handle.HasValue);
                moduleToken = new ModuleToken(factory.ManifestMetadataTable._mutableModule, handle.Value);
            }

            ArraySignatureBuilder signatureBuilder = new ArraySignatureBuilder();
            signatureBuilder.EmitMethodSignature(
                new MethodWithToken(method, moduleToken, constrainedType: null, unboxing: false, genericContextObject: null),
                enforceDefEncoding: true,
                enforceOwningType: moduleToken.Module is EcmaModule ? factory.CompilationModuleGroup.EnforceOwningType((EcmaModule)moduleToken.Module) : true,
                factory.SignatureContext,
                isInstantiatingStub: false);

            return signatureBuilder.ToArray();
        }

        private byte[] BuildSignatureForMethod(MethodWithGCInfo method, NodeFactory factory)
        {
            return BuildSignatureForMethodDefinedInModule(method.Method, factory);
        }

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            if (relocsOnly)
            {
                return new ObjectData(Array.Empty<byte>(), Array.Empty<Relocation>(), 1, Array.Empty<ISymbolDefinitionNode>());
            }

            NativeWriter hashtableWriter = new NativeWriter();

            Section hashtableSection = hashtableWriter.NewSection();
            VertexHashtable vertexHashtable = new VertexHashtable();
            hashtableSection.Place(vertexHashtable);

            Dictionary<byte[], BlobVertex> uniqueFixups = new Dictionary<byte[], BlobVertex>(ByteArrayComparer.Instance);
            Dictionary<byte[], BlobVertex> uniqueSignatures = new Dictionary<byte[], BlobVertex>(ByteArrayComparer.Instance);

            foreach (MethodWithGCInfo method in factory.EnumerateCompiledMethods(null, CompiledMethodCategory.Instantiated))
            {
                // Resumption stubs are discovered via READYTORUN_FIXUP_ResumptionStubEntryPoint fixups
                // on their parent async variant methods, so they do not need entries in the InstanceEntryPointTable.
                if (method.Method is AsyncResumptionStub)
                    continue;

                Debug.Assert(method.Method.HasInstantiation || method.Method.OwningType.HasInstantiation || method.Method.IsAsyncVariant() || method.Method is UnboxingStubMethod);

                int methodIndex = factory.RuntimeFunctionsTable.GetIndex(method);

                byte[] signature = BuildSignatureForMethod(method, factory);
                BlobVertex signatureBlob;
                if (!uniqueSignatures.TryGetValue(signature, out signatureBlob))
                {
                    signatureBlob = new BlobVertex(signature);
                    uniqueSignatures.Add(signature, signatureBlob);
                }

                byte[] fixup = method.GetFixupBlob(factory);
                BlobVertex fixupBlob = null;
                if (fixup != null && !uniqueFixups.TryGetValue(fixup, out fixupBlob))
                {
                    fixupBlob = new BlobVertex(fixup);
                    uniqueFixups.Add(fixup, fixupBlob);
                }

                EntryPointVertex entryPointVertex = new EntryPointWithBlobVertex((uint)methodIndex, fixupBlob, signatureBlob);
                hashtableSection.Place(entryPointVertex);

                // Shared-generic value-type unboxing thunks (GenericUnboxingThunk wrapped in a
                // MethodForInstantiatedType) compute their hash from the synthetic boxed owning type and the
                // "_Unbox"-suffixed name. That does NOT match the runtime's version-resilient hash of the
                // unboxing-stub MethodDesc (which hashes the real value type and the underlying method name),
                // so key the bucket by the underlying method instead. The runtime then finds this entry and
                // disambiguates it from the real method's entry (same bucket) via the unboxing signature flag.
                // The non-generic UnboxingStubMethod already matches via its own ComputeHashCode override, so
                // it is intentionally left untouched here.
                MethodDesc hashableMethod = method.Method;
                if (hashableMethod.Context is CompilerTypeSystemContext unboxingContext && unboxingContext.IsSpecialUnboxingThunk(hashableMethod))
                {
                    hashableMethod = hashableMethod.GetPrimaryMethodDesc();
                }
                vertexHashtable.Append(unchecked((uint)hashableMethod.GetHashCode()), entryPointVertex);
            }

            MemoryStream hashtableContent = new MemoryStream();
            hashtableWriter.Save(hashtableContent);
            return new ObjectData(
                data: hashtableContent.ToArray(),
                relocs: null,
                alignment: 8,
                definedSymbols: new ISymbolDefinitionNode[] { this });
        }

        public override int ClassCode => -348722540;
    }
}
