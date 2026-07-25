#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using StaticCs;

namespace NXTest.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class TestGenerator : IIncrementalGenerator
{
    private const string TestClassAttributeName = "NXTest.TestClassAttribute";
    private const string FactAttributeName = "NXTest.FactAttribute";
    private const string TheoryAttributeName = "NXTest.TheoryAttribute";
    private const string BenchAttributeName = "NXTest.BenchAttribute";
    private const string InlineDataAttributeName = "NXTest.InlineDataAttribute";

    private static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: "NXTEST001",
        title: "Unsupported test method return type",
        messageFormat: "Method '{0}' must return void or System.Threading.Tasks.Task; found '{1}'",
        category: "NXTest",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor MissingBenchmarkData = new(
        id: "NXTEST002",
        title: "Parameterized benchmark has no data",
        messageFormat: "Benchmark method '{0}' has parameters and must declare at least one InlineData row",
        category: "NXTest",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor InvalidBenchmarkData = new(
        id: "NXTEST003",
        title: "Benchmark data does not match parameters",
        messageFormat: "Benchmark method '{0}' expects {1} arguments, but an InlineData row supplies {2}",
        category: "NXTest",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register explicitly marked test classes so they can include inherited tests.
        var markedTestClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TestClassAttributeName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => ctx.TargetSymbol as INamedTypeSymbol)
            .Where(static c => c is not null)
            .Select(static (c, _) => c!);

        // Register Fact attributes
        var factMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FactAttributeName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => GetTestMethodInfo(ctx, TestMethodKind.Fact, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Register Theory attributes
        var theoryMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TheoryAttributeName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => GetTestMethodInfo(ctx, TestMethodKind.Theory, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var benchmarkMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                BenchAttributeName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => GetTestMethodInfo(ctx, TestMethodKind.Benchmark, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var allTestMethods = factMethods
            .Collect()
            .Combine(theoryMethods.Collect())
            .Combine(benchmarkMethods.Collect())
            .Select(static (pair, _) =>
                pair.Left.Left.AddRange(pair.Left.Right).AddRange(pair.Right));

        context.RegisterSourceOutput(allTestMethods, static (spc, methods) =>
        {
            foreach (var method in methods.OfType<TestMethodInfo>())
            {
                if (!method.HasSupportedReturnType)
                {
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            UnsupportedReturnType,
                            method.MethodSyntax.ReturnType.GetLocation(),
                            method.MethodSymbol.Name,
                            method.MethodSymbol.ReturnType.ToDisplayString()
                        )
                    );
                    continue;
                }

                if (method.Kind != TestMethodKind.Benchmark)
                    continue;

                var parameterCount = method.MethodSymbol.Parameters.Length;
                if (parameterCount > 0 && method.InlineDataCases.IsEmpty)
                {
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            MissingBenchmarkData,
                            method.MethodSyntax.Identifier.GetLocation(),
                            method.MethodSymbol.Name
                        )
                    );
                    continue;
                }

                foreach (
                    var testCase in method.InlineDataCases.Where(
                        testCase => testCase.Arguments.Length != parameterCount
                    )
                )
                {
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidBenchmarkData,
                            method.MethodSyntax.Identifier.GetLocation(),
                            method.MethodSymbol.Name,
                            parameterCount,
                            testCase.Arguments.Length
                        )
                    );
                }
            }
        });

        // Existing tests are discovered from their methods. Explicitly marked classes
        // additionally include public test methods declared by their base classes.
        var testClassesWithMethods = allTestMethods
            .Combine(markedTestClasses.Collect())
            .Select(static (pair, _) =>
                GetTestClassesWithMethods(
                    pair.Left
                        .Where(method => method.HasSupportedReturnType && method.HasValidBenchmarkData)
                        .ToImmutableArray(),
                    pair.Right));

        // Generate metadata file for each test class
        context.RegisterSourceOutput(testClassesWithMethods, static (spc, testClasses) =>
        {
            foreach (var testClass in testClasses)
            {
                GenerateTestClassFile(spc, testClass);
            }
        });

        // Generate TestRegistry.g.cs
        context.RegisterSourceOutput(testClassesWithMethods, static (spc, testClasses) =>
        {
            GenerateTestRegistry(spc, testClasses);
        });

        // NXTest owns the entry point so direct benchmark runs can bypass MTP.
        // Projects with an explicit Main continue to own their startup path.
        var entryPoint = context.CompilationProvider
            .Select(static (compilation, cancellationToken) =>
                (
                    HasRuntime:
                        compilation.GetTypeByMetadataName(
                            "NXTest.Runtime.TestFramework"
                        ) is not null,
                    ShouldGenerate:
                        compilation.Options.OutputKind
                            is OutputKind.ConsoleApplication
                                or OutputKind.WindowsApplication
                        && compilation.GetEntryPoint(cancellationToken) is null
                ));

        context.RegisterSourceOutput(entryPoint, static (spc, entryPoint) =>
        {
            if (entryPoint.HasRuntime && entryPoint.ShouldGenerate)
                GenerateEntryPoint(spc);
        });
    }

    private static TestMethodInfo? GetTestMethodInfo(
        GeneratorAttributeSyntaxContext context,
        TestMethodKind kind,
        CancellationToken ct)
    {
        if (context.TargetSymbol is not IMethodSymbol methodSymbol)
            return null;

        if (context.TargetNode is not MethodDeclarationSyntax methodSyntax)
            return null;

        var containingClass = methodSymbol.ContainingType;
        if (containingClass is null)
            return null;

        return CreateTestMethodInfo(containingClass, methodSymbol, methodSyntax, kind);
    }

    private static TestMethodInfo CreateTestMethodInfo(
        INamedTypeSymbol containingClass,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax,
        TestMethodKind kind)
    {
        ImmutableArray<InlineDataCase> inlineDataCases = ImmutableArray<InlineDataCase>.Empty;

        if (kind is TestMethodKind.Theory or TestMethodKind.Benchmark)
        {
            // Collect all InlineData attributes
            var inlineDataAttributes = methodSymbol.GetAttributes()
                .Where(attr => attr.AttributeClass?.ToDisplayString() == InlineDataAttributeName)
                .ToImmutableArray();

            var casesBuilder = ImmutableArray.CreateBuilder<InlineDataCase>();
            foreach (var attr in inlineDataAttributes)
            {
                if (attr.ConstructorArguments.Length > 0)
                {
                    var paramsArg = attr.ConstructorArguments[0];
                    if (paramsArg.Kind == TypedConstantKind.Array)
                    {
                        casesBuilder.Add(new InlineDataCase(paramsArg.Values));
                    }
                }
            }
            inlineDataCases = casesBuilder.ToImmutable();
        }

        return new TestMethodInfo(
            containingClass,
            methodSymbol,
            methodSyntax,
            kind,
            inlineDataCases);
    }

    private static ImmutableArray<TestClassWithMethods> GetTestClassesWithMethods(
        ImmutableArray<TestMethodInfo> methods,
        ImmutableArray<INamedTypeSymbol> markedTestClasses)
    {
        var candidateClasses = new List<INamedTypeSymbol>();
        var seenClasses = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var method in methods)
        {
            if (seenClasses.Add(method.ContainingClass))
            {
                candidateClasses.Add(method.ContainingClass);
            }
        }

        foreach (var testClass in markedTestClasses)
        {
            if (seenClasses.Add(testClass))
            {
                candidateClasses.Add(testClass);
            }
        }

        var testClasses = ImmutableArray.CreateBuilder<TestClassWithMethods>();
        foreach (var testClass in candidateClasses)
        {
            if (testClass.IsAbstract || HasTypeParameters(testClass))
            {
                continue;
            }

            bool isExplicitlyMarked = markedTestClasses.Any(
                markedClass => SymbolEqualityComparer.Default.Equals(markedClass, testClass));
            var testMethods = isExplicitlyMarked
                ? GetInheritedTestMethods(testClass, methods)
                : methods.Where(method =>
                    SymbolEqualityComparer.Default.Equals(method.ContainingClass, testClass)).ToImmutableArray();

            if (!testMethods.IsEmpty)
            {
                testClasses.Add(new TestClassWithMethods(testClass, testMethods));
            }
        }

        return testClasses.ToImmutable();
    }

    private static bool HasTypeParameters(INamedTypeSymbol testClass)
    {
        for (INamedTypeSymbol? currentClass = testClass;
             currentClass is not null;
             currentClass = currentClass.ContainingType)
        {
            if (!currentClass.TypeParameters.IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<TestMethodInfo> GetInheritedTestMethods(
        INamedTypeSymbol testClass,
        ImmutableArray<TestMethodInfo> methods)
    {
        var classHierarchy = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? currentClass = testClass;
             currentClass is not null;
             currentClass = currentClass.BaseType)
        {
            classHierarchy.Add(currentClass);
        }

        return methods
            .Select(method => GetInheritedTestMethod(testClass, classHierarchy, method))
            .Where(static method => method is not null)
            .Select(static (method, _) => method!)
            .ToImmutableArray();
    }

    private static TestMethodInfo? GetInheritedTestMethod(
        INamedTypeSymbol testClass,
        List<INamedTypeSymbol> classHierarchy,
        TestMethodInfo method)
    {
        foreach (var containingClass in classHierarchy)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    method.ContainingClass.OriginalDefinition,
                    containingClass.OriginalDefinition))
            {
                continue;
            }

            bool isDeclaredOnTestClass = SymbolEqualityComparer.Default.Equals(
                containingClass,
                testClass);
            if (!isDeclaredOnTestClass &&
                (method.MethodSymbol.IsStatic ||
                 method.MethodSymbol.DeclaredAccessibility != Accessibility.Public))
            {
                return null;
            }

            var substitutedMethod = containingClass
                .GetMembers(method.MethodSymbol.Name)
                .OfType<IMethodSymbol>()
                .SingleOrDefault(candidate => SymbolEqualityComparer.Default.Equals(
                    candidate.OriginalDefinition,
                    method.MethodSymbol.OriginalDefinition));

            return substitutedMethod is null
                ? null
                : method.WithMethodSymbol(containingClass, substitutedMethod);
        }

        return null;
    }

    private static void GenerateTestClassFile(
        SourceProductionContext context,
        TestClassWithMethods testClass)
    {
        var className = testClass.TestClass.Name;
        var safeClassName = GetSafeClassName(testClass.TestClass);
        var fileName = $"{safeClassName}_Metadata.g.cs";
        var fqName = "global::" + testClass.TestClass.ToDisplayString();

        var builder = new IndentingBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine("using System.Threading.Tasks;");
        builder.AppendLine("using NXTest;");
        builder.AppendLine("");
        builder.AppendLine("namespace NXTest.Generated");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"internal static class {safeClassName}_Metadata");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("public static TestClassMetadata GetMetadata()");
        builder.AppendLine("{");
        builder.Indent();

        GenerateTestClassMetadata(builder, testClass);

        builder.Dedent();
        builder.AppendLine("}");
        GenerateBenchmarkDispatchMethods(builder, testClass, fqName);
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");

        context.AddSource(fileName, SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void GenerateTestClassMetadata(
        IndentingBuilder builder,
        TestClassWithMethods testClass)
    {
        var className = testClass.TestClass.ToDisplayString();
        var fqName = "global::" + className;
        var isClassStatic = testClass.TestClass.IsStatic;

        builder.AppendLine("return ");
        builder.AppendLine("new TestClassMetadata");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"ClassName = \"{className}\",");

        if (isClassStatic)
        {
            builder.AppendLine("CreateInstance = () => null,");
        }
        else
        {
            builder.AppendLine($"CreateInstance = () => new {fqName}(),");
        }

        GenerateDispatch(builder, testClass, fqName);

        builder.AppendLine("TestMethods = new TestMethodMetadata[]");
        builder.AppendLine("{");
        builder.Indent();

        var benchmarkIndex = 0;
        foreach (var method in testClass.Methods)
        {
            switch (method.Kind)
            {
                case TestMethodKind.Theory:
                    GenerateTheoryMetadata(builder, method);
                    break;
                case TestMethodKind.Benchmark:
                    GenerateBenchmarkMetadata(builder, method, benchmarkIndex++);
                    break;
                case TestMethodKind.Fact:
                    GenerateFactMetadata(builder, method);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown test method kind: {method.Kind}");
            }
        }

        builder.Dedent();
        builder.AppendLine("},");
        builder.Dedent();
        builder.AppendLine("};");
    }

    private static void GenerateDispatch(
        IndentingBuilder builder,
        TestClassWithMethods testClass,
        string fqName)
    {
        var dispatchedMethods = testClass.Methods
            .Where(method => method.Kind != TestMethodKind.Benchmark)
            .ToImmutableArray();
        if (dispatchedMethods.IsEmpty)
        {
            builder.AppendLine("TestDispatch = (_, methodName, _) =>");
            builder.AppendLine("{");
            builder.Indent();
            builder.AppendLine(
                "throw new global::System.InvalidOperationException(\"Unknown test method: \" + methodName);"
            );
            builder.Dedent();
            builder.AppendLine("},");
            return;
        }

        builder.AppendLine("TestDispatch = async (receiver, methodName, theoryArgs) =>");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("switch (methodName)");
        builder.AppendLine("{");
        builder.Indent();

        foreach (var method in dispatchedMethods)
        {
            builder.AppendLine($"case \"{method.MethodSymbol.Name}\":");
            builder.AppendLine("{");
            builder.Indent();
            GenerateInvocation(builder, method, fqName);
            builder.AppendLine("break;");
            builder.Dedent();
            builder.AppendLine("}");
        }

        builder.AppendLine("default:");
        builder.Indent();
        builder.AppendLine(
            "throw new global::System.InvalidOperationException(\"Unknown test method: \" + methodName);");
        builder.Dedent();

        builder.Dedent();
        builder.AppendLine("}");
        builder.AppendLine("await global::System.Threading.Tasks.Task.CompletedTask;");
        builder.Dedent();
        builder.AppendLine("},");
    }

    private static void GenerateInvocation(
        IndentingBuilder builder,
        TestMethodInfo method,
        string fqName)
    {
        var methodName = method.MethodSymbol.Name;
        var isAsync = IsAsyncMethod(method.MethodSymbol);
        var isStatic = method.MethodSymbol.IsStatic;
        var parameters = method.MethodSymbol.Parameters;

        var target = isStatic ? fqName : $"(({fqName})receiver!)";

        var argList = GenerateArgumentUnpacking(
            builder,
            parameters,
            "theoryArgs",
            unpackSingleArgument: false
        );
        var awaitKeyword = isAsync ? "await " : "";
        builder.AppendLine($"{awaitKeyword}{target}.{methodName}({argList});");
    }

    private static string GenerateArgumentUnpacking(
        IndentingBuilder builder,
        ImmutableArray<IParameterSymbol> parameters,
        string argumentsVariable,
        bool unpackSingleArgument)
    {
        if (parameters.Length == 1)
        {
            var argumentExpression =
                $"({parameters[0].Type.ToDisplayString()}){argumentsVariable}!";
            if (!unpackSingleArgument)
                return argumentExpression;

            builder.AppendLine(
                $"var arg = {argumentExpression};"
            );
            return "arg";
        }

        if (parameters.Length > 1)
        {
            // Cast straight to the concrete tuple type so arguments are strongly
            // typed (no per-element unboxing cast via ITuple's object indexer).
            builder.AppendLine(
                $"var args = ({BuildValueTupleType(parameters)}){argumentsVariable}!;"
            );

            var sb = new StringBuilder();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append($"args.Item{i + 1}");
            }
            return sb.ToString();
        }

        return "";
    }

    private static string BuildValueTupleType(ImmutableArray<IParameterSymbol> parameters)
    {
        return $"global::System.ValueTuple<{BuildTupleElementTypes(parameters)}>";
    }

    private static string BuildTupleType(ImmutableArray<IParameterSymbol> parameters)
    {
        return $"({BuildTupleElementTypes(parameters)})";
    }

    private static string BuildTupleElementTypes(ImmutableArray<IParameterSymbol> parameters)
    {
        return string.Join(", ", parameters.Select(p => p.Type.ToDisplayString()));
    }

    private static void GenerateFactMetadata(
        IndentingBuilder builder,
        TestMethodInfo method)
    {
        var methodName = method.MethodSymbol.Name;
        var isAsync = IsAsyncMethod(method.MethodSymbol);
        var isStatic = method.MethodSymbol.IsStatic;

        builder.AppendLine("new TestMethodMetadata.Fact");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"MethodName = \"{methodName}\",");
        builder.AppendLine($"IsAsync = {(isAsync ? "true" : "false")},");
        builder.AppendLine($"IsStatic = {(isStatic ? "true" : "false")},");
        builder.Dedent();
        builder.AppendLine("},");
    }

    private static void GenerateTheoryMetadata(
        IndentingBuilder builder,
        TestMethodInfo method)
    {
        var methodName = method.MethodSymbol.Name;
        var isAsync = IsAsyncMethod(method.MethodSymbol);
        var isStatic = method.MethodSymbol.IsStatic;
        var parameters = method.MethodSymbol.Parameters;

        builder.AppendLine("new TestMethodMetadata.Theory");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"MethodName = \"{methodName}\",");
        builder.AppendLine($"IsAsync = {(isAsync ? "true" : "false")},");
        builder.AppendLine($"IsStatic = {(isStatic ? "true" : "false")},");
        GenerateTestCases(builder, method);
        builder.Dedent();
        builder.AppendLine("},");
    }

    private static void GenerateTestCases(
        IndentingBuilder builder,
        TestMethodInfo method)
    {
        var parameters = method.MethodSymbol.Parameters;

        builder.AppendLine("TestCases = new TestCaseInfo[]");
        builder.AppendLine("{");
        builder.Indent();

        foreach (var testCase in method.InlineDataCases)
        {
            builder.AppendLine("new TestCaseInfo");
            builder.AppendLine("{");
            builder.Indent();

            var args = testCase.Arguments.Select(FormatConstantValue).ToList();
            // 1 arg: box the value directly. 2+ args: cast to a typed tuple. 0 args: null.
            var arguments = args.Count switch
            {
                0 => "null",
                1 => args[0],
                _ => $"({BuildTupleType(parameters)})({string.Join(", ", args)})",
            };
            builder.AppendLine($"Arguments = {arguments},");

            // Generate DisplayName with parameter names and values (e.g., "a: 1, b: 2, expected: 3")
            var displayNameParts = new List<string>();
            for (int i = 0; i < testCase.Arguments.Length && i < parameters.Length; i++)
            {
                var paramName = parameters[i].Name;
                var paramValue = FormatDisplayValue(testCase.Arguments[i]);
                displayNameParts.Add($"{paramName}: {paramValue}");
            }
            var displayName = EscapeString(string.Join(", ", displayNameParts));
            builder.AppendLine($"DisplayName = \"{displayName}\",");

            builder.Dedent();
            builder.AppendLine("},");
        }

        builder.Dedent();
        builder.AppendLine("},");
    }

    private static void GenerateBenchmarkMetadata(
        IndentingBuilder builder,
        TestMethodInfo method,
        int benchmarkIndex)
    {
        var methodName = method.MethodSymbol.Name;
        var isAsync = IsAsyncMethod(method.MethodSymbol);
        var isStatic = method.MethodSymbol.IsStatic;

        builder.AppendLine("new TestMethodMetadata.Benchmark");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"MethodName = \"{methodName}\",");
        builder.AppendLine($"IsAsync = {(isAsync ? "true" : "false")},");
        builder.AppendLine($"IsStatic = {(isStatic ? "true" : "false")},");
        GenerateTestCases(builder, method);
        GenerateBenchmarkDispatch(builder, benchmarkIndex);
        builder.Dedent();
        builder.AppendLine("},");
    }

    private static void GenerateBenchmarkDispatch(
        IndentingBuilder builder,
        int benchmarkIndex)
    {
        builder.AppendLine($"BenchmarkDispatch = BenchmarkDispatch{benchmarkIndex},");
    }

    private static void GenerateBenchmarkDispatchMethods(
        IndentingBuilder builder,
        TestClassWithMethods testClass,
        string fqName)
    {
        var benchmarkIndex = 0;
        foreach (var method in testClass.Methods)
        {
            if (method.Kind == TestMethodKind.Benchmark)
            {
                GenerateBenchmarkDispatchMethod(builder, method, fqName, benchmarkIndex);
                benchmarkIndex++;
            }
        }
    }

    private static void GenerateBenchmarkDispatchMethod(
        IndentingBuilder builder,
        TestMethodInfo method,
        string fqName,
        int benchmarkIndex)
    {
        var methodName = method.MethodSymbol.Name;
        var isAsync = IsAsyncMethod(method.MethodSymbol);
        var target = method.MethodSymbol.IsStatic
            ? fqName
            : $"(({fqName})receiver!)";
        var asyncKeyword = isAsync ? "async " : "";
        var awaitKeyword = isAsync ? "await " : "";

        builder.AppendLine("");
        if (!isAsync)
        {
            builder.AppendLine(
                "[global::System.Runtime.CompilerServices.MethodImpl("
                    + "global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]"
            );
        }
        builder.AppendLine(
            $"private static {asyncKeyword}global::System.Threading.Tasks.Task BenchmarkDispatch{benchmarkIndex}("
                + "object? receiver, object? benchmarkArgs, int invocationCount)"
        );
        builder.AppendLine("{");
        builder.Indent();
        var argList = GenerateArgumentUnpacking(
            builder,
            method.MethodSymbol.Parameters,
            "benchmarkArgs",
            unpackSingleArgument: true
        );
        builder.AppendLine("for (var i = 0; i < invocationCount; i++)");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine($"{awaitKeyword}{target}.{methodName}({argList});");
        builder.Dedent();
        builder.AppendLine("}");
        if (!isAsync)
            builder.AppendLine("return global::System.Threading.Tasks.Task.CompletedTask;");
        builder.Dedent();
        builder.AppendLine("}");
    }

    private static void GenerateTestRegistry(
        SourceProductionContext context,
        ImmutableArray<TestClassWithMethods> testClasses)
    {
        var builder = new IndentingBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("");
        builder.AppendLine("using NXTest;");
        builder.AppendLine("");
        builder.AppendLine("namespace NXTest.Generated");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("internal static class TestRegistry");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("public static TestClassMetadata[] GetAllTests()");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("return new TestClassMetadata[]");
        builder.AppendLine("{");
        builder.Indent();

        foreach (var testClass in testClasses)
        {
            var safeClassName = GetSafeClassName(testClass.TestClass);
            builder.AppendLine($"{safeClassName}_Metadata.GetMetadata(),");
        }

        builder.Dedent();
        builder.AppendLine("};");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");

        context.AddSource("TestRegistry.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    private static void GenerateEntryPoint(SourceProductionContext context)
    {
        var builder = new IndentingBuilder();

        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("");
        builder.AppendLine("namespace NXTest.Generated");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Dispatches direct benchmark runs before Microsoft.Testing.Platform starts.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("internal static class NXTestEntryPoint");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "public static global::System.Threading.Tasks.Task<int> Main(string[] args)"
        );
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "return global::NXTest.Runtime.TestFramework.RunAsync(args, TestRegistry.GetAllTests());"
        );
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");

        context.AddSource(
            "NXTestEntryPoint.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }

    private static bool IsAsyncMethod(IMethodSymbol method)
    {
        return IsTaskReturnType(method);
    }

    private static bool HasSupportedReturnType(IMethodSymbol method)
    {
        return method.ReturnsVoid || IsTaskReturnType(method);
    }

    private static bool IsTaskReturnType(IMethodSymbol method)
    {
        return method.ReturnType is INamedTypeSymbol
        {
            Name: "Task",
            Arity: 0,
        } returnType
            && returnType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
    }

    private static string GetSafeClassName(INamedTypeSymbol type)
    {
        var displayString = type.ToDisplayString();
        return displayString
            .Replace(".", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace(",", "_")
            .Replace(" ", "");
    }

    private static string FormatConstantValue(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return "null";
        }

        return constant.Kind switch
        {
            TypedConstantKind.Primitive => constant.Type?.SpecialType switch
            {
                SpecialType.System_String => $"\"{EscapeString((string)constant.Value!)}\"",
                SpecialType.System_Char => $"'{EscapeChar((char)constant.Value!)}'",
                SpecialType.System_Boolean => constant.Value!.ToString()!.ToLowerInvariant(),
                _ => constant.Value!.ToString()!
            },
            TypedConstantKind.Enum => $"({constant.Type!.ToDisplayString()}){constant.Value}",
            TypedConstantKind.Type => $"typeof({((ITypeSymbol)constant.Value!).ToDisplayString()})",
            _ => constant.Value?.ToString() ?? "null"
        };
    }

    /// <summary>
    /// Formats a constant value for display in test names (human-readable format).
    /// </summary>
    private static string FormatDisplayValue(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return "null";
        }

        return constant.Kind switch
        {
            TypedConstantKind.Primitive => constant.Type?.SpecialType switch
            {
                SpecialType.System_String => $"\\\"{constant.Value}\\\"",
                SpecialType.System_Char => $"'{constant.Value}'",
                SpecialType.System_Boolean => constant.Value!.ToString()!.ToLowerInvariant(),
                _ => constant.Value!.ToString()!
            },
            TypedConstantKind.Enum => constant.Value?.ToString() ?? "null",
            TypedConstantKind.Type => $"typeof({((ITypeSymbol)constant.Value!).ToDisplayString()})",
            _ => constant.Value?.ToString() ?? "null"
        };
    }

    private static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string EscapeChar(char value)
    {
        return value switch
        {
            '\\' => "\\\\",
            '\'' => "\\'",
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            _ => value.ToString()
        };
    }

    // Data model classes
    private enum TestMethodKind
    {
        Fact,
        Theory,
        Benchmark,
    }

    private sealed class TestMethodInfo
    {
        public INamedTypeSymbol ContainingClass { get; }
        public IMethodSymbol MethodSymbol { get; }
        public MethodDeclarationSyntax MethodSyntax { get; }
        public TestMethodKind Kind { get; }
        public ImmutableArray<InlineDataCase> InlineDataCases { get; }
        public bool HasSupportedReturnType { get; }
        public bool HasValidBenchmarkData { get; }

        public TestMethodInfo(
            INamedTypeSymbol containingClass,
            IMethodSymbol methodSymbol,
            MethodDeclarationSyntax methodSyntax,
            TestMethodKind kind,
            ImmutableArray<InlineDataCase> inlineDataCases)
        {
            ContainingClass = containingClass;
            MethodSymbol = methodSymbol;
            MethodSyntax = methodSyntax;
            Kind = kind;
            InlineDataCases = inlineDataCases;
            HasSupportedReturnType = TestGenerator.HasSupportedReturnType(methodSymbol);
            HasValidBenchmarkData =
                kind != TestMethodKind.Benchmark
                || (
                    (methodSymbol.Parameters.IsEmpty || !inlineDataCases.IsEmpty)
                    && inlineDataCases.All(
                        testCase =>
                            testCase.Arguments.Length == methodSymbol.Parameters.Length
                    )
                );
        }

        public TestMethodInfo WithMethodSymbol(
            INamedTypeSymbol containingClass,
            IMethodSymbol methodSymbol)
        {
            return new TestMethodInfo(
                containingClass,
                methodSymbol,
                MethodSyntax,
                Kind,
                InlineDataCases);
        }
    }

    private sealed class InlineDataCase
    {
        public ImmutableArray<TypedConstant> Arguments { get; }

        public InlineDataCase(ImmutableArray<TypedConstant> arguments)
        {
            Arguments = arguments;
        }
    }

    private sealed class TestClassWithMethods
    {
        public INamedTypeSymbol TestClass { get; }
        public ImmutableArray<TestMethodInfo> Methods { get; }

        public TestClassWithMethods(
            INamedTypeSymbol testClass,
            ImmutableArray<TestMethodInfo> methods)
        {
            TestClass = testClass;
            Methods = methods;
        }
    }
}
