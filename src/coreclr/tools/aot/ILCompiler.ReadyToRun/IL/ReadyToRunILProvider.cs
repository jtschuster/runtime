// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using ILCompiler;

using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using Internal.IL.Stubs;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Internal.JitInterface;
using System.Text;
using System.Text.Unicode;
using ILCompiler.DependencyAnalysis.ReadyToRun;

namespace Internal.IL
{
    /// <summary>
    /// Marker interface that promises that all tokens from this MethodIL are useable in the current compilation
    /// </summary>
    public interface IMethodTokensAreUseableInCompilation { }


    public sealed class ReadyToRunILProvider : ILProvider
    {
        private CompilationModuleGroup _compilationModuleGroup;
        private MutableModule _manifestMutableModule;
        private int _version = 0;

        public ReadyToRunILProvider(CompilationModuleGroup compilationModuleGroup)
        {
            _compilationModuleGroup = compilationModuleGroup;
        }

        public void InitManifestMutableModule(MutableModule module, ModuleTokenResolver tokenResolver)
        {
            _manifestMutableModule = module;
            EnsureR2RKnownMethodsAreInManifestModule(tokenResolver);
        }

        void IncrementVersion()
        {
            _version++;
        }

        public int Version => _version;

        private MethodIL TryGetIntrinsicMethodILForActivator(MethodDesc method)
        {
            if (method.Instantiation.Length == 1
                && method.Signature.Length == 0
                && method.Name.SequenceEqual("CreateInstance"u8))
            {
                TypeDesc type = method.Instantiation[0];
                if (type.IsValueType && type.GetParameterlessConstructor() == null)
                {
                    // Replace the body with implementation that just returns "default"
                    MethodDesc createDefaultInstance = method.OwningType.GetKnownMethod("CreateDefaultInstance"u8, method.GetTypicalMethodDefinition().Signature);
                    return GetMethodIL(createDefaultInstance.MakeInstantiatedMethod(type));
                }
            }

            return null;
        }

        /// <summary>
        /// Provides method bodies for intrinsics recognized by the compiler.
        /// It can return null if it's not an intrinsic recognized by the compiler,
        /// but an intrinsic e.g. recognized by codegen.
        /// </summary>
        private MethodIL TryGetIntrinsicMethodIL(MethodDesc method)
        {
            var mdType = method.OwningType as MetadataType;
            if (mdType == null)
                return null;

            if (mdType.Name.SequenceEqual("RuntimeHelpers"u8) && mdType.Namespace.SequenceEqual("System.Runtime.CompilerServices"u8))
            {
                return RuntimeHelpersIntrinsics.EmitIL(method);
            }

            if (mdType.Name.SequenceEqual("Unsafe"u8) && mdType.Namespace.SequenceEqual("System.Runtime.CompilerServices"u8))
            {
                return UnsafeIntrinsics.EmitIL(method);
            }

            if (mdType.Name.SequenceEqual("InstanceCalliHelper"u8) && mdType.Namespace.SequenceEqual("System.Reflection"u8))
            {
                return InstanceCalliHelperIntrinsics.EmitIL(method);
            }

            return null;
        }

        /// <summary>
        /// Provides method bodies for intrinsics recognized by the compiler that
        /// are specialized per instantiation. It can return null if the intrinsic
        /// is not recognized.
        /// </summary>
        private MethodIL TryGetPerInstantiationIntrinsicMethodIL(MethodDesc method)
        {
            var mdType = method.OwningType as MetadataType;
            if (mdType == null)
                return null;

            if (mdType.Name.SequenceEqual("RuntimeHelpers"u8) && mdType.Namespace.SequenceEqual("System.Runtime.CompilerServices"u8))
            {
                return RuntimeHelpersIntrinsics.EmitIL(method);
            }

            if (mdType.Name.SequenceEqual("Activator"u8) && mdType.Namespace.SequenceEqual("System"u8))
            {
                return TryGetIntrinsicMethodILForActivator(method);
            }

            if (mdType.Name.SequenceEqual("Interlocked"u8) && mdType.Namespace.SequenceEqual("System.Threading"u8))
            {
                return InterlockedIntrinsics.EmitIL(_compilationModuleGroup, method);
            }

            return null;
        }

