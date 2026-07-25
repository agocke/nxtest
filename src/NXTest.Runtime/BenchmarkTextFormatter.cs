using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using static NXTest.RunResult;

namespace NXTest.Runtime;

internal static class BenchmarkTextFormatter
{
    internal static string Format(
        IReadOnlyList<RunResult> results,
        Guid runId,
        DateTimeOffset timestampUtc
    )
    {
        var builder = new StringBuilder();
        builder.AppendLine("# nxbench: 1");
        builder.AppendLine($"# run: {runId}");
        builder.AppendLine($"# timestamp: {timestampUtc:O}");

        foreach (var result in results)
        {
            if (result is BenchmarkResult.Failed failed)
                AppendCommentedFailure(builder, failed);
        }

        foreach (var result in results)
        {
            if (result is not BenchmarkResult.Completed completed)
                continue;

            var statistics = completed.Statistics;
            var totalOperations =
                (long)statistics.Iterations * statistics.OperationsPerIteration;
            var bytesPerOperation = totalOperations > 0
                ? statistics.AllocatedBytes / (double)totalOperations
                : 0;
            var gen0PerThousand = PerThousand(
                statistics.Gen0Collections,
                totalOperations
            );
            var gen1PerThousand = PerThousand(
                statistics.Gen1Collections,
                totalOperations
            );
            var gen2PerThousand = PerThousand(
                statistics.Gen2Collections,
                totalOperations
            );
            var benchmarkName = "Benchmark" + EscapeName(completed.Id);
            builder.AppendLine(
                $"# benchmark: {benchmarkName} stable={statistics.IsStable.ToString().ToLowerInvariant()} "
                    + $"calibrated={statistics.CalibrationTargetReached.ToString().ToLowerInvariant()} "
                    + $"warmup={statistics.WarmupIterations}"
            );

            builder.Append(benchmarkName);
            builder.Append(' ');
            builder.Append(totalOperations.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(
                statistics.MeanNanoseconds.ToString("R", CultureInfo.InvariantCulture)
            );
            builder.Append(" ns/op ");
            builder.Append(
                bytesPerOperation.ToString("R", CultureInfo.InvariantCulture)
            );
            builder.Append(" B/op");
            AppendMetric(builder, gen0PerThousand, "gen0/1k-op");
            AppendMetric(builder, gen1PerThousand, "gen1/1k-op");
            AppendMetric(builder, gen2PerThousand, "gen2/1k-op");
            builder.AppendLine();
        }

        builder.AppendLine("#");
        AppendCommentedSummary(builder, results);
        return builder.ToString();
    }

    private static void AppendCommentedFailure(
        StringBuilder builder,
        BenchmarkResult.Failed failure
    )
    {
        builder.AppendLine(
            $"# failed benchmark {failure.ClassDisplayName ?? failure.ClassName}.{failure.Name}"
        );
        AppendCommentedLines(builder, failure.ErrorMessage);
        AppendCommentedLines(builder, failure.StackTrace);
        builder.AppendLine("#");
    }

    private static void AppendCommentedLines(StringBuilder builder, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        foreach (var line in value.Split('\n'))
            builder.Append("# ").AppendLine(line.TrimEnd('\r'));
    }

    private static void AppendCommentedSummary(
        StringBuilder builder,
        IReadOnlyList<RunResult> results
    )
    {
        var benchmarks = new List<BenchmarkResult>();
        foreach (var result in results)
        {
            if (result is BenchmarkResult benchmark)
                benchmarks.Add(benchmark);
        }

        var summary = BenchmarkSummaryFormatter.FormatSummary(benchmarks);
        foreach (var line in summary.Split('\n'))
        {
            builder.Append("#");
            if (line.Length > 0)
                builder.Append(' ').Append(line.TrimEnd('\r'));
            builder.AppendLine();
        }
    }

    private static string EscapeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character == '%' || char.IsWhiteSpace(character) || char.IsControl(character))
            {
                builder.Append('%');
                builder.Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static double PerThousand(int collections, long operations) =>
        operations > 0 ? collections * 1_000d / operations : 0;

    private static void AppendMetric(StringBuilder builder, double value, string unit)
    {
        builder.Append(' ');
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(unit);
    }

}
