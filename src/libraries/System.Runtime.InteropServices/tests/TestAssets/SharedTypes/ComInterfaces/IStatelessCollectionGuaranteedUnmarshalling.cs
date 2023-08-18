// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SharedTypes.ComInterfaces
{
    [GeneratedComInterface(), Guid("0A52B77C-E08B-4274-A1F4-1A2BF2C07E60")]
    internal partial interface IStatelessCollectionGuaranteedUnmarshallingStatelessElement
    {
        void Method(
            [MarshalUsing(CountElementName = nameof(size))] StatelessCollectionGuaranteedUnmarshalling<StatelessType> p,
            int size);

        void MethodIn(
            [MarshalUsing(CountElementName = nameof(size))] in StatelessCollectionGuaranteedUnmarshalling<StatelessType> pIn,
            in int size);

        void MethodRef(
            [MarshalUsing(CountElementName = nameof(size))] ref StatelessCollectionGuaranteedUnmarshalling<StatelessType> pRef,
            int size);

        void MethodOut(
            [MarshalUsing(CountElementName = nameof(size))] out StatelessCollectionGuaranteedUnmarshalling<StatelessType> pOut,
            out int size);

        [return: MarshalUsing(CountElementName = nameof(size))]
        StatelessCollectionGuaranteedUnmarshalling<StatelessType> Return(int size);
    }

    [NativeMarshalling(typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>))]
    internal class StatelessCollectionGuaranteedUnmarshalling<T>
    {
    }

    [ContiguousCollectionMarshaller]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ManagedToUnmanagedIn, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.ManagedToUnmanaged))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.UnmanagedToManagedOut, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.ManagedToUnmanaged))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ManagedToUnmanagedOut, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.UnmanagedToManaged))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.UnmanagedToManagedIn, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.UnmanagedToManaged))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.UnmanagedToManagedRef, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ManagedToUnmanagedRef, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ElementIn, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ElementOut, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.Bidirectional))]
    [CustomMarshaller(typeof(StatelessCollectionGuaranteedUnmarshalling<>), MarshalMode.ElementRef, typeof(StatelessCollectionGuaranteedUnmarshallingMarshaller<,>.Bidirectional))]
    internal static unsafe class StatelessCollectionGuaranteedUnmarshallingMarshaller<T, TUnmanagedElement> where TUnmanagedElement : unmanaged
    {
        internal static class Bidirectional
        {
            public static NativeCollection<TUnmanagedElement> AllocateContainerForUnmanagedElements(StatelessCollectionGuaranteedUnmarshalling<T> managed, out int numElements)
            {
                throw new NotImplementedException();
            }

            public static StatelessCollectionGuaranteedUnmarshalling<T> AllocateContainerForManagedElementsFinally(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<T> GetManagedValuesSource(StatelessCollectionGuaranteedUnmarshalling<T> managed)
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

            public static Span<T> GetManagedValuesDestination(StatelessCollectionGuaranteedUnmarshalling<T> managed)
            {
                throw new NotImplementedException();
            }

            public static void Free(NativeCollection<TUnmanagedElement> unmanaged) { }
        }

        internal static class ManagedToUnmanaged
        {
            public static NativeCollection<TUnmanagedElement> AllocateContainerForUnmanagedElements(StatelessCollectionGuaranteedUnmarshalling<T> managed, out int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<T> GetManagedValuesSource(StatelessCollectionGuaranteedUnmarshalling<T> managed)
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
            public static StatelessCollectionGuaranteedUnmarshalling<T> AllocateContainerForManagedElementsFinally(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static ReadOnlySpan<TUnmanagedElement> GetUnmanagedValuesSource(NativeCollection<TUnmanagedElement> unmanaged, int numElements)
            {
                throw new NotImplementedException();
            }

            public static Span<T> GetManagedValuesDestination(StatelessCollectionGuaranteedUnmarshalling<T> managed)
            {
                throw new NotImplementedException();
            }

            public static void Free(NativeCollection<TUnmanagedElement> unmanaged) => throw new NotImplementedException();
        }
    }
}
