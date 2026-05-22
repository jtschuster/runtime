// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if DEBUG
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ILLink.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyCS = ILLink.RoslynAnalyzer.Tests.CSharpCodeFixVerifier<
    ILLink.RoslynAnalyzer.Tests.UnsafeMethodMissingRequiresUnsafeCodeFixTests.UnsafeMethodMissingRequiresUnsafeDiagnosticAnalyzer,
    ILLink.CodeFix.UnsafeMethodMissingRequiresUnsafeCodeFixProvider>;

namespace ILLink.RoslynAnalyzer.Tests
{
    public class UnsafeMethodMissingRequiresUnsafeCodeFixTests
    {
        private const string FullyQualifiedRequiresUnsafeAttribute = "System.Diagnostics.CodeAnalysis.RequiresUnsafeAttribute";
        private const string RequiresUnsafeAttributeDefinition = """

            namespace System.Diagnostics.CodeAnalysis
            {
                [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
                public sealed class RequiresUnsafeAttribute : Attribute { }
            }
            """;

        private static Task VerifyCodeFix(
            string source,
            string fixedSource,
            DiagnosticResult[] baselineExpected)
        {
            var test = new VerifyCS.Test
            {
                TestCode = source,
                FixedCode = fixedSource,
            };

            test.ExpectedDiagnostics.AddRange(baselineExpected);
            test.SolutionTransforms.Add((solution, projectId) =>
            {
                var project = solution.GetProject(projectId)!;
                var compilationOptions = (CSharpCompilationOptions)project.CompilationOptions!;
                compilationOptions = compilationOptions.WithAllowUnsafe(true);
                return solution.WithProjectCompilationOptions(projectId, compilationOptions);
            });

            return test.RunAsync();
        }

        [Fact]
        public async Task CodeFix_MethodReturningPointer_AddsAttribute()
        {
            var source = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    public unsafe int* M() => default;
                }
                """ + RequiresUnsafeAttributeDefinition;

            var fixedSource = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public unsafe int* M() => default;
                }
                """ + RequiresUnsafeAttributeDefinition;

            await VerifyCodeFix(
                source,
                fixedSource,
                baselineExpected: new[] {
                    VerifyCS.Diagnostic(DiagnosticId.UnsafeMethodMissingRequiresUnsafe)
                        .WithSpan(5, 24, 5, 25)
                        .WithArguments("C.M()")
                });
        }

        [Fact]
        public async Task CodeFix_MethodTakingPointerParameter_AddsAttribute()
        {
            var source = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    public unsafe void M(int* p) { }
                }
                """ + RequiresUnsafeAttributeDefinition;

            var fixedSource = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public unsafe void M(int* p) { }
                }
                """ + RequiresUnsafeAttributeDefinition;

            await VerifyCodeFix(
                source,
                fixedSource,
                baselineExpected: new[] {
                    VerifyCS.Diagnostic(DiagnosticId.UnsafeMethodMissingRequiresUnsafe)
                        .WithSpan(5, 24, 5, 25)
                        .WithArguments("C.M(Int32*)")
                });
        }

        public sealed class UnsafeMethodMissingRequiresUnsafeDiagnosticAnalyzer : DiagnosticAnalyzer
        {
            private static readonly DiagnosticDescriptor s_rule = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.UnsafeMethodMissingRequiresUnsafe);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

            public override void Initialize(AnalysisContext context)
            {
                context.EnableConcurrentExecution();
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
            }

            private static void AnalyzeMethod(SymbolAnalysisContext context)
            {
                if (context.Symbol is not IMethodSymbol method
                    || !HasPointerInSignature(method)
                    || HasRequiresUnsafeAttribute(method)
                    || (method.AssociatedSymbol is IPropertySymbol property && HasRequiresUnsafeAttribute(property)))
                    return;

                foreach (var location in method.Locations.Where(location => location.IsInSource))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        s_rule,
                        location,
                        method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                }
            }

            private static bool HasPointerInSignature(IMethodSymbol method)
            {
                if (IsPointerType(method.ReturnType))
                    return true;

                foreach (var parameter in method.Parameters)
                {
                    if (IsPointerType(parameter.Type))
                        return true;
                }

                return false;
            }

            private static bool IsPointerType(ITypeSymbol type) =>
                type is IPointerTypeSymbol or IFunctionPointerTypeSymbol;

            private static bool HasRequiresUnsafeAttribute(ISymbol symbol) =>
                symbol.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == FullyQualifiedRequiresUnsafeAttribute);
        }
    }
}
#endif
