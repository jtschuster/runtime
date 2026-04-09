// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace ILCompiler.Reflection.ReadyToRun.Structural;

/// <summary>
/// A decoded signature indirection table: a flat array of <see cref="SignatureHandle"/> values,
/// one per import slot in the owning import section.
/// Obtained via <see cref="ReadyToRunReader.GetSignatureTable(SignatureTableHandle, int)"/>.
/// </summary>
public sealed class SignatureTable
{
    /// <summary>Per-slot signature handles. Index corresponds to slot index in the import section.</summary>
    public IReadOnlyList<SignatureHandle> Entries { get; }

    internal SignatureTable(IReadOnlyList<SignatureHandle> entries)
    {
        Entries = entries;
    }
}

public partial class ReadyToRunReader
{
    private Dictionary<SignatureTableHandle, SignatureTable> _signatureTableCache;

    /// <summary>
    /// Decode a signature indirection table into per-slot <see cref="SignatureHandle"/> values.
    /// </summary>
    /// <param name="handle">Handle to the signature table (from <see cref="ImportSectionEntry.SignatureTableRva"/>).</param>
    /// <param name="entryCount">Number of slots in the owning import section.</param>
    public SignatureTable GetSignatureTable(SignatureTableHandle handle, int entryCount)
    {
        if ((int)handle == 0 || entryCount <= 0)
            return null;

        _signatureTableCache ??= new Dictionary<SignatureTableHandle, SignatureTable>();

        if (_signatureTableCache.TryGetValue(handle, out SignatureTable cached))
            return cached;

        int tableOffset = GetOffset((int)handle);
        var entries = new SignatureHandle[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            int slotOffset = tableOffset + i * sizeof(int);
            int sigRva = (int)_imageReader.ReadUInt32(ref slotOffset);
            entries[i] = (SignatureHandle)sigRva;
        }

        var table = new SignatureTable(entries);
        _signatureTableCache[handle] = table;
        return table;
    }
}