        private Dictionary<EcmaMethod, MethodIL> _manifestModuleWrappedMethods = new Dictionary<EcmaMethod, MethodIL>();

        // Create the cross module inlineable tokens for a method
        // This method is order dependent, and must be called during the single threaded portion of compilation
        public void CreateCrossModuleInlineableTokensForILBody(EcmaMethod method)
        {
            Debug.Assert(_manifestMutableModule != null);
            Debug.Assert(!_compilationModuleGroup.VersionsWithMethodBody(method) &&
                    _compilationModuleGroup.CrossModuleInlineable(method));
            var wrappedMethodIL = new ManifestModuleWrappedMethodIL();
            if (!wrappedMethodIL.Initialize(_manifestMutableModule, EcmaMethodIL.Create(method)))
            {
                // If we could not initialize the wrapped method IL, we should store a null.
                // That will result in the IL code for the method being unavailable for use in
                // the compilation, which is version safe.
                wrappedMethodIL = null;
            }
            _manifestModuleWrappedMethods.Add(method, wrappedMethodIL);
            IncrementVersion();
        }

        public bool NeedsCrossModuleInlineableTokens(EcmaMethod method)
        {
            if (!_compilationModuleGroup.VersionsWithMethodBody(method) &&
                    _compilationModuleGroup.CrossModuleInlineable(method) &&
                    !_manifestModuleWrappedMethods.ContainsKey(method))
            {
                return true;
            }
            return false;
        }

        public override MethodIL GetMethodIL(MethodDesc method)
        {
            if (method is EcmaMethod ecmaMethod)
            {
                if (method.IsIntrinsic)
                {
                    MethodIL result = TryGetIntrinsicMethodIL(method);
                    if (result != null)
                        return result;
                }

                if (method.IsAsync)
                {
                    // AsyncCallConv methods should be AsyncMethodDesc, not EcmaMethod
                    // Generate IL for Task wrapping stub
                    MethodIL result = TryGetTaskReturningAsyncWrapperThunkIL(method);
                    return result;
                }

                // Check to see if there is an override for the EcmaMethodIL. If there is not
                // then simply return the EcmaMethodIL. In theory this could call
                // CreateCrossModuleInlineableTokensForILBody, but we explicitly do not want
                // to do that. The reason is that this method is called during the multithreaded
                // portion of compilation, and CreateCrossModuleInlineableTokensForILBody
                // will produce tokens which are order dependent thus violating the determinism
                // principles of the compiler.
                if (!_manifestModuleWrappedMethods.TryGetValue(ecmaMethod, out var methodIL))
                {
                    methodIL = EcmaMethodIL.Create(ecmaMethod);
                }

                if (methodIL != null)
                    return methodIL;

                return null;
            }
            else if (method is AsyncMethodDesc asyncMethod)
            {
                if (asyncMethod.Target is EcmaMethod target)
                {
                    var methodIL = EcmaMethodIL.Create(target);
                    return methodIL;
                }
                return null;
            }
            else if (method is MethodForInstantiatedType || method is InstantiatedMethod)
            {
                // Intrinsics specialized per instantiation
                if (method.IsIntrinsic)
                {
                    MethodIL methodIL = TryGetPerInstantiationIntrinsicMethodIL(method);
                    if (methodIL != null)
                        return methodIL;
                }

                var methodDefinitionIL = GetMethodIL(method.GetTypicalMethodDefinition());
                if (methodDefinitionIL == null)
                    return null;
                return new InstantiatedMethodIL(method, methodDefinitionIL);
            }
            else
            {
                return null;
            }
        }

