// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;


namespace ILCompiler.Reflection.ReadyToRun;

public partial class ReadyToRunReader
{
    private readonly Dictionary<UnwindInfoHandle, BaseUnwindInfo> _unwindInfoCache = new();

    /// <summary>
    /// Resolve an <see cref="UnwindInfoHandle"/> to its parsed unwind information.
    /// Returns null if the handle cannot be decoded for the current architecture.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: per-RuntimeFunction unwind data attached to <c>MethodWithGCInfo</c>
    /// (for x64/arm64 this is the PE <c>.pdata</c>/<c>xdata</c> referenced by <c>RuntimeFunctionsTableNode</c> entries).
    /// </remarks>
    public BaseUnwindInfo GetUnwindInfo(UnwindInfoHandle handle)
    {
        if (_unwindInfoCache.TryGetValue(handle, out BaseUnwindInfo cached))
            return cached;

        BaseUnwindInfo result;
        try
        {
            int unwindOffset = GetOffsetForRVA((int)handle);
            result = Machine switch
            {
                Machine.I386 => new x86.UnwindInfo(ImageReader, unwindOffset),
                Machine.Amd64 => new Amd64.UnwindInfo(ImageReader, unwindOffset),
                Machine.ArmThumb2 => new Arm.UnwindInfo(ImageReader, unwindOffset),
                Machine.Arm64 => new Arm64.UnwindInfo(ImageReader, unwindOffset),
                _ => null,
            };
        }
        catch
        {
            result = null;
        }

        _unwindInfoCache[handle] = result;
        return result;
    }
}
