using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using XunitAssert = Xunit.Assert;

namespace NXTest.Generators.Tests;

public class BasicGenerationTests
{
    [Fact]
    public Task GeneratesMetadataForSimpleFact()
    {
        var source = """
using NXTest;

public class SimpleTests
{
    [Fact]
    public void PassingTest()
    {
        // Test implementation
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForTheoryWithInlineData()
    {
        var source = """
using NXTest;

public class MathTests
{
    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(5, 7, 12)]
    public void AddTest(int a, int b, int sum)
    {
        // Test implementation
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public async Task GeneratesCompilableMetadataForMultiArgumentInlineDataWithNull()
    {
        var source = """
using NXTest;

public class NullableTheoryTests
{
    [Theory]
    [InlineData(null, "bob@example.com", 2)]
    public void SendsEmail(string? displayName, string email, int retryCount)
    {
    }
}
""";
        var compilation = await TestHelpers.CreateCompilation(source);
        var driver = TestHelpers.RunGenerator(compilation);
        var result = driver.GetRunResult();
        var generatedSource = result.GeneratedTrees
            .Single(tree => tree.FilePath.EndsWith("NullableTheoryTests_Metadata.g.cs"))
            .ToString();
        var generatedCompilation = compilation.AddSyntaxTrees(result.GeneratedTrees);
        var compilationErrors = generatedCompilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        if (!generatedSource.Contains(
                "Arguments = ((string?, string, int))(null, \"bob@example.com\", 2),"))
        {
            throw new System.InvalidOperationException("Generated metadata did not cast to a typed tuple.");
        }

        if (compilationErrors.Count != 0)
        {
            throw new System.InvalidOperationException("Generated metadata did not compile.");
        }
    }

    [Fact]
    public Task GeneratesMetadataForMultipleTestMethods()
    {
        var source = """
using NXTest;

public class MixedTests
{
    [Fact]
    public void Test1()
    {
    }

    [Fact]
    public void Test2()
    {
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("world")]
    public void StringTest(string input)
    {
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForAsyncTest()
    {
        var source = """
using System.Threading.Tasks;
using NXTest;

public class AsyncTests
{
    [Fact]
    public async Task AsyncPassingTest()
    {
        await Task.CompletedTask;
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForStaticFactMethod()
    {
        var source = """
using NXTest;

public class StaticMethodTests
{
    [Fact]
    public static void StaticPassingTest()
    {
        // Test implementation
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForStaticTestClass()
    {
        var source = """
using NXTest;

public static class StaticTestClass
{
    [Fact]
    public static void StaticFact()
    {
        // Test implementation
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForStaticTheoryMethod()
    {
        var source = """
using NXTest;

public static class StaticTheoryClass
{
    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(5, 7, 12)]
    public static void StaticAddTest(int a, int b, int sum)
    {
        // Test implementation
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForTestClassWithInheritedTests()
    {
        var source = """
using NXTest;

public abstract class BaseTests
{
    [Fact]
    public void InheritedFact()
    {
    }

    [Theory]
    [InlineData(42)]
    public void InheritedTheory(int value)
    {
    }
}

[TestClass]
public class DerivedTests : BaseTests
{
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesMetadataForTestClassWithGenericInheritedTests()
    {
        var source = """
using NXTest;

public class GenericBaseTests<T>
{
    [Fact]
    public void InheritedFact()
    {
    }

    [Theory]
    [InlineData(42)]
    public void InheritedTheory(T value)
    {
    }
}

[TestClass]
public class DerivedGenericTests : GenericBaseTests<int>
{
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public Task GeneratesSeparateFilesForDifferentClasses()
    {
        var source = """
using NXTest;

public class TestClass1
{
    [Fact]
    public void Test1()
    {
    }
}

public class TestClass2
{
    [Fact]
    public void Test2()
    {
    }
}
""";
        return VerifyGenerator(source);
    }

    [Fact]
    public async Task GeneratesMetadataForBenchmark()
    {
        var source = """
using NXTest;

public class Benchmarks
{
    [Bench]
    [InlineData(16, "ascii")]
    [InlineData(64, "unicode")]
    public void MeasureWork(int size, string encoding)
    {
        Benchmark.Consume(size + encoding.Length);
    }
}
""";
        var compilation = await TestHelpers.CreateCompilation(source);
        var result = TestHelpers.RunGenerator(compilation).GetRunResult();
        var generatedCompilation = compilation.AddSyntaxTrees(result.GeneratedTrees);
        var generatedSource = result.GeneratedTrees
            .Single(tree => tree.FilePath.EndsWith("Benchmarks_Metadata.g.cs"))
            .ToString();

        XunitAssert.DoesNotContain(
            generatedCompilation.GetDiagnostics(default),
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
        );
        XunitAssert.Contains("new TestMethodMetadata.Benchmark", generatedSource);
        XunitAssert.Contains("MethodName = \"MeasureWork\"", generatedSource);
        XunitAssert.Contains("DisplayName = \"size: 16, encoding:", generatedSource);
        XunitAssert.Contains("DisplayName = \"size: 64, encoding:", generatedSource);
        XunitAssert.Contains("BenchmarkDispatch =", generatedSource);
        XunitAssert.Contains("BenchmarkDispatch0", generatedSource);
        XunitAssert.Contains("MethodImplOptions.AggressiveOptimization", generatedSource);
        XunitAssert.Contains(
            "object? receiver, object? benchmarkArgs, int invocationCount",
            generatedSource
        );
        XunitAssert.Contains(
            "var args = (global::System.ValueTuple<int, string>)benchmarkArgs!;",
            generatedSource
        );
        XunitAssert.True(
            generatedSource.IndexOf("var args =", System.StringComparison.Ordinal)
                < generatedSource.IndexOf(
                    "for (var i = 0;",
                    System.StringComparison.Ordinal
                )
        );
        XunitAssert.Contains("MeasureWork(args.Item1, args.Item2)", generatedSource);
        XunitAssert.Contains("i < invocationCount", generatedSource);
    }

    [Fact]
    public async Task ReportsUnsupportedReturnTypes()
    {
        var source = """
using System.Threading.Tasks;
using NXTest;

public class InvalidReturnTypes
{
    [Fact]
    public ValueTask ValueTaskTest() => ValueTask.CompletedTask;

    [Bench]
    public Task<int> GenericTaskBenchmark() => Task.FromResult(1);
}
""";
        var compilation = await TestHelpers.CreateCompilation(source);
        var result = TestHelpers.RunGenerator(compilation).GetRunResult();
        var diagnostics = result.Diagnostics
            .Where(diagnostic => diagnostic.Id == "NXTEST001")
            .ToArray();

        XunitAssert.Equal(2, diagnostics.Length);
        XunitAssert.All(
            diagnostics,
            diagnostic => XunitAssert.Equal(
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
                diagnostic.Severity
            )
        );
        XunitAssert.Contains(
            diagnostics,
            diagnostic => diagnostic.GetMessage().Contains("ValueTaskTest")
        );
        XunitAssert.Contains(
            diagnostics,
            diagnostic => diagnostic.GetMessage().Contains("GenericTaskBenchmark")
        );
    }

    [Fact]
    public async Task ReportsInvalidParameterizedBenchmarkData()
    {
        var source = """
using NXTest;

public class InvalidBenchmarks
{
    [Bench]
    public void MissingData(int size)
    {
    }

    [Bench]
    [InlineData(16)]
    public void WrongArgumentCount(int size, bool pooled)
    {
    }
}
""";
        var compilation = await TestHelpers.CreateCompilation(source);
        var result = TestHelpers.RunGenerator(compilation).GetRunResult();

        XunitAssert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "NXTEST002");
        XunitAssert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "NXTEST003");
        XunitAssert.DoesNotContain(
            result.GeneratedTrees,
            tree => tree.FilePath.EndsWith("InvalidBenchmarks_Metadata.g.cs")
        );
    }

    [Fact]
    public async Task GeneratesEntryPointForExecutableWithoutMain()
    {
        var source = """
using NXTest;

public static class Tests
{
    [Fact]
    public static void Pass() { }
}
""";
        var runtimeReference = MetadataReference.CreateFromFile(
            typeof(NXTest.Runtime.TestFramework).Assembly.Location
        );
        var compilation = await TestHelpers.CreateCompilation(
            source,
            additionalRefs: [runtimeReference],
            outputKind: OutputKind.ConsoleApplication
        );
        var result = TestHelpers.RunGenerator(compilation).GetRunResult();

        XunitAssert.Contains(
            result.GeneratedTrees,
            tree => tree.FilePath.EndsWith("NXTestEntryPoint.g.cs")
        );
        var generatedCompilation = compilation.AddSyntaxTrees(result.GeneratedTrees);
        XunitAssert.DoesNotContain(
            generatedCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
        );
    }

    private static async Task VerifyGenerator(string source, [CallerMemberName] string? testName = null)
    {
        var compilation = await TestHelpers.CreateCompilation(source, assemblyName: testName);

        // Check for compilation errors
        var diagnostics = compilation.GetDiagnostics();
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (errors.Any())
        {
            foreach (var error in errors)
            {
                System.Console.WriteLine($"Compilation Error: {error}");
            }
        }

        var driver = TestHelpers.RunGenerator(compilation);

        // Check generator diagnostics
        var result = driver.GetRunResult();
        foreach (var diag in result.Diagnostics)
        {
            System.Console.WriteLine($"Generator Diagnostic: {diag}");
        }

        System.Console.WriteLine($"Generated {result.GeneratedTrees.Length} files");
        foreach (var tree in result.GeneratedTrees)
        {
            System.Console.WriteLine($"  - {tree.FilePath}");
        }

        // Compile the generated code to check for compilation errors
        var generatedCompilation = compilation.AddSyntaxTrees(result.GeneratedTrees);
        var compilationDiagnostics = generatedCompilation.GetDiagnostics();
        var compilationErrors = compilationDiagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        if (compilationErrors.Any())
        {
            foreach (var error in compilationErrors)
            {
                System.Console.WriteLine($"Generated Code Compilation Error: {error}");
            }
            throw new System.Exception($"Generated code has {compilationErrors.Count} compilation error(s)");
        }

        await GeneratorSnapshotVerifier.Verify(driver, $"{nameof(BasicGenerationTests)}.{testName}");
    }
}
