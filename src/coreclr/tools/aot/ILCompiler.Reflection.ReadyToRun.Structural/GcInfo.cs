// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.PortableExecutable;


namespace ILCompiler.Reflection.ReadyToRun;

public partial class ReadyToRunReader
{
    private readonly Dictionary<UnwindInfoHandle, BaseGcInfo> _gcInfoCache = new();

    /// <summary>
    /// Resolve GC info for a runtime function identified by its <see cref="UnwindInfoHandle"/>.
    /// GC info sits immediately after the unwind info in the image:
    /// - On I386: GcInfo offset == UnwindInfo offset (same location).
    /// - On other architectures: GcInfo offset == UnwindInfo offset + UnwindInfo.Size.
    /// </summary>
    /// <remarks>
    /// Crossgen2 emitter: per-method GC info blob attached to <c>MethodWithGCInfo</c> and aggregated by <c>RuntimeFunctionsGCInfoNode</c>.
    /// </remarks>
    public BaseGcInfo GetGcInfo(UnwindInfoHandle handle)
    {
        if (_gcInfoCache.TryGetValue(handle, out BaseGcInfo cached))
            return cached;

        BaseGcInfo result;
        try
        {
            int gcInfoRva;
            if (Machine == Machine.I386)
            {
                gcInfoRva = (int)handle;
            }
            else
            {
                var unwindInfo = GetUnwindInfo(handle);
                if (unwindInfo is null)
                {
                    _gcInfoCache[handle] = null;
                    return null;
                }
                gcInfoRva = (int)handle + unwindInfo.Size;
            }

            int gcInfoOffset = GetOffsetForRVA(gcInfoRva);

            if (Machine == Machine.I386)
            {
                result = new x86.GcInfo(ImageReader, gcInfoOffset);
            }
            else
            {
                result = new Amd64.GcInfo(
                    ImageReader,
                    gcInfoOffset,
                    Machine,
                    ReadyToRunHeader.MajorVersion,
                    ReadyToRunHeader.MinorVersion);
            }
        }
        catch
        {
            result = null;
        }

        _gcInfoCache[handle] = result;
        return result;
    }
}