        public enum R2RKnownType
        {
            ExecutionAndSyncBlockStore,
            SystemException,
        }

        public enum R2RKnownMethod
        {
            ExecutionAndSyncBlockStore_Push,
            ExecutionAndSyncBlockStore_Pop,
            ValueTask_FromResult,
            Task_FromResult,
            ValueTask_get_CompletedTask,
            Task_get_CompletedTask,
            StubHelpers_AsyncCallContinuation,
            AsyncHelpers_TaskFromException,
            AsyncHelpers_ValueTaskFromException,
            AsyncHelpers_TaskFromExceptionGeneric,
            AsyncHelpers_ValueTaskFromExceptionGeneric,
            AsyncHelpers_FinalizeTaskReturningThunk,
            AsyncHelpers_FinalizeValueTaskReturningThunk,
            AsyncHelpers_FinalizeTaskReturningThunkGeneric,
            AsyncHelpers_FinalizeValueTaskReturningThunkGeneric,
        }

        public MetadataType GetR2RKnownType(R2RKnownType knownType)
        {
            switch (knownType)
            {
                case R2RKnownType.ExecutionAndSyncBlockStore:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "ExecutionAndSyncBlockStore"u8);
                case R2RKnownType.SystemException:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System"u8, "SystemException"u8);
                default:
                    throw new InvalidOperationException($"Unknown R2R known type: {knownType}");
            }
        }

