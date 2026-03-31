// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// Orchestrates crossgen2 compilation and R2R validation for cross-module resolution tests.
///
/// Usage:
///   RunCrossgen --crossgen2 &lt;path&gt; --output-dir &lt;dir&gt; --ref-dir &lt;dir&gt;
///     --assembly &lt;name&gt; [--extra-args &lt;args&gt;]     (repeatable, order matters)
///     --main &lt;name&gt; --main-extra-args &lt;args&gt; --main-refs &lt;name,...&gt;
///     [--common-args &lt;args&gt;]
///     [--validate &lt;r2rvalidate.dll path&gt; --validate-args &lt;args&gt;]
/// </summary>
class RunCrossgen
{
    static int Main(string[] args)
    {
        string crossgen2Path = null;
        string outputDir = null;
        string refDir = null;
        string coreRunPath = null;
        string commonArgs = "";
        string mainAssembly = null;
        string mainExtraArgs = "";
        string mainRefs = "";
        string validatePath = null;
        string validateArgs = "";

        var steps = new List<(string name, string extraArgs)>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--crossgen2":
                    crossgen2Path = args[++i];
                    break;
                case "--output-dir":
                    outputDir = args[++i];
                    break;
                case "--ref-dir":
                    refDir = args[++i];
                    break;
                case "--corerun":
                    coreRunPath = args[++i];
                    break;
                case "--common-args":
                    commonArgs = args[++i];
                    break;
                case "--assembly":
                    string name = args[++i];
                    string extra = "";
                    if (i + 1 < args.Length && args[i + 1] == "--extra-args")
                    {
                        i++;
                        extra = args[++i];
                    }
                    steps.Add((name, extra));
                    break;
                case "--main":
                    mainAssembly = args[++i];
                    break;
                case "--main-extra-args":
                    mainExtraArgs = args[++i];
                    break;
                case "--main-refs":
                    mainRefs = args[++i];
                    break;
                case "--validate":
                    validatePath = args[++i];
                    break;
                case "--validate-args":
                    validateArgs = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 1;
            }
        }

        if (crossgen2Path is null || outputDir is null || refDir is null || mainAssembly is null)
        {
            Console.Error.WriteLine("Required: --crossgen2, --output-dir, --ref-dir, --main");
            return 1;
        }

        // All assembly names (deps + main)
        var allAssemblies = steps.Select(s => s.name).Append(mainAssembly).ToList();

        // 1. Copy IL assemblies to IL_DLLS/ before crossgen2 overwrites them
        string ilDir = Path.Combine(outputDir, "IL_DLLS");
        Directory.CreateDirectory(ilDir);

        foreach (string asm in allAssemblies)
        {
            string src = Path.Combine(outputDir, $"{asm}.dll");
            string dst = Path.Combine(ilDir, $"{asm}.dll");
            if (!File.Exists(dst))
            {
                if (!File.Exists(src))
                {
                    Console.Error.WriteLine($"FAILED: source assembly not found: {src}");
                    return 1;
                }
                File.Copy(src, dst);
                Console.WriteLine($"Copied {asm}.dll to IL_DLLS/");
            }
        }

        // 2. Crossgen2 each dependency assembly
        foreach (var (name, extraArgs) in steps)
        {
            int exitCode = RunCrossgen2(crossgen2Path, outputDir, refDir, commonArgs,
                name, extraArgs);
            if (exitCode != 0)
                return exitCode;
        }

        // 3. Crossgen2 main assembly with refs and extra args
        string refArgs = string.Join(" ", mainRefs.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => $"-r:{r.Trim()}.dll"));
        string allMainExtra = $"{refArgs} {mainExtraArgs} --map".Trim();

        int mainExit = RunCrossgen2(crossgen2Path, outputDir, refDir, commonArgs,
            mainAssembly, allMainExtra);
        if (mainExit != 0)
            return mainExit;

        string mapFile = Path.Combine(outputDir, $"{mainAssembly}.map");
        if (!File.Exists(mapFile))
        {
            Console.Error.WriteLine($"FAILED: no map file generated at {mapFile}");
            return 1;
        }

        // 4. R2R Validation (optional)
        if (validatePath is not null && coreRunPath is not null && File.Exists(validatePath))
        {
            Console.WriteLine($"Running R2R validation on {mainAssembly}.dll...");
            string valArgs = $"\"{validatePath}\" --in {mainAssembly}.dll --ref \"{refDir}\" --ref \"{outputDir}\" {validateArgs}";

            int valExit = RunProcess(coreRunPath, valArgs, outputDir);
            if (valExit != 100)
            {
                Console.Error.WriteLine($"R2R validation failed with exitcode: {valExit}");
                return 1;
            }
            Console.WriteLine("R2R validation passed");
        }
        else if (validatePath is not null)
        {
            Console.WriteLine($"WARNING: r2rvalidate not found at {validatePath}, skipping");
        }

        Console.WriteLine("Crossgen orchestration completed successfully");
        return 0;
    }

    static int RunCrossgen2(string crossgen2Path, string outputDir, string refDir,
        string commonArgs, string assemblyName, string extraArgs)
    {
        string ilDll = Path.Combine("IL_DLLS", $"{assemblyName}.dll");
        string outDll = $"{assemblyName}.dll";

        // Use glob pattern for reference directory
        string refGlob = Path.Combine(refDir, "*.dll");
        string arguments = $"{commonArgs} -r:\"{refGlob}\" {extraArgs} -o:{outDll} {ilDll}".Trim();

        Console.WriteLine($"Crossgen2 {assemblyName}...");
        int exitCode = RunProcess(crossgen2Path, arguments, outputDir);

        if (exitCode != 0)
        {
            Console.Error.WriteLine($"Crossgen2 {assemblyName} failed with exitcode: {exitCode}");
            return 1;
        }

        return 0;
    }

    static int RunProcess(string fileName, string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };

        // Suppress DOTNET variables that interfere with crossgen2
        psi.Environment.Remove("DOTNET_GCName");
        psi.Environment.Remove("DOTNET_GCStress");
        psi.Environment.Remove("DOTNET_HeapVerify");
        psi.Environment.Remove("DOTNET_ReadyToRun");

        Console.WriteLine($"  > {fileName} {arguments}");

        using var process = Process.Start(psi);
        process.WaitForExit();

        return process.ExitCode;
    }
}
