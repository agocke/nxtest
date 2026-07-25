using System;
using System.Linq;
using NXTest.Runtime;
using static NXTest.RunResult;
using XunitAssert = Xunit.Assert;

namespace NXTest.Runtime.Tests;

public class BenchmarkSummaryFormatterTests
{
    private static BenchmarkResult.Completed Completed(
        string className,
        string name,
        double[] samples,
        TestExecutionEngine.BenchmarkGcStatistics gc = default
    )
    {
        var statistics = BenchmarkAnalysis.Calculate(
            samples,
            operationsPerIteration: 100,
            calibrationTargetReached: true,
            warmupIterations: 5,
            totalMeasurementTimestampTicks: System.Diagnostics.Stopwatch.Frequency,
            gcStatistics: gc
        );
        return new BenchmarkResult.Completed(name, name, className, TimeSpan.FromSeconds(1), statistics);
    }

    [Fact]
    public void FormatSummary_RendersAlignedTableWithHeaders()
    {
        double[] stable = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100];
        var summary = BenchmarkSummaryFormatter.FormatSummary(
        [
            Completed("Bench", "Fast", stable),
            Completed("Bench", "Slow", stable),
        ]);

        XunitAssert.Contains("Benchmark summary", summary);
        XunitAssert.Contains("Median", summary);
        XunitAssert.Contains("Floor (P10)", summary);
        XunitAssert.Contains("Alloc/op", summary);
        XunitAssert.Contains("GC/1k op", summary);
        XunitAssert.DoesNotContain("Batch", summary);
        XunitAssert.Contains("Bench.Fast", summary);
        XunitAssert.Contains("Bench.Slow", summary);
        // A markdown-style separator row is present.
        XunitAssert.Contains("|---", summary);
    }

    [Fact]
    public void FormatSummary_MarksUnstableBenchmarksAndAddsNote()
    {
        double[] drifting = [100, 100, 100, 100, 200, 200, 200, 200, 200, 200];
        var summary = BenchmarkSummaryFormatter.FormatSummary(
        [
            Completed("Bench", "Drifty", drifting),
        ]);

        XunitAssert.Contains("Bench.Drifty*", summary);
        XunitAssert.Contains("Notes:", summary);
        XunitAssert.Contains("unstable", summary);
    }

    [Fact]
    public void FormatSummary_ListsFailedBenchmarks()
    {
        var failed = new BenchmarkResult.Failed(
            "Boom", "Boom", "Bench", TimeSpan.Zero, "kaboom\nsecond line"
        );
        var summary = BenchmarkSummaryFormatter.FormatSummary([failed]);

        XunitAssert.Contains("Failed benchmarks (1)", summary);
        XunitAssert.Contains("Bench.Boom: kaboom", summary);
        // Only the first line of the error message is shown.
        XunitAssert.DoesNotContain("second line", summary);
    }

    [Fact]
    public void TextFormatter_EmitsCommentedSummaryAndRawSamples()
    {
        var runId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var timestamp = DateTimeOffset.Parse("2026-07-22T21:00:00Z");
        var output = BenchmarkTextFormatter.Format(
            [Completed("Bench", "Fast", [100, 101, 99])],
            runId,
            timestamp
        );

        XunitAssert.Contains("# Benchmark summary", output);
        XunitAssert.Contains($"# run: {runId}", output);
        XunitAssert.Contains("# timestamp: 2026-07-22T21:00:00.0000000+00:00", output);
        XunitAssert.DoesNotContain("goos:", output);
        XunitAssert.DoesNotContain("goarch:", output);
        XunitAssert.DoesNotContain("runtime:", output);
        XunitAssert.Contains(
            "# benchmark: BenchmarkFast stable=true calibrated=true warmup=5",
            output
        );
        XunitAssert.Contains(
            "BenchmarkFast 300 100 ns/op 0 B/op 0 gen0/1k-op 0 gen1/1k-op 0 gen2/1k-op",
            output
        );
        XunitAssert.DoesNotContain("# sample:", output);
        XunitAssert.Single(
            output.Split('\n').Where(line => line.StartsWith("BenchmarkFast "))
        );
        XunitAssert.True(
            output.IndexOf("BenchmarkFast 300", StringComparison.Ordinal)
                < output.IndexOf("# Benchmark summary", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void TextFormatter_CommentsFailureDetails()
    {
        var failure = new BenchmarkResult.Failed(
            "Boom",
            "Boom",
            "Bench",
            TimeSpan.Zero,
            "kaboom"
        )
        {
            StackTrace = "at Bench.Boom()",
        };

        var output = BenchmarkTextFormatter.Format(
            [failure],
            Guid.Empty,
            DateTimeOffset.UnixEpoch
        );

        XunitAssert.Contains("# failed benchmark Bench.Boom", output);
        XunitAssert.Contains("# kaboom", output);
        XunitAssert.Contains("# at Bench.Boom()", output);
    }
}
