using System;
using System.Threading;
using System.Threading.Tasks;
using static NXTest.RunResult;

namespace NXTest.Runtime;

/// <summary>
/// Console test runner that executes tests and prints results.
/// </summary>
public static class TestRunner
{
    /// <summary>
    /// Runs tests and prints results to the console.
    /// </summary>
    /// <param name="testClasses">The test classes to execute.</param>
    /// <param name="options">Execution options. If null, uses default options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code: 0 for success, 1 for test failures.</returns>
    internal static async Task<int> RunAsync(
        TestClassMetadata[] testClasses,
        TestExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TestExecutionOptions();

        return await CustomRunAsync(testClasses, options, cancellationToken);
    }

    private static async Task<int> CustomRunAsync(
        TestClassMetadata[] testClasses,
        TestExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var results = await TestExecutionEngine.ExecuteTestsAsync(testClasses, options, cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        int passed = 0, failed = 0, skipped = 0;
        int completedBenchmarks = 0, failedBenchmarks = 0, skippedBenchmarks = 0;
        foreach (var result in results)
        {
            switch (result)
            {
                case TestResult.Passed:
                    passed++;
                    break;
                case TestResult.Failed test:
                    failed++;
                    PrintFailure("test", test.Name, test.ClassName, test.ErrorMessage, test.StackTrace);
                    break;
                case TestResult.Faulted test:
                    failed++;
                    PrintFailure("test", test.Name, test.ClassName, test.ErrorMessage, test.StackTrace);
                    break;
                case TestResult.Skipped:
                    skipped++;
                    break;
                case BenchmarkResult.Completed benchmark:
                    completedBenchmarks++;
                    break;
                case BenchmarkResult.Failed benchmark:
                    failedBenchmarks++;
                    if (!options.RunBenchmarks)
                    {
                        PrintFailure(
                            "benchmark",
                            benchmark.Name,
                            benchmark.ClassName,
                            benchmark.ErrorMessage,
                            benchmark.StackTrace
                        );
                    }
                    break;
                case BenchmarkResult.Skipped:
                    skippedBenchmarks++;
                    break;
            }
        }

        if (options.RunBenchmarks)
        {
            Console.Write(
                BenchmarkTextFormatter.Format(
                    results,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow
                )
            );
        }

        if (!options.RunBenchmarks)
        {
            PrintSummary(
                passed,
                failed,
                skipped,
                completedBenchmarks,
                failedBenchmarks,
                skippedBenchmarks,
                duration
            );
        }

        return failed + failedBenchmarks > 0 ? 1 : 0;
    }

    private static void PrintFailure(
        string resultKind,
        string name,
        string className,
        string? errorMessage,
        string? stackTrace
    )
    {
        Console.WriteLine($"failed {resultKind} {className}.{name}");
        if (!string.IsNullOrEmpty(errorMessage))
            Console.WriteLine($"  {errorMessage}");
        if (!string.IsNullOrEmpty(stackTrace))
            Console.WriteLine(stackTrace);
        Console.WriteLine();
    }

    private static void PrintSummary(
        int passed,
        int failed,
        int skipped,
        int completedBenchmarks,
        int failedBenchmarks,
        int skippedBenchmarks,
        TimeSpan duration
    )
    {
        var statusText = failed + failedBenchmarks > 0 ? "Failed!" : "Passed!";
        var assemblyPath = Environment.ProcessPath ?? string.Empty;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLower();

        // Extract TFM from framework description (e.g., ".NET 8.0.22" -> "net8.0")
        var tfm = "net8.0"; // Default assumption for now

        Console.WriteLine($"Test run summary: {statusText} - {assemblyPath} ({tfm}|{arch})");
        Console.WriteLine($"  total: {passed + failed + skipped}");
        Console.WriteLine($"  failed: {failed}");
        Console.WriteLine($"  succeeded: {passed}");
        Console.WriteLine($"  skipped: {skipped}");
        if (completedBenchmarks + failedBenchmarks + skippedBenchmarks > 0)
        {
            Console.WriteLine($"  benchmarks completed: {completedBenchmarks}");
            Console.WriteLine($"  benchmarks failed: {failedBenchmarks}");
            Console.WriteLine($"  benchmarks skipped: {skippedBenchmarks}");
        }
        Console.WriteLine($"  duration: {duration.TotalMilliseconds:F0}ms");
    }
}
