// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

/// <summary>Runs a generated runtime test's Crossgen2 invocation with content-based caching.</summary>
internal sealed class Crossgen2Test : CachedToolTask
{
    private static readonly InvocationCacheConfiguration s_cacheConfiguration =
        new("Crossgen2Test", "DOTNET_CROSSGEN2_CACHE", "DOTNET_CROSSGEN2_CACHE_PATH");

    /// <summary>Gets or sets the runtime layout used by the test and compiler.</summary>
    [Required]
    public string CoreRoot { get; set; } = "";

    /// <summary>Gets or sets the directory containing the preserved IL inputs.</summary>
    [Required]
    public string InputDirectory { get; set; } = "";

    /// <summary>Gets or sets the compiler's primary output path.</summary>
    [Required]
    public string OutputFile { get; set; } = "";

    /// <summary>Gets or sets the generated compiler response file.</summary>
    [Required]
    public string ResponseFile { get; set; } = "";

    /// <summary>Gets or sets additional compiler arguments.</summary>
    public string ExtraArguments { get; set; } = "";

    /// <summary>Gets or sets whether the wrapper can describe all compiler inputs and outputs.</summary>
    public bool Cacheable { get; set; }

    private protected override InvocationCacheConfiguration CacheConfiguration => s_cacheConfiguration;

    protected override string ToolName => OperatingSystem.IsWindows() ? "crossgen2.exe" : "crossgen2";

    protected override string GenerateFullPathToTool() => Path.Combine(CoreRoot, "crossgen2", ToolName);

    protected override string GenerateResponseFileCommands() => File.ReadAllText(ResponseFile);

    // Extra arguments must follow the response file to preserve option precedence.
    protected override string GetResponseFileSwitch(string responseFilePath) =>
        $"{base.GetResponseFileSwitch(responseFilePath)} {ExtraArguments}";

    private protected override InvocationDescription? DescribeInvocation(
        string pathToTool, string responseFileCommands, string commandLineCommands)
    {
        // Symbol generation can read PDBs outside the preserved IL directory.
        if (!Cacheable || !OutputFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            responseFileCommands.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--pdb"))
            return null;

        var inputs = new SortedSet<string>(StringComparer.Ordinal)
        {
            Path.GetFullPath(pathToTool),
            // The NativeAOT or single-file host executable contains this task.
            Environment.ProcessPath!,
        };
        // Native debug sidecars are large and do not participate in compilation or execution.
        inputs.UnionWith(Directory.EnumerateFiles(CoreRoot)
            .Concat(Directory.EnumerateFiles(Path.Combine(CoreRoot, "crossgen2"), "*", SearchOption.AllDirectories))
            .Where(path => !path.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)));
        inputs.UnionWith(Directory.GetFiles(InputDirectory, "*.dll"));

        var outputs = new SortedSet<string>(StringComparer.Ordinal) { Path.GetFullPath(OutputFile) };
        if (ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("--composite"))
        {
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(OutputFile))!;
            foreach (string input in Directory.GetFiles(InputDirectory, "*.dll"))
                outputs.Add(Path.Combine(outputDirectory, Path.GetFileName(input)));
        }
        foreach (string output in outputs.ToArray())
            outputs.Add(Path.ChangeExtension(output, ".ni.pdb"));

        var context = new List<string> { responseFileCommands, commandLineCommands, ExtraArguments };
        // Compiler/JIT switches and native-library resolution are inherited by the compiler process.
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal))
        {
            string name = (string)entry.Key;
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LD_", StringComparison.Ordinal) ||
                name.StartsWith("DYLD_", StringComparison.Ordinal) ||
                name.StartsWith("LC_", StringComparison.Ordinal) ||
                name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
                name is "LANG")
            {
                context.Add(name);
                context.Add((string)entry.Value!);
            }
        }

        return new InvocationDescription(InvocationInputs.FromFiles(inputs.ToArray()), context.ToArray(),
            InvocationOutputs.FromFiles(outputs.ToArray()));
    }
}
