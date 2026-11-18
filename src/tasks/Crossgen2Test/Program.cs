// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Linq;
using Microsoft.Build.Framework;

namespace Microsoft.NET.Build.Tasks;

internal sealed class Program : IBuildEngine
{
    private const string Usage =
        "Usage: crossgen2-test --core-root <dir> --out <file> --rsp <file> [--no-cache] [-- <crossgen2 arguments>...]";

    private static int Main(string[] args)
    {
        string coreRoot = "";
        string outputFile = "";
        string responseFile = "";
        string[] extraArguments = [];
        bool cacheable = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                extraArguments = args[(i + 1)..];
                break;
            }

            if (args[i] == "--no-cache")
            {
                cacheable = false;
                continue;
            }

            string option = args[i];
            if (++i >= args.Length)
                return Fail($"Missing value for '{option}'.");

            string value = args[i];
            switch (option)
            {
                case "--core-root": coreRoot = value; break;
                case "--out": outputFile = value; break;
                case "--rsp": responseFile = value; break;
                default: return Fail($"Unknown option '{option}'.");
            }
        }

        if (new[] { coreRoot, outputFile, responseFile }.Any(string.IsNullOrEmpty))
            return Fail("Missing required option.");

        RunReadyToRunCompiler task = RunReadyToRunCompiler.CreateForRuntimeTest(
            new(coreRoot, outputFile, responseFile, extraArguments, cacheable));
        task.BuildEngine = new Program();
        bool succeeded = task.Execute();
        return succeeded ? 0 : task.ExitCode != 0 ? task.ExitCode : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

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
