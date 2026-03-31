// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AssemblyD
{
    public class DType
    {
        public int Value => 42;
    }

    public class DClass
    {
        public static int StaticField = 100;

        public static int StaticMethod() => StaticField + 1;
    }

    public class Outer
    {
        public class Inner
        {
            public int GetValue() => 99;
        }
    }

    public class SomeForwardedType
    {
        public static string Name => "forwarded";
    }
}
