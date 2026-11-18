// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks;

partial class RunReadyToRunCompiler
{
    private static readonly InvocationCacheConfiguration s_runtimeTestCacheConfiguration =
        new("Crossgen2Test", "DOTNET_CROSSGEN2_CACHE", "DOTNET_CROSSGEN2_CACHE_PATH");

    private RuntimeTestInvocation? _runtimeTest;

    internal sealed record RuntimeTestInvocation(string CoreRoot, string OutputFile, string ResponseFile,
        string[] Arguments, bool Cacheable);

    internal static RunReadyToRunCompiler CreateForRuntimeTest(RuntimeTestInvocation invocation) =>
        new()
        {
            _runtimeTest = invocation,
            UseCrossgen2 = true,
            Crossgen2Tool = new TaskItem(Path.Combine(invocation.CoreRoot, "crossgen2",
                OperatingSystem.IsWindows() ? "crossgen2.exe" : "crossgen2")),
            LogStandardErrorAsError = false,
            ShowCompilerWarnings = true,
            StandardOutputImportance = "High",
            StandardErrorImportance = "High",
        };

    private bool ValidateRuntimeTest()
    {
        if (!File.Exists(Crossgen2Tool.ItemSpec))
            Log.LogError(Strings.Crossgen2ToolExecutableNotFound, Crossgen2Tool.ItemSpec);
        if (!File.Exists(_runtimeTest!.ResponseFile))
            Log.LogError(Strings.InputAssemblyNotFound, _runtimeTest.ResponseFile);

        return !Log.HasLoggedErrors;
    }

    protected override string GetResponseFileSwitch(string responseFilePath)
    {
        string responseSwitch = base.GetResponseFileSwitch(responseFilePath);
        if (_runtimeTest is null || _runtimeTest.Arguments.Length == 0)
            return responseSwitch;

        // The wrapper's trailing arguments can override switches in the response file.
        var commandLine = new CommandLineBuilder();
        foreach (string argument in _runtimeTest.Arguments)
            commandLine.AppendSwitchIfNotNull("", argument);

        return $"{responseSwitch} {commandLine}";
    }

    internal InvocationDescription? DescribeRuntimeTest(RuntimeTestInvocation test, string pathToTool,
        string response, string command)
    {
        if (!test.Cacheable || !test.OutputFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var compiled = new SortedSet<string>(Crossgen2Cache.PathComparer);
            var references = new SortedSet<string>(Crossgen2Cache.PathComparer);
            string outputFile = Crossgen2Cache.InputPath(test.OutputFile);
            bool outputSpecified = false;
            bool composite = false;
            string[] switches = [.. response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), .. test.Arguments];
            foreach (string argument in switches)
            {
                if (argument.StartsWith("-r:", StringComparison.Ordinal))
                    references.UnionWith(ExpandFiles(argument["-r:".Length..]));
                else if (argument.StartsWith("-o:", StringComparison.Ordinal))
                {
                    if (!Crossgen2Cache.PathComparer.Equals(outputFile, Crossgen2Cache.InputPath(argument["-o:".Length..].Trim('"'))))
                        return null;
                    outputSpecified = true;
                }
                else if (argument == "--composite")
                    composite = true;
                else if (argument is "-O" or "--verify-type-and-field-layout" or "--method-layout:random" or
                         "--inputbubble" or "--hot-cold-splitting" ||
                         argument.StartsWith("--targetos:", StringComparison.Ordinal) ||
                         argument.StartsWith("--targetarch:", StringComparison.Ordinal))
                    continue;
                else if (!argument.StartsWith('-') && !argument.StartsWith('@') && Path.IsPathFullyQualified(argument.Trim('"')))
                    compiled.UnionWith(ExpandFiles(argument));
                else
                    return null;
            }
            if (!outputSpecified || compiled.Count == 0)
                return null;

            var inputs = new SortedSet<string>(Crossgen2Cache.PathComparer) { pathToTool };
            // Include the test runtime and the full compiler layout, not just the executable.
            inputs.UnionWith(Directory.EnumerateFiles(test.CoreRoot)
                .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(pathToTool)!, "*", SearchOption.AllDirectories))
                .Where(path => !path.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)));

            var outputs = new SortedSet<string>(Crossgen2Cache.PathComparer) { outputFile };
            if (composite)
            {
                string outputDirectory = Path.GetDirectoryName(outputFile)!;
                foreach (string input in compiled)
                    outputs.Add(Path.Combine(outputDirectory, Path.GetFileName(input)));
            }
            foreach (string output in outputs.ToArray())
                outputs.Add(GetSymbolOutputPath(output, "", OperatingSystem.IsWindows()));

            references.UnionWith(compiled);
            return Crossgen2Cache.DescribeInvocation(
                // The single-file launcher contains the task and its managed and native runtime.
                Environment.ProcessPath!, inputs, references, outputs.ToArray(), response, command,
                Directory.GetCurrentDirectory(), GetRuntimeTestCacheContext(test.Arguments), []);
        }
        catch (Exception ex) when (Crossgen2Cache.IsCacheFailure(ex))
        {
            Crossgen2Cache.Report(Log, $"bypass: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> GetRuntimeTestCacheContext(string[] arguments)
    {
        foreach (string argument in arguments)
            yield return argument;

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal))
        {
            string name = (string)entry.Key;
            // MSBuild's dotnet host sets a new telemetry ID per invocation.
            if (name.Equals("DOTNET_CLI_TELEMETRY_SESSIONID", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LD_", StringComparison.Ordinal) ||
                name.StartsWith("DYLD_", StringComparison.Ordinal) ||
                name.StartsWith("LC_", StringComparison.Ordinal) ||
                name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
                name is "LANG")
            {
                yield return name;
                yield return (string)entry.Value!;
            }
        }
    }

    private static IEnumerable<string> ExpandFiles(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim('"'));
        string fileName = Path.GetFileName(fullPath);
        if (!fileName.Contains('*') && !fileName.Contains('?'))
            return File.Exists(fullPath) ? [fullPath] : [];

        string directory = Path.GetDirectoryName(fullPath)!;
        return Directory.Exists(directory) ? Directory.GetFiles(directory, fileName) : [];
    }
}
