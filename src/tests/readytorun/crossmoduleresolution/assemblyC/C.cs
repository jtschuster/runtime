// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AssemblyC
{
    public class CType
    {
        public int Value => 3;
    }

    public class CClass
    {
        public static int StaticField = 50;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseOwnType() => new CType().Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseDType() => new AssemblyD.DType().Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CallDMethod() => AssemblyD.DClass.StaticMethod();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadDField() => AssemblyD.DClass.StaticField;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseNestedType() => new AssemblyD.Outer.Inner().GetValue();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string UseForwardedType() => AssemblyD.SomeForwardedType.Name;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseGenericWithDType()
        {
            var list = new List<AssemblyD.DType>();
            list.Add(new AssemblyD.DType());
            return list[0].Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UseCoreLibGeneric()
        {
            var list = new List<int> { 1, 2, 3 };
            return list.Count;
        }
    }

    public class CGeneric<T>
    {
        public T Value { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetCount() => 1;
    }

    public interface ICrossModule
    {
        int DoWork();
    }
}
