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
    private readonly RunReadyToRunCompiler _task;
    private RunReadyToRunCompiler.RuntimeTestInvocation _invocation;

    public Crossgen2TestTests()
    {
        string coreRoot = Path.Combine(_directory, "Core_Root");
        string inputs = Path.Combine(_directory, "test");
        Directory.CreateDirectory(Path.Combine(coreRoot, "crossgen2"));
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(Path.Combine(_directory, "readytorun"));
        File.Copy(typeof(object).Assembly.Location, Path.Combine(coreRoot, "System.Private.CoreLib.dll"));
        File.WriteAllText(Path.Combine(coreRoot, "runtime"), "runtime");
        File.WriteAllText(Path.Combine(coreRoot, "crossgen2", "compiler"), "compiler");
        File.Copy(typeof(Crossgen2TestTests).Assembly.Location, Path.Combine(inputs, "test.dll"));
        _invocation = new(coreRoot, Path.Combine(_directory, "readytorun", "test.dll"), "", [], Cacheable: true);
        _task = RunReadyToRunCompiler.CreateForRuntimeTest(_invocation);
    }

    [Theory]
    [InlineData("Core_Root/crossgen2/compiler")]
    [InlineData("Core_Root/System.Private.CoreLib.dll")]
    [InlineData("Core_Root/runtime")]
    [InlineData("test/test.dll")]
    public void InputContentChangesInvalidateCache(string relativePath)
    {
        string original = InvocationCache.ComputeKey(Describe());
        string path = Path.Combine(_directory, relativePath);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, "modified");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.NotEqual(original, InvocationCache.ComputeKey(Describe()));
    }

    [Theory]
    [InlineData("Core_Root/crossgen2/new-jit")]
    [InlineData("Core_Root/new-reference.dll")]
    [InlineData("test/dependency.dll")]
    public void InputSetChangesInvalidateCache(string relativePath)
    {
        string original = InvocationCache.ComputeKey(Describe());
        string path = Path.Combine(_directory, relativePath);
        File.Copy(typeof(Crossgen2TestTests).Assembly.Location, path);
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
    [InlineData("-O\n--targetos:linux", "")]
    [InlineData("-O", "--inputbubble")]
    [InlineData("-O", "--composite")]
    public void ArgumentsInvalidateCache(string options, string arguments)
    {
        Assert.NotEqual(InvocationCache.ComputeKey(Describe()),
            InvocationCache.ComputeKey(Describe($"{TestInput}\n{options}", arguments)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--composite")]
    public void OutputContentsDoNotChangeInputIdentity(string arguments)
    {
        string original = InvocationCache.ComputeKey(Describe(arguments: arguments));
        File.WriteAllText(_invocation.OutputFile, "compiled image");
        Assert.Equal(original, InvocationCache.ComputeKey(Describe(arguments: arguments)));
        File.Delete(_invocation.OutputFile);
        Assert.Equal(original, InvocationCache.ComputeKey(Describe(arguments: arguments)));
    }

    [Fact]
    public void CompositeOwnsManifestComponentsAndSymbols()
    {
        string output = Path.Combine(_directory, "readytorun");
        _invocation = _invocation with { OutputFile = Path.Combine(output, "composite-r2r.dll") };
        string symbolExtension = OperatingSystem.IsWindows() ? ".ni.pdb" : ".ni.r2rmap";
        string[] expected =
        {
            Path.Combine(output, "System.Private.CoreLib.dll"),
            Path.Combine(output, "System.Private.CoreLib" + symbolExtension),
            Path.Combine(output, "composite-r2r.dll"),
            Path.Combine(output, "composite-r2r" + symbolExtension),
            Path.Combine(output, "test.dll"),
            Path.Combine(output, "test" + symbolExtension),
        };
        Array.Sort(expected, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Assert.Equal(expected, Describe($"{Path.Combine(_directory, "test", "*.dll")}\n{Path.Combine(_invocation.CoreRoot, "System.Private.CoreLib.dll")}\n-O", "--composite").Outputs.Files);
    }

    [Theory]
    [InlineData(false, "test.dll")]
    [InlineData(true, "test.wasm")]
    [InlineData(true, "test.o")]
    public void UnsupportedInvocationsBypassCache(bool cacheable, string outputFile)
    {
        _invocation = _invocation with { Cacheable = cacheable, OutputFile = Path.Combine(_directory, "readytorun", outputFile) };
        Assert.Null(Describe());
    }

    [Fact]
    public void SymbolGenerationBypassesCache() => Assert.Null(Describe($"{TestInput}\n-O\n--pdb"));

    [Fact]
    public void ResponseFilePrecedesExtraArguments()
    {
        RunReadyToRunCompiler task = RunReadyToRunCompiler.CreateForRuntimeTest(_invocation with { Arguments = ["--targetos:windows"] });
        string command = (string)typeof(RunReadyToRunCompiler)
            .GetMethod("GetResponseFileSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(task, new object[] { Path.Combine(_directory, "test response.rsp") })!;
        Assert.StartsWith("@", command);
        Assert.Contains("test response.rsp", command);
        Assert.EndsWith(" --targetos:windows", command);
    }

    private string TestInput => Path.Combine(_directory, "test", "test.dll");

    private InvocationDescription Describe(string? response = null, string arguments = "")
    {
        response ??= $"{TestInput}\n-r:{Path.Combine(_directory, "test", "*.dll")}\n-O";
        return _task.DescribeRuntimeTest(_invocation with { Arguments = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries) },
            Path.Combine(_invocation.CoreRoot, "crossgen2", "compiler"), $"{response}\n-o:\"{_invocation.OutputFile}\"", "")!;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
