// Test: Async method thunks in R2R
// Validates that runtime-async compiled methods produce the expected
// RuntimeFunction layout (thunk + async body + resumption stub).
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

public static class AsyncMethods
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> TestAsyncInline()
    {
        return await AsyncInlineableLib.GetValueAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<string> TestAsyncStringInline()
    {
        return await AsyncInlineableLib.GetStringAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int TestSyncFromAsyncLib()
    {
        return AsyncInlineableLib.GetValueSync();
    }
}
