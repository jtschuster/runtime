// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.ObjectWriter
{
    /// <summary>
    /// Wasm ABI constants shared between hand-emitted stub bodies and the Wasm object writer.
    /// </summary>
    internal static class WasmAbiConstants
    {
        // Imported mutable global holding the current shadow stack pointer.
        public const int StackPointerGlobalIndex = 0;

        // Imported constant global holding the image base address, used to turn a symbol RVA
        // into an absolute linear-memory address.
        public const int ImageBaseGlobalIndex = 1;
    }
}
