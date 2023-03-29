// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SharedTypes.ComInterfaces;
using Xunit;
using Xunit.Sdk;

namespace SharedTypes.ComInterfaces
{
    [GeneratedComInterface]
    partial interface IGetAndSetInt;
}
namespace ComInterfaceGenerator.Tests
{
    [GeneratedComClass]
    public class Impl : IGetAndSetInt
    {
        public int GetData() => throw new System.NotImplementedException();
        public void SetData(int x) => throw new System.NotImplementedException();
    }

    internal unsafe partial class NativeExportsNE
    {
        [LibraryImport(NativeExportsNE_Binary, EntryPoint = "get_com_object")]
        public static partial void* NewNativeObject();
    }


    public class RcwTests
    {
        [Fact]
        public unsafe void CallRcwFromGeneratedComInterface()
        {
            var ptr = NativeExportsNE.NewNativeObject(); // new_native_object
            var cw = new StrategyBasedComWrappers();
            var obj = cw.GetOrCreateObjectForComInstance((nint)ptr, CreateObjectFlags.None);

            var intObj = (IGetAndSetInt)obj;
            Assert.Equal(0, intObj.GetData());
            intObj.SetData(2);
            Assert.Equal(2, intObj.GetData());
        }
    }
}
