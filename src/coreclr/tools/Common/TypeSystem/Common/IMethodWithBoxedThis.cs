// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.TypeSystem
{
    /// <summary>
    /// Implemented by an unboxing-thunk <see cref="MethodDesc"/> whose compiled body receives
    /// <c>this</c> as a boxed object reference, even though the thunk's identity
    /// (<see cref="MethodDesc.OwningType"/>, <see cref="MethodDesc.Signature"/>,
    /// <see cref="MethodDesc.IsVirtual"/>, attributes, instantiation, ...) is transparently that of
    /// the underlying value-type method it wraps.
    ///
    /// The boxed-<c>this</c> type is reported to the JIT only at the class-reporting point
    /// (<c>getMethodClass</c>) while the thunk itself is being compiled, so that callers continue to
    /// see the transparent value-type method while the body sees a boxed receiver and can skip the
    /// <c>MethodTable*</c> to recover a byref to the value.
    /// </summary>
    public interface IMethodWithBoxedThis
    {
        /// <summary>
        /// The boxed-layout reference type that represents <c>this</c> inside the compiled thunk body.
        /// </summary>
        TypeDesc BoxedThisType { get; }

        /// <summary>
        /// The underlying value-type instance method the thunk dispatches to.
        /// </summary>
        MethodDesc UnboxedTargetMethod { get; }
    }
}
