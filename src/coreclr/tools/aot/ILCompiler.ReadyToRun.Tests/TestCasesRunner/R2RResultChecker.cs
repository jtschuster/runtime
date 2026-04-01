// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILCompiler.Reflection.ReadyToRun;
using Internal.ReadyToRunConstants;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Static assertion helpers for validating R2R images via <see cref="ReadyToRunReader"/>.
/// Use these in <see cref="R2RTestCase.Validate"/> callbacks.
/// </summary>
internal static class R2RAssert
{
    /// <summary>
    /// Returns all methods (assembly methods + instance methods) from the reader.
    /// </summary>
    public static List<ReadyToRunMethod> GetAllMethods(ReadyToRunReader reader)
    {
        var methods = new List<ReadyToRunMethod>();
        foreach (var assembly in reader.ReadyToRunAssemblies)
            methods.AddRange(assembly.Methods);
        foreach (var instanceMethod in reader.InstanceMethods)
            methods.Add(instanceMethod.Method);

        return methods;
    }

    /// <summary>
    /// Asserts the R2R image contains a manifest or MSIL assembly reference with the given name.
    /// </summary>
    public static void HasManifestRef(ReadyToRunReader reader, string assemblyName)
    {
        var allRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var globalMetadata = reader.GetGlobalMetadata();
        var mdReader = globalMetadata.MetadataReader;
        foreach (var handle in mdReader.AssemblyReferences)
        {
            var assemblyRef = mdReader.GetAssemblyReference(handle);
            allRefs.Add(mdReader.GetString(assemblyRef.Name));
        }

        foreach (var kvp in reader.ManifestReferenceAssemblies)
            allRefs.Add(kvp.Key);

        Assert.True(allRefs.Contains(assemblyName),
            $"Expected assembly reference '{assemblyName}' not found. " +
            $"Found: [{string.Join(", ", allRefs.OrderBy(s => s))}]");
    }

    /// <summary>
    /// Asserts the R2R image contains a CHECK_IL_BODY fixup whose signature contains the given method name.
    /// </summary>
    public static void HasInlinedMethod(ReadyToRunReader reader, string methodName)
    {
        var formattingOptions = new SignatureFormattingOptions();
        var checkIlBodySigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in GetAllMethods(reader))
        {
            if (method.Fixups is null)
                continue;
            foreach (var cell in method.Fixups)
            {
                if (cell.Signature?.FixupKind is ReadyToRunFixupKind.Check_IL_Body or ReadyToRunFixupKind.Verify_IL_Body)
                    checkIlBodySigs.Add(cell.Signature.ToString(formattingOptions));
            }
        }

        Assert.True(
            checkIlBodySigs.Any(s => s.Contains(methodName, StringComparison.OrdinalIgnoreCase)),
            $"Expected CHECK_IL_BODY fixup for '{methodName}' not found. " +
            $"CHECK_IL_BODY fixups: [{string.Join(", ", checkIlBodySigs)}]");
    }

    /// <summary>
    /// Asserts the R2R image contains an [ASYNC] variant entry whose signature contains the given method name.
    /// </summary>
    public static void HasAsyncVariant(ReadyToRunReader reader, string methodName)
    {
        var asyncSigs = GetAllMethods(reader)
            .Where(m => m.SignatureString.Contains("[ASYNC]", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SignatureString)
            .ToList();

        Assert.True(
            asyncSigs.Any(s => s.Contains(methodName, StringComparison.OrdinalIgnoreCase)),
            $"Expected [ASYNC] variant for '{methodName}' not found. " +
            $"Async methods: [{string.Join(", ", asyncSigs)}]");
    }

    /// <summary>
    /// Asserts the R2R image contains a [RESUME] stub entry whose signature contains the given method name.
    /// </summary>
    public static void HasResumptionStub(ReadyToRunReader reader, string methodName)
    {
        var resumeSigs = GetAllMethods(reader)
            .Where(m => m.SignatureString.Contains("[RESUME]", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.SignatureString)
            .ToList();

        Assert.True(
            resumeSigs.Any(s => s.Contains(methodName, StringComparison.OrdinalIgnoreCase)),
            $"Expected [RESUME] stub for '{methodName}' not found. " +
            $"Resume methods: [{string.Join(", ", resumeSigs)}]");
    }

    /// <summary>
    /// Asserts the R2R image contains at least one ContinuationLayout fixup.
    /// </summary>
    public static void HasContinuationLayout(ReadyToRunReader reader)
    {
        HasFixupKind(reader, ReadyToRunFixupKind.ContinuationLayout);
    }

    /// <summary>
    /// Asserts the R2R image contains at least one ResumptionStubEntryPoint fixup.
    /// </summary>
    public static void HasResumptionStubFixup(ReadyToRunReader reader)
    {
        HasFixupKind(reader, ReadyToRunFixupKind.ResumptionStubEntryPoint);
    }

    /// <summary>
    /// Asserts the R2R image contains at least one fixup of the given kind.
    /// </summary>
    public static void HasFixupKind(ReadyToRunReader reader, ReadyToRunFixupKind kind)
    {
        var presentKinds = new HashSet<ReadyToRunFixupKind>();
        foreach (var method in GetAllMethods(reader))
        {
            if (method.Fixups is null)
                continue;
            foreach (var cell in method.Fixups)
            {
                if (cell.Signature is not null)
                    presentKinds.Add(cell.Signature.FixupKind);
            }
        }

        Assert.True(presentKinds.Contains(kind),
            $"Expected fixup kind '{kind}' not found. " +
            $"Present kinds: [{string.Join(", ", presentKinds)}]");
    }
}

/// <summary>
/// Simple assembly resolver that looks in the same directory as the input image.
/// </summary>
internal sealed class SimpleAssemblyResolver : IAssemblyResolver
{
    public IAssemblyMetadata? FindAssembly(MetadataReader metadataReader, AssemblyReferenceHandle assemblyReferenceHandle, string parentFile)
    {
        var assemblyRef = metadataReader.GetAssemblyReference(assemblyReferenceHandle);
        string name = metadataReader.GetString(assemblyRef.Name);

        return FindAssembly(name, parentFile);
    }

    public IAssemblyMetadata? FindAssembly(string simpleName, string parentFile)
    {
        string? dir = Path.GetDirectoryName(parentFile);
        if (dir is null)
            return null;

        string candidate = Path.Combine(dir, simpleName + ".dll");
        if (!File.Exists(candidate))
            candidate = Path.Combine(TestPaths.RuntimePackDir, simpleName + ".dll");

        if (!File.Exists(candidate))
            return null;

        return new SimpleAssemblyMetadata(candidate);
    }
}

/// <summary>
/// Simple assembly metadata wrapper.
/// </summary>
internal sealed class SimpleAssemblyMetadata : IAssemblyMetadata, IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _peReader;

    public SimpleAssemblyMetadata(string path)
    {
        _stream = File.OpenRead(path);
        _peReader = new PEReader(_stream);
    }

    public PEReader ImageReader => _peReader;

    public MetadataReader MetadataReader => _peReader.GetMetadataReader();

    public void Dispose()
    {
        _peReader.Dispose();
        _stream.Dispose();
    }
}
