// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SharedTypes.ComInterfaces
{
    [GeneratedComInterface(), Guid("0A52B77C-E08B-4274-A1F4-1A2BF2C07E60")]
    internal partial interface IStatelessCollectionStatelessGuaranteedUnmarshallingElementStatelessElement
    {
        void Method(
            [MarshalUsing(CountElementName = nameof(size))] StatelessCollectionStatelessGuaranteedUnmarshallingElement<StatelessFinallyType> p,
            int size);

        void MethodIn(
            [MarshalUsing(CountElementName = nameof(size))] in StatelessCollectionStatelessGuaranteedUnmarshallingElement<StatelessFinallyType> pIn,
            in int size);

        void MethodRef(
            [MarshalUsing(CountElementName = nameof(size))] ref StatelessCollectionStatelessGuaranteedUnmarshallingElement<StatelessFinallyType> pRef,
            int size);

        void MethodOut(
            [MarshalUsing(CountElementName = nameof(size))] out StatelessCollectionStatelessGuaranteedUnmarshallingElement<StatelessFinallyType> pOut,
            out int size);

        [return: MarshalUsing(CountElementName = nameof(size))]
        StatelessCollectionStatelessGuaranteedUnmarshallingElement<StatelessFinallyType> Return(int size);
    }

    [NativeMarshalling(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>))]
    internal class StatelessCollectionStatelessGuaranteedUnmarshallingElement<T>
    {
    }

    [ContiguousCollectionMarshaller]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ManagedToUnmanagedIn, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.ManagedToUnmanaged))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.UnmanagedToManagedOut, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.ManagedToUnmanaged))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ManagedToUnmanagedOut, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.UnmanagedToManaged))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.UnmanagedToManagedIn, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.UnmanagedToManaged))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.UnmanagedToManagedRef, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ManagedToUnmanagedRef, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ElementIn, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ElementOut, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElement<>), MarshalMode.ElementRef, typeof(StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<,>.Bidirectional))]
    internal static unsafe class StatelessCollectionStatelessGuaranteedUnmarshallingElementMarshaller<T, TUnmanagedElement> where TUnmanagedElement : unmanaged
    {
        internal static class Bidirectional
        {
            public static NativeCollection<TUnmanagedElement> AllocateContainerForUnmanagedElements(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed, out int numElements)
            {
                throw new NotImplementedException();
            }

            public static StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> AllocateContainerForManagedElements(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<T> GetManagedValuesSource(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed)
            {
                throw new NotImplementedException();
            }

            public static Span<TUnmanagedElement> GetUnmanagedValuesDestination(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<TUnmanagedElement> GetUnmanagedValuesSource(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static Span<T> GetManagedValuesDestination(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed)
            {
                throw new NotImplementedException();
            }

            public static void Free(NativeCollection<TUnmanagedElement> unmanaged) { }
        }

        internal static class ManagedToUnmanaged
        {
            public static NativeCollection<TUnmanagedElement> AllocateContainerForUnmanagedElements(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed, out int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<T> GetManagedValuesSource(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed)
            {
                throw new NotImplementedException();
            }

            public static Span<TUnmanagedElement> GetUnmanagedValuesDestination(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static void Free(NativeCollection<TUnmanagedElement> unmanaged) => throw new NotImplementedException();
        }

        internal static class UnmanagedToManaged
        {
            public static StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> AllocateContainerForManagedElements(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<TUnmanagedElement> GetUnmanagedValuesSource(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static Span<T> GetManagedValuesDestination(StatelessCollectionStatelessGuaranteedUnmarshallingElement<T> managed)
            {
                throw new NotImplementedException();
            }

            public static void Free(NativeCollection<TUnmanagedElement> unmanaged) => throw new NotImplementedException();
        }
    }
}
