// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Known crossgen2 option kinds.
/// </summary>
internal enum Crossgen2OptionKind
{
    /// <summary>Enables cross-module inlining for a named assembly (--opt-cross-module:AssemblyName).</summary>
    CrossModuleOptimization,
}

/// <summary>
/// A typed crossgen2 option with optional parameter value.
/// </summary>
internal sealed record Crossgen2Option(Crossgen2OptionKind Kind, string? Value = null)
{
    public static Crossgen2Option CrossModuleOptimization(string assemblyName)
        => new(Crossgen2OptionKind.CrossModuleOptimization, assemblyName);

    public IEnumerable<string> ToArgs() => Kind switch
    {
        Crossgen2OptionKind.CrossModuleOptimization => [$"--opt-cross-module:{Value}"],
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };
}

/// <summary>
/// Result of a crossgen2 compilation step.
/// </summary>
internal sealed record R2RCompilationResult(
    string OutputPath,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Options for a single crossgen2 compilation step.
/// </summary>
internal sealed class R2RCompilationOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public List<string> ReferencePaths { get; init; } = new();
    public List<string> ExtraArgs { get; init; } = new();
    public bool Composite { get; init; }
    public List<string>? CompositeInputPaths { get; init; }
    public List<string>? InputBubbleRefs { get; init; }
}

/// <summary>
/// Invokes crossgen2 out-of-process to produce R2R images.
/// </summary>
internal sealed class R2RDriver
{
    private readonly string _crossgen2Dir;

    public R2RDriver()
    {
        _crossgen2Dir = TestPaths.Crossgen2Dir;

        if (!File.Exists(TestPaths.Crossgen2Dll))
            throw new FileNotFoundException($"crossgen2.dll not found at {TestPaths.Crossgen2Dll}");
    }

    /// <summary>
    /// Runs crossgen2 on a single assembly.
    /// </summary>
    public R2RCompilationResult Compile(R2RCompilationOptions options)
    {
        var args = new List<string>();

        if (options.Composite)
        {
            args.Add("--composite");
            if (options.CompositeInputPaths is not null)
            {
                foreach (string input in options.CompositeInputPaths)
                    args.Add(input);
            }
        }
        else
        {
            args.Add(options.InputPath);
        }

        args.Add("-o");
        args.Add(options.OutputPath);

        foreach (string refPath in options.ReferencePaths)
        {
            args.Add("-r");
            args.Add(refPath);
        }

        if (options.InputBubbleRefs is not null)
        {
            foreach (string bubbleRef in options.InputBubbleRefs)
            {
                args.Add("--inputbubbleref");
                args.Add(bubbleRef);
            }
        }

        args.AddRange(options.ExtraArgs);

        return RunCrossgen2(args);
    }

    /// <summary>
    /// Crossgen2 a dependency assembly (simple single-assembly R2R).
    /// </summary>
    public R2RCompilationResult CompileDependency(string inputPath, string outputPath, IEnumerable<string> referencePaths)
    {
        return Compile(new R2RCompilationOptions
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            ReferencePaths = referencePaths.ToList()
        });
    }

    private R2RCompilationResult RunCrossgen2(List<string> crossgen2Args)
    {
        // Use dotnet exec to invoke crossgen2.dll
        string dotnetHost = TestPaths.DotNetHost;
        string crossgen2Dll = TestPaths.Crossgen2Dll;

        var allArgs = new List<string> { "exec", crossgen2Dll };
        allArgs.AddRange(crossgen2Args);

        string argsString = string.Join(" ", allArgs.Select(QuoteIfNeeded));

        var psi = new ProcessStartInfo
        {
            FileName = dotnetHost,
            Arguments = argsString,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Strip environment variables that interfere with crossgen2
        string[] envVarsToStrip = { "DOTNET_GCName", "DOTNET_GCStress", "DOTNET_HeapVerify", "DOTNET_ReadyToRun" };
        foreach (string envVar in envVarsToStrip)
        {
            psi.Environment[envVar] = null;
        }

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string outputPath = crossgen2Args
            .SkipWhile(a => a != "-o")
            .Skip(1)
            .FirstOrDefault() ?? "unknown";

        return new R2RCompilationResult(
            outputPath,
            process.ExitCode,
            stdout,
            stderr);
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }
}
