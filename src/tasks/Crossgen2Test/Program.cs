// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

internal sealed class Program : IBuildEngine
{
    private const string Usage =
        "Usage: crossgen2-test --core-root <dir> --input-directory <dir> --out <file> --rsp <file> [--no-cache] [-- <crossgen2 arguments>...]";

    private static int Main(string[] args)
    {
        var task = new Crossgen2Test
        {
            BuildEngine = new Program(),
            Cacheable = true,
            StandardOutputImportance = "High",
            StandardErrorImportance = "High",
        };

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                task.ExtraArguments = string.Join(' ', args.Skip(i + 1).Select(Quote));
                break;
            }

            if (args[i] == "--no-cache")
            {
                task.Cacheable = false;
                continue;
            }

            string option = args[i];
            if (++i >= args.Length)
                return Fail($"Missing value for '{option}'.");

            string value = args[i];
            switch (option)
            {
                case "--core-root": task.CoreRoot = value; break;
                case "--input-directory": task.InputDirectory = value; break;
                case "--out": task.OutputFile = value; break;
                case "--rsp": task.ResponseFile = value; break;
                default: return Fail($"Unknown option '{option}'.");
            }
        }

        if (new[] { task.CoreRoot, task.InputDirectory, task.OutputFile, task.ResponseFile }.Any(string.IsNullOrEmpty))
            return Fail("Missing required option.");

        bool succeeded = task.Execute();
        return succeeded ? 0 : task.ExitCode != 0 ? task.ExitCode : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    // Arguments are re-joined into the compiler command line, so preserve those the shell kept intact.
    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? argument
            : $"\"{argument.Replace("\"", "\\\"")}\"";

    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "";

    public void LogErrorEvent(BuildErrorEventArgs e) => Console.Error.WriteLine(e.Message);
    public void LogWarningEvent(BuildWarningEventArgs e) => Console.WriteLine(e.Message);
    public void LogCustomEvent(CustomBuildEventArgs e) => Console.WriteLine(e.Message);
    public void LogMessageEvent(BuildMessageEventArgs e) => Console.WriteLine(e.Message);

    public bool BuildProjectFile(string projectFileName, string[] targetNames,
        IDictionary globalProperties, IDictionary targetOutputs) => throw new NotSupportedException();
}
