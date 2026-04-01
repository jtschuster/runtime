// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace ILCompiler.ReadyToRun.Tests.Expectations;

/// <summary>
/// Marks a method as expected to be cross-module inlined into the main R2R image.
/// The R2R result checker will verify a CHECK_IL_BODY fixup exists for this method's callee.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExpectInlinedAttribute : Attribute
{
    /// <summary>
    /// The fully qualified name of the method expected to be inlined.
    /// If null, infers from the method body (looks for the first cross-module call).
    /// </summary>
    public string? MethodName { get; set; }
}

/// <summary>
/// Marks a method as expected to have async thunk RuntimeFunctions in the R2R image.
/// Async methods compiled with --opt-async-methods produce 3 RuntimeFunctions:
/// thunk + async body + resumption stub.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExpectAsyncThunkAttribute : Attribute
{
    /// <summary>
    /// Expected number of RuntimeFunctions. Defaults to 3 (thunk + body + resumption).
    /// </summary>
    public int ExpectedRuntimeFunctionCount { get; set; } = 3;
}

/// <summary>
/// Declares that the R2R image should contain a manifest reference to the specified assembly.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ExpectManifestRefAttribute : Attribute
{
    public string AssemblyName { get; }

    public ExpectManifestRefAttribute(string assemblyName)
    {
        AssemblyName = assemblyName;
    }
}

/// <summary>
/// Specifies a crossgen2 command-line option for the main assembly compilation.
/// Applied at the assembly level of the test case.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class Crossgen2OptionAttribute : Attribute
{
    public string Option { get; }

    public Crossgen2OptionAttribute(string option)
    {
        Option = option;
    }
}

/// <summary>
/// Declares a dependency assembly that should be compiled before the main test assembly.
/// The source files are compiled with Roslyn, then optionally crossgen2'd before the main assembly.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SetupCompileBeforeAttribute : Attribute
{
    /// <summary>
    /// The output assembly filename (e.g., "InlineableLib.dll").
    /// </summary>
    public string OutputName { get; }

    /// <summary>
    /// Source file paths relative to the test case's Dependencies/ folder.
    /// </summary>
    public string[] SourceFiles { get; }

    /// <summary>
    /// Additional assembly references needed to compile this dependency.
    /// </summary>
    public string[]? References { get; set; }

    /// <summary>
    /// If true, this assembly is also crossgen2'd before the main assembly.
    /// </summary>
    public bool Crossgen { get; set; }

    /// <summary>
    /// Additional crossgen2 options for this dependency assembly.
    /// </summary>
    public string[]? CrossgenOptions { get; set; }

    public SetupCompileBeforeAttribute(string outputName, string[] sourceFiles)
    {
        OutputName = outputName;
        SourceFiles = sourceFiles;
    }
}

/// <summary>
/// Marks a method as expected to have R2R compiled code in the output image.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExpectR2RMethodAttribute : Attribute
{
}

/// <summary>
/// Marks an assembly-level option to enable composite mode compilation.
/// When present, all SetupCompileBefore assemblies with Crossgen=true are compiled
/// together with the main assembly using --composite.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CompositeModeAttribute : Attribute
{
}

/// <summary>
/// Marks an assembly-level option to enable runtime-async compilation.
/// Adds Features=runtime-async=on to Roslyn compilation and --opt-async-methods to crossgen2.
/// </summary>
[Conditional("R2R_EXPECTATIONS")]
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class EnableRuntimeAsyncAttribute : Attribute
{
}
