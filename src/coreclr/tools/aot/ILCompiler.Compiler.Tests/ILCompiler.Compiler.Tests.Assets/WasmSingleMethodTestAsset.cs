// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

public static class WasmSingleMethodTestAsset
{
    public static int FirstValue;
    public static int SecondValue;

    public static int TestEntryPoint(int stackPointer, int context)
    {
        FirstValue = 23;
        SecondValue = 19;
        return (FirstValue * 10) + SecondValue;
    }
}
