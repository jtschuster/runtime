// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Microsoft.NET.Build.Tasks.Tests;

public sealed class Crossgen2TestTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "crossgen2-test-" + Guid.NewGuid().ToString("N"));
    private readonly Crossgen2Test _task;

    public Crossgen2TestTests()
    {
        string coreRoot = Path.Combine(_directory, "Core_Root");
        string inputs = Path.Combine(_directory, "IL-CG2");
        Directory.CreateDirectory(Path.Combine(coreRoot, "crossgen2"));
        Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(coreRoot, "System.Private.CoreLib.dll"), "corelib");
        File.WriteAllText(Path.Combine(coreRoot, "runtime"), "runtime");
        File.WriteAllText(Path.Combine(coreRoot, "crossgen2", "compiler"), "compiler");
        File.WriteAllText(Path.Combine(inputs, "test.dll"), "IL");
        _task = new Crossgen2Test
        {
            CoreRoot = coreRoot,
            InputDirectory = inputs,
            OutputFile = Path.Combine(_directory, "test.dll"),
            Cacheable = true,
        };
    }

    [Theory]
    [InlineData("Core_Root/crossgen2/compiler")]
    [InlineData("Core_Root/System.Private.CoreLib.dll")]
    [InlineData("Core_Root/runtime")]
    [InlineData("IL-CG2/test.dll")]
    public void InputContentChangesInvalidateCache(string relativePath)
    {
        string original = InvocationCache.ComputeKey(Describe());
        string path = Path.Combine(_directory, relativePath);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "modified");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.NotEqual(original, InvocationCache.ComputeKey(Describe()));
    }

    [Theory]
    [InlineData("Core_Root/crossgen2/new-jit")]
    [InlineData("Core_Root/new-reference.dll")]
    [InlineData("IL-CG2/dependency.dll")]
    public void InputSetChangesInvalidateCache(string relativePath)
    {
        string original = InvocationCache.ComputeKey(Describe());
        string path = Path.Combine(_directory, relativePath);
        File.WriteAllText(path, "new input");
        Assert.NotEqual(original, InvocationCache.ComputeKey(Describe()));
        File.Delete(path);
        Assert.Equal(original, InvocationCache.ComputeKey(Describe()));
    }

    [Theory]
    [InlineData("Core_Root/runtime.dbg")]
    [InlineData("Core_Root/crossgen2/compiler.dbg")]
    public void NativeDebugSidecarsDoNotInvalidateCache(string relativePath)
    {
        string original = InvocationCache.ComputeKey(Describe());
        File.WriteAllText(Path.Combine(_directory, relativePath), "debug symbols");
        Assert.Equal(original, InvocationCache.ComputeKey(Describe()));
    }

    [Theory]
    [InlineData("test.dll -O --targetos:linux", "")]
    [InlineData("test.dll -O", "--inputbubble")]
    [InlineData("test.dll -O", "--composite")]
    public void ArgumentsInvalidateCache(string response, string arguments)
    {
        Assert.NotEqual(InvocationCache.ComputeKey(Describe()),
            InvocationCache.ComputeKey(Describe(response, arguments)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--composite")]
    public void OutputContentsDoNotChangeInputIdentity(string arguments)
    {
        string original = InvocationCache.ComputeKey(Describe(arguments: arguments));
        File.WriteAllText(_task.OutputFile, "compiled image");
        Assert.Equal(original, InvocationCache.ComputeKey(Describe(arguments: arguments)));
        File.Delete(_task.OutputFile);
        Assert.Equal(original, InvocationCache.ComputeKey(Describe(arguments: arguments)));
    }

    [Fact]
    public void CompositeOwnsManifestComponentsAndSymbols()
    {
        _task.OutputFile = Path.Combine(_directory, "composite-r2r.dll");
        Assert.Equal(new[]
        {
            Path.Combine(_directory, "composite-r2r.dll"),
            Path.Combine(_directory, "composite-r2r.ni.pdb"),
            Path.Combine(_directory, "test.dll"),
            Path.Combine(_directory, "test.ni.pdb"),
        }, Describe(arguments: "--composite").Outputs.Files);
    }

    [Theory]
    [InlineData(false, "test.dll")]
    [InlineData(true, "test.wasm")]
    [InlineData(true, "test.o")]
    public void UnsupportedInvocationsBypassCache(bool cacheable, string outputFile)
    {
        _task.Cacheable = cacheable;
        _task.OutputFile = Path.Combine(_directory, outputFile);
        Assert.Null(Describe());
    }

    [Fact]
    public void SymbolGenerationBypassesCache() => Assert.Null(Describe("test.dll -O --pdb"));

    [Fact]
    public void ResponseFilePrecedesExtraArguments()
    {
        _task.ExtraArguments = "--targetos:windows";
        string command = (string)typeof(Crossgen2Test)
            .GetMethod("GetResponseFileSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_task, new object[] { Path.Combine(_directory, "test response.rsp") })!;
        Assert.StartsWith("@", command);
        Assert.Contains("test response.rsp", command);
        Assert.EndsWith(" --targetos:windows", command);
    }

    private InvocationDescription Describe(string response = "test.dll -O", string arguments = "")
    {
        _task.ExtraArguments = arguments;
        return (InvocationDescription)typeof(Crossgen2Test)
            .GetMethod("DescribeInvocation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_task, new object[] { Path.Combine(_task.CoreRoot, "crossgen2", "compiler"), response, "" })!;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