        public MethodDesc GetR2RKnownMethod(R2RKnownMethod knownMethod)
        {
            TypeSystemContext context = _manifestMutableModule.Context;
            switch (knownMethod)
            {
                case R2RKnownMethod.ExecutionAndSyncBlockStore_Push:
                    return GetR2RKnownType(R2RKnownType.ExecutionAndSyncBlockStore).GetKnownMethod("Push"u8, null);
                case R2RKnownMethod.ExecutionAndSyncBlockStore_Pop:
                    return GetR2RKnownType(R2RKnownType.ExecutionAndSyncBlockStore).GetKnownMethod("Pop"u8, null);
                case R2RKnownMethod.ValueTask_FromResult:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask"u8).GetKnownMethod("FromResult"u8, null);
                case R2RKnownMethod.Task_FromResult:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8).GetKnownMethod("FromResult"u8, null);
                case R2RKnownMethod.ValueTask_get_CompletedTask:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask"u8).GetKnownMethod("get_CompletedTask"u8, null);
                case R2RKnownMethod.Task_get_CompletedTask:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8).GetKnownMethod("get_CompletedTask"u8, null);
                case R2RKnownMethod.StubHelpers_AsyncCallContinuation:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.StubHelpers"u8, "StubHelpers"u8).GetKnownMethod("AsyncCallContinuation"u8, null);
                case R2RKnownMethod.AsyncHelpers_TaskFromException:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("TaskFromException"u8, new MethodSignature(MethodSignatureFlags.Static, 0, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8), new TypeDesc[] { context.SystemModule.GetKnownType("System"u8, "Exception"u8) }));
                case R2RKnownMethod.AsyncHelpers_ValueTaskFromException:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("ValueTaskFromException"u8, new MethodSignature(MethodSignatureFlags.Static, 0, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask"u8), new TypeDesc[] { context.SystemModule.GetKnownType("System"u8, "Exception"u8) }));
                case R2RKnownMethod.AsyncHelpers_TaskFromExceptionGeneric:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("TaskFromException"u8, new MethodSignature(MethodSignatureFlags.Static, 1, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task`1"u8).MakeInstantiatedType(context.GetSignatureVariable(0, method: true)), new TypeDesc[] { context.SystemModule.GetKnownType("System"u8, "Exception"u8) }));
                case R2RKnownMethod.AsyncHelpers_ValueTaskFromExceptionGeneric:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("ValueTaskFromException"u8, new MethodSignature(MethodSignatureFlags.Static, 1, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask`1"u8).MakeInstantiatedType(context.GetSignatureVariable(0, method: true)), new TypeDesc[] { context.SystemModule.GetKnownType("System"u8, "Exception"u8) }));
                case R2RKnownMethod.AsyncHelpers_FinalizeTaskReturningThunk:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("FinalizeTaskReturningThunk"u8, new MethodSignature(MethodSignatureFlags.Static, 0, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task"u8), Array.Empty<TypeDesc>()));
                case R2RKnownMethod.AsyncHelpers_FinalizeValueTaskReturningThunk:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("FinalizeValueTaskReturningThunk"u8, new MethodSignature(MethodSignatureFlags.Static, 0, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask"u8), Array.Empty<TypeDesc>()));
                case R2RKnownMethod.AsyncHelpers_FinalizeTaskReturningThunkGeneric:
                    return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("FinalizeTaskReturningThunk"u8, new MethodSignature(MethodSignatureFlags.Static, 1, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "Task`1"u8).MakeInstantiatedType(context.GetSignatureVariable(0, method: true)), Array.Empty<TypeDesc>()));
                case R2RKnownMethod.AsyncHelpers_FinalizeValueTaskReturningThunkGeneric:
                        return _manifestMutableModule.Context.SystemModule.GetKnownType("System.Runtime.CompilerServices"u8, "AsyncHelpers"u8).GetKnownMethod("FinalizeValueTaskReturningThunk"u8, new MethodSignature(MethodSignatureFlags.Static, 1, context.SystemModule.GetKnownType("System.Threading.Tasks"u8, "ValueTask`1"u8).MakeInstantiatedType(context.GetSignatureVariable(0, method: true)), Array.Empty<TypeDesc>()));

                default:
                    throw new InvalidOperationException($"Unknown R2R known method: {knownMethod}");
            }
        }

        public void EnsureR2RKnownMethodsAreInManifestModule(ModuleTokenResolver tokenResolver)
        {
            try
            {
                Debug.Assert(_manifestMutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences == null);
                _manifestMutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences = _manifestMutableModule.Context.SystemModule;
                _manifestMutableModule.AddingReferencesToR2RKnownTypesAndMethods = true;

                // Force all known R2R methods to be present in the manifest module
                foreach (R2RKnownMethod knownMethod in Enum.GetValues(typeof(R2RKnownMethod)))
                {
                    MethodDesc method = GetR2RKnownMethod(knownMethod);
                    if (!_compilationModuleGroup.VersionsWithMethodBody(method))
                    {
                        tokenResolver.AddModuleTokenForMethod(method, new ModuleToken(_manifestMutableModule, _manifestMutableModule.TryGetEntityHandle(method).Value));

                        // This is effectively an assert
                        tokenResolver.GetModuleTokenForMethod(method, allowDynamicallyCreatedReference: true, throwIfNotFound: true);
                    }
                }
                // Force all known R2R types to be present in the manifest module
                foreach (R2RKnownType knownType in Enum.GetValues(typeof(R2RKnownType)))
                {
                    TypeDesc type = GetR2RKnownType(knownType);
                    if (!_compilationModuleGroup.VersionsWithType(type))
                    {
                        tokenResolver.AddModuleTokenForType((EcmaType)type, new ModuleToken(_manifestMutableModule, _manifestMutableModule.TryGetEntityHandle(type).Value));
                        tokenResolver.GetModuleTokenForType((EcmaType)type, allowDynamicallyCreatedReference: true, throwIfNotFound: true);
                    }
                }
            }
            finally
            {
                _manifestMutableModule.AddingReferencesToR2RKnownTypesAndMethods = false;
                _manifestMutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences = null;
            }
        }

        // Emits roughly the following code:
        //
        // ExecutionAndSyncBlockStore store = default;
        // store.Push();
        // try
        // {
        //   try
        //   {
        //     T result = Inner(args);
        //     // call an intrisic to see if the call above produced a continuation
        //     if (StubHelpers.AsyncCallContinuation() == null)
        //       return Task.FromResult(result);
        //
        //     return FinalizeTaskReturningThunk();
        //   }
        //   catch (Exception ex)
        //   {
        //     return TaskFromException(ex);
        //   }
        // }
        // finally
        // {
        //   store.Pop();
        // }
        private MethodIL TryGetTaskReturningAsyncWrapperThunkIL(MethodDesc method)
        {
            ILEmitter ilEmitter = new ILEmitter();
            ILCodeStream il = ilEmitter.NewCodeStream();
            TypeDesc retType = method.Signature.ReturnType;
            IL.Stubs.ILLocalVariable returnTaskLocal = ilEmitter.NewLocal(retType);
            bool isValueTask = retType.IsValueType;
            TypeDesc logicalResultType = null;
            IL.Stubs.ILLocalVariable logicalResultLocal = default;
            if (retType.HasInstantiation)
            {
                logicalResultType = retType.Instantiation[0];
                logicalResultLocal = ilEmitter.NewLocal(logicalResultType);
            }
            var executionAndSyncBlockStoreType = GetR2RKnownType(R2RKnownType.ExecutionAndSyncBlockStore);
            var executionAndSyncBlockStoreLocal = ilEmitter.NewLocal(executionAndSyncBlockStoreType);

            ILCodeLabel returnTaskLabel = ilEmitter.NewCodeLabel();
            ILCodeLabel suspendedLabel = ilEmitter.NewCodeLabel();
            ILCodeLabel finishedLabel = ilEmitter.NewCodeLabel();

            // store.Push()
            il.EmitLdLoca(executionAndSyncBlockStoreLocal);
            il.Emit(ILOpcode.call, ilEmitter.NewToken(GetR2RKnownMethod(R2RKnownMethod.ExecutionAndSyncBlockStore_Push)));

            // Inner try block must appear first in metadata
            var exceptionType = ilEmitter.NewToken(GetR2RKnownType(R2RKnownType.SystemException));
            ILExceptionRegionBuilder innerTryRegion = ilEmitter.NewCatchRegion(exceptionType);
            // Outer try block
            ILExceptionRegionBuilder outerTryRegion = ilEmitter.NewFinallyRegion();
            il.BeginTry(outerTryRegion);
            il.BeginTry(innerTryRegion);

            // Call the async variant method with arguments
            int argIndex = 0;
            if (!method.Signature.IsStatic)
            {
                il.EmitLdArg(argIndex++);
            }

            for (int i = 0; i < method.Signature.Length; i++)
            {
                il.EmitLdArg(argIndex++);
            }

            // Get the async call conv method and call it
            MethodDesc asyncOtherVariant = new AsyncMethodDesc(method, null);
            il.Emit(ILOpcode.call, ilEmitter.NewToken(asyncOtherVariant));

            // Store result if there is one
            if (logicalResultLocal != default)
            {
                il.EmitStLoc(logicalResultLocal);
            }

            il.Emit(ILOpcode.call, ilEmitter.NewToken(GetR2RKnownMethod(R2RKnownMethod.StubHelpers_AsyncCallContinuation)));
            il.Emit(ILOpcode.brfalse, finishedLabel);

            il.Emit(ILOpcode.leave, suspendedLabel);

            il.EmitLabel(finishedLabel);
            if (logicalResultLocal != default)
            {
                il.EmitLdLoc(logicalResultLocal);
                R2RKnownMethod fromResultValue = isValueTask ? R2RKnownMethod.ValueTask_FromResult : R2RKnownMethod.Task_FromResult;
                MethodDesc fromResultMethod = GetR2RKnownMethod(fromResultValue).MakeInstantiatedMethod(logicalResultType);
                il.Emit(ILOpcode.call, ilEmitter.NewToken(fromResultMethod));
            }
            else
            {
                MethodDesc completedTaskGetter;
                if (isValueTask)
                {
                    completedTaskGetter = GetR2RKnownMethod(R2RKnownMethod.ValueTask_get_CompletedTask);
                }
                else
                {
                    completedTaskGetter = GetR2RKnownMethod(R2RKnownMethod.Task_get_CompletedTask);
                }
                il.Emit(ILOpcode.call, ilEmitter.NewToken(completedTaskGetter));
            }
            il.EmitStLoc(returnTaskLocal);

            il.Emit(ILOpcode.leave, returnTaskLabel);
            il.EndTry(innerTryRegion);

            il.BeginHandler(innerTryRegion);

            MethodDesc fromExceptionMethod;
            R2RKnownMethod fromExceptionKnownMethod;
            if (isValueTask)
            {
                fromExceptionKnownMethod = logicalResultLocal != default ?
                    R2RKnownMethod.AsyncHelpers_ValueTaskFromExceptionGeneric :
                    R2RKnownMethod.AsyncHelpers_ValueTaskFromException;
            }
            else
            {
                fromExceptionKnownMethod = logicalResultLocal != default ?
                    R2RKnownMethod.AsyncHelpers_TaskFromExceptionGeneric :
                    R2RKnownMethod.AsyncHelpers_TaskFromException;
            }
            fromExceptionMethod = GetR2RKnownMethod(fromExceptionKnownMethod);
            if (logicalResultLocal != default)
            {
                fromExceptionMethod = fromExceptionMethod.MakeInstantiatedMethod(logicalResultType);
            }

            il.Emit(ILOpcode.call, ilEmitter.NewToken(fromExceptionMethod));
            il.EmitStLoc(returnTaskLocal);

            il.Emit(ILOpcode.leave, returnTaskLabel);
            il.EndHandler(innerTryRegion);

            il.EmitLabel(suspendedLabel);

            MethodDesc finalizeMethod;
            R2RKnownMethod finalizeKnownMethod;
            if (isValueTask)
            {
                finalizeKnownMethod = logicalResultLocal != default ?
                    R2RKnownMethod.AsyncHelpers_FinalizeValueTaskReturningThunkGeneric :
                    R2RKnownMethod.AsyncHelpers_FinalizeValueTaskReturningThunk;
            }
            else
            {
                finalizeKnownMethod = logicalResultLocal != default ?
                    R2RKnownMethod.AsyncHelpers_FinalizeTaskReturningThunkGeneric :
                    R2RKnownMethod.AsyncHelpers_FinalizeTaskReturningThunk;
            }

            finalizeMethod = GetR2RKnownMethod(finalizeKnownMethod);

            if (logicalResultLocal != default)
            {
                finalizeMethod = finalizeMethod.MakeInstantiatedMethod(logicalResultType);
            }

            il.Emit(ILOpcode.call, ilEmitter.NewToken(finalizeMethod));
            il.EmitStLoc(returnTaskLocal);

            il.Emit(ILOpcode.leave, returnTaskLabel);
            il.EndTry(outerTryRegion);

            // Finally block
            il.BeginHandler(outerTryRegion);
            il.EmitLdLoca(executionAndSyncBlockStoreLocal);
            il.Emit(ILOpcode.call, ilEmitter.NewToken(GetR2RKnownMethod(R2RKnownMethod.ExecutionAndSyncBlockStore_Pop)));
            il.Emit(ILOpcode.endfinally);
            il.EndHandler(outerTryRegion);

            // Return task label
            il.EmitLabel(returnTaskLabel);
            il.EmitLdLoc(returnTaskLocal);
            il.Emit(ILOpcode.ret);

            return ilEmitter.Link(method);
        }

        /// <summary>
        /// A MethodIL Provider which provides tokens relative to a MutableModule. Used to implement cross
        /// module inlining of code in ReadyToRun files.
        /// </summary>
        class ManifestModuleWrappedMethodIL : MethodIL, IEcmaMethodIL, IMethodTokensAreUseableInCompilation
        {
            int _maxStack;
            bool _isInitLocals;
            EcmaMethod _owningMethod;
            ILExceptionRegion[] _exceptionRegions;
            byte[] _ilBytes;
            LocalVariableDefinition[] _locals;

            MutableModule _mutableModule;

            public ManifestModuleWrappedMethodIL() { }

            public bool Initialize(MutableModule mutableModule, EcmaMethodIL wrappedMethod)
            {
                bool failedToReplaceToken = false;
                try
                {
                    Debug.Assert(mutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences == null);
                    mutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences = wrappedMethod.OwningMethod.Module;
                    var owningMethodHandle = mutableModule.TryGetEntityHandle(wrappedMethod.OwningMethod);
                    if (!owningMethodHandle.HasValue)
                        return false;
                    _mutableModule = mutableModule;
                    _maxStack = wrappedMethod.MaxStack;
                    _isInitLocals = wrappedMethod.IsInitLocals;
                    _owningMethod = wrappedMethod.OwningMethod;
                    _exceptionRegions = (ILExceptionRegion[])wrappedMethod.GetExceptionRegions().Clone();
                    _ilBytes = (byte[])wrappedMethod.GetILBytes().Clone();
                    _locals = (LocalVariableDefinition[])wrappedMethod.GetLocals();

                    for (int i = 0; i < _exceptionRegions.Length; i++)
                    {
                        var region = _exceptionRegions[i];
                        if (region.Kind == ILExceptionRegionKind.Catch)
                        {
                            var newHandle = _mutableModule.TryGetHandle((TypeSystemEntity)wrappedMethod.GetObject(region.ClassToken));
                            if (!newHandle.HasValue)
                            {
                                return false;
                            }
                            _exceptionRegions[i] = new ILExceptionRegion(region.Kind, region.TryOffset, region.TryLength, region.HandlerOffset, region.HandlerLength, newHandle.Value, newHandle.Value);
                        }
                    }

                    ILTokenReplacer.Replace(_ilBytes, GetMutableModuleToken);
#if DEBUG
                    Debug.Assert(ReadyToRunStandaloneMethodMetadata.Compute(_owningMethod) != null);
#endif // DEBUG
                }
                finally
                {
                    mutableModule.ModuleThatIsCurrentlyTheSourceOfNewReferences = null;
                }


                return !failedToReplaceToken;

                int GetMutableModuleToken(int token)
                {
                    object result = wrappedMethod.GetObject(token);
                    int? newToken;
                    if (result is string str)
                    {
                        newToken = mutableModule.TryGetStringHandle(str);
                    }
                    else
                    {
                        newToken = mutableModule.TryGetHandle((TypeSystemEntity)result);
                    }
                    if (!newToken.HasValue)
                    {
                        // Token replacement has failed. Do not attempt to use this IL.
                        failedToReplaceToken = true;
                        return 1;
                    }
                    return newToken.Value;
                }
            }

            public override int MaxStack => _maxStack;

            public override bool IsInitLocals => _isInitLocals;

            public override MethodDesc OwningMethod => _owningMethod;

            public IEcmaModule Module => _mutableModule;

            public override ILExceptionRegion[] GetExceptionRegions() => _exceptionRegions;
            public override byte[] GetILBytes() => _ilBytes;
            public override LocalVariableDefinition[] GetLocals() => _locals;
            public override object GetObject(int token, NotFoundBehavior notFoundBehavior = NotFoundBehavior.Throw)
            {
                // UserStrings cannot be wrapped in EntityHandle
                if ((token & 0xFF000000) == 0x70000000)
                    return _mutableModule.GetUserString(System.Reflection.Metadata.Ecma335.MetadataTokens.UserStringHandle(token));

                return _mutableModule.GetObject(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(token), notFoundBehavior);
            }
        }
    }
}
