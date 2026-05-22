// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if DEBUG
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILLink.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using VerifyCS = ILLink.RoslynAnalyzer.Tests.CSharpCodeFixVerifier<
    ILLink.RoslynAnalyzer.Tests.RequiresUnsafeCodeFixTests.RequiresUnsafeDiagnosticAnalyzer,
    ILLink.CodeFix.RequiresUnsafeCodeFixProvider>;

namespace ILLink.RoslynAnalyzer.Tests
{
    public class RequiresUnsafeCodeFixTests
    {
        private const string FullyQualifiedRequiresUnsafeAttribute = "System.Diagnostics.CodeAnalysis.RequiresUnsafeAttribute";

        private static Task VerifyRequiresUnsafeCodeFix(
            string source,
            string fixedSource,
            DiagnosticResult[] baselineExpected,
            int codeActionIndex = 1)
        {
            var test = new VerifyCS.Test
            {
                TestCode = source,
                FixedCode = fixedSource,
                CodeActionIndex = codeActionIndex
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
        public async Task CodeFix_WrapInUnsafeBlock_SimpleStatement()
        {
            var test = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public static int M1() => 0;

                    public void M2()
                    {
                        int x = M1();
                    }
                }

                namespace System.Diagnostics.CodeAnalysis
                {
                    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
                    public sealed class RequiresUnsafeAttribute : Attribute { }
                }
                """;

            var fixedSource = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public static int M1() => 0;

                    public void M2()
                    {
                        int x;
                        // TODO(unsafe): Baselining unsafe usage
                        unsafe
                        {
                            x = M1();
                        }
                    }
                }

                namespace System.Diagnostics.CodeAnalysis
                {
                    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
                    public sealed class RequiresUnsafeAttribute : Attribute { }
                }
                """;

            await VerifyRequiresUnsafeCodeFix(
                source: test,
                fixedSource: fixedSource,
                baselineExpected: new[] {
                    VerifyCS.Diagnostic(DiagnosticId.RequiresUnsafe)
                        .WithSpan(10, 17, 10, 19)
                        .WithArguments("C.M1()", "", "")
                });
        }

        [Fact]
        public async Task CodeFix_AddRequiresUnsafeAttribute_Method()
        {
            var test = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public static int M1() => 0;

                    public int M2()
                    {
                        return M1();
                    }
                }

                namespace System.Diagnostics.CodeAnalysis
                {
                    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
                    public sealed class RequiresUnsafeAttribute : Attribute { }
                }
                """;

            var fixedSource = """
                using System.Diagnostics.CodeAnalysis;

                public class C
                {
                    [RequiresUnsafe]
                    public static int M1() => 0;

                    [RequiresUnsafe()]
                    public int M2()
                    {
                        return M1();
                    }
                }

                namespace System.Diagnostics.CodeAnalysis
                {
                    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
                    public sealed class RequiresUnsafeAttribute : Attribute { }
                }
                """;

            await VerifyRequiresUnsafeCodeFix(
                source: test,
                fixedSource: fixedSource,
                baselineExpected: new[] {
                    VerifyCS.Diagnostic(DiagnosticId.RequiresUnsafe)
                        .WithSpan(10, 16, 10, 18)
                        .WithArguments("C.M1()", "", "")
                },
                codeActionIndex: 0);
        }

        public sealed class RequiresUnsafeDiagnosticAnalyzer : DiagnosticAnalyzer
        {
            private static readonly DiagnosticDescriptor s_rule = DiagnosticDescriptors.GetDiagnosticDescriptor(DiagnosticId.RequiresUnsafe);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

            public override void Initialize(AnalysisContext context)
            {
                context.EnableConcurrentExecution();
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            }

            private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocation = (InvocationExpressionSyntax)context.Node;
                if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
                    || !HasRequiresUnsafeAttribute(method)
                    || IsInUnsafeContext(invocation)
                    || IsInRequiresUnsafeScope(context.SemanticModel, invocation, context.CancellationToken))
                    return;

                context.ReportDiagnostic(Diagnostic.Create(
                    s_rule,
                    invocation.GetLocation(),
                    method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    "",
                    ""));
            }

            private static bool IsInRequiresUnsafeScope(SemanticModel semanticModel, SyntaxNode node, CancellationToken cancellationToken)
            {
                for (ISymbol? symbol = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
                    symbol is not null;
                    symbol = symbol.ContainingSymbol)
                {
                    if (HasRequiresUnsafeAttribute(symbol))
                        return true;

                    if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol property }
                        && HasRequiresUnsafeAttribute(property))
                        return true;
                }

                return false;
            }

            private static bool IsInUnsafeContext(SyntaxNode node)
            {
                for (SyntaxNode? current = node; current is not null; current = current.Parent)
                {
                    if (current.IsKind(SyntaxKind.UnsafeStatement))
                        return true;

                    if (current is BaseMethodDeclarationSyntax method && method.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        return true;

                    if (current is LocalFunctionStatementSyntax localFunction && localFunction.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        return true;

                    if (current is BasePropertyDeclarationSyntax property && property.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        return true;

                    if (current is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        return true;

                    if (current is TypeDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        return true;
                }

                return false;
            }

            private static bool HasRequiresUnsafeAttribute(ISymbol symbol) =>
                symbol.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == FullyQualifiedRequiresUnsafeAttribute);
        }
    }
}
#endif
