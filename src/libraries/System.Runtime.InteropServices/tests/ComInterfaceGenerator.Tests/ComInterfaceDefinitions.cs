// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Xunit;

namespace ComInterfaceGenerator.Tests
{

    [GeneratedComInterface]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2c3f9903-b586-46b1-881b-adfce9af47b1")]
    public partial interface IComInterface
    {
        void Method1();
        int Method2();
    }

    public class ImplIComInterface : IComInterface
    {
        int _data = 0;
        public void Method1() => _data = 2;
        public int Method2() => _data;
    }
internal sealed partial class MyGeneratedComWrappers
    {
    }


    public class Tests
    {
#pragma warning disable xUnit1004 // Test methods should not be skipped
        //[Fact(Skip = "Not Implemented Yet")]
#pragma warning restore xUnit1004 // Test methods should not be skipped
        [Fact]
        public unsafe void UseGeneratedComInterface()
        {
            var ob = new[] { new ImplIComInterface()};
            var vtable = asdf<IComInterface>();
#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
            fixed (void* ptr = &ob[0])
            {
                var data = ((delegate*<void*, int>)vtable[4])(ptr);
                ((delegate*<void*, void>)vtable[3])(ptr);
                var data2 = ((delegate*<void*, int>)vtable[4])(ptr);
            }
#pragma warning restore CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
            //throw new NotImplementedException("Not Implemented");
        }

        internal unsafe void** asdf<T>() where T: IIUnknownInterfaceType
        {
            return T.ManagedVirtualMethodTable;
        }
    }
}

