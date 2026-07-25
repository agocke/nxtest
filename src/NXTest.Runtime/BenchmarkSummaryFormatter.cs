using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static NXTest.RunResult;

namespace NXTest.Runtime;

/// <summary>
/// Renders a compact, BenchmarkDotNet-style summary table for a set of
/// benchmark results. This is the primary presentation of benchmark output;
/// the run itself stays quiet and the table is printed once at the end.
/// </summary>
internal static class BenchmarkSummaryFormatter
{
    private const string UnstableMarker = "*";

    public static string FormatSummary(IReadOnlyList<BenchmarkResult> results)
    {
        var completed = results.OfType<BenchmarkResult.Completed>().ToList();
        var failed = results.OfType<BenchmarkResult.Failed>().ToList();
        var skipped = results.OfType<BenchmarkResult.Skipped>().ToList();

        var builder = new StringBuilder();
        builder.AppendLine("Benchmark summary");

        if (completed.Count > 0)
        {
            builder.AppendLine();
            AppendTable(builder, completed);
        }

        var anyUnstable = completed.Any(c => !c.Statistics.IsStable);
        var anyCalibrationCapped = completed.Any(c => !c.Statistics.CalibrationTargetReached);

        if (anyUnstable || anyCalibrationCapped)
        {
            builder.AppendLine();
            builder.AppendLine("Notes:");
            if (anyUnstable)
                builder.AppendLine(
                    $"  {UnstableMarker} unstable: distinct timing regimes were detected across samples; interpret with caution."
                );
            if (anyCalibrationCapped)
                builder.AppendLine(
                    "  ~ calibration capped: the operation limit was reached before the target sample duration."
                );
        }

        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Failed benchmarks ({failed.Count}):");
            foreach (var failure in failed)
            {
                var message = failure.ErrorMessage.Split('\n').FirstOrDefault()?.Trim();
                builder.AppendLine($"  ✗ {Name(failure)}: {message}");
            }
        }

        if (skipped.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Skipped benchmarks ({skipped.Count}):");
            foreach (var skip in skipped)
                builder.AppendLine($"  ⊝ {Name(skip)}: {skip.Reason}");
        }

        return builder.ToString();
    }

    private static void AppendTable(
        StringBuilder builder,
        IReadOnlyList<BenchmarkResult.Completed> completed
    )
    {
        var floorHeader = $"Floor (P{(int)(BenchmarkAnalysis.LowerQuantile * 100)})";
        string[] headers =
        [
            "Benchmark",
            "Median",
            floorHeader,
            "MAD",
            "Alloc/op",
            "GC/1k op",
        ];

        var rows = new List<string[]>();
        foreach (var result in completed)
        {
            var statistics = result.Statistics;
            var totalOperations =
                (long)statistics.Iterations * statistics.OperationsPerIteration;
            var bytesPerOperation = totalOperations > 0
                ? statistics.AllocatedBytes / (double)totalOperations
                : 0d;

            var flags = FlagSuffix(statistics);
            rows.Add(
            [
                Name(result) + flags,
                BenchmarkResultFormatter.FormatNanoseconds(statistics.MedianNanoseconds),
                BenchmarkResultFormatter.FormatNanoseconds(statistics.LowerQuantileNanoseconds),
                BenchmarkResultFormatter.FormatNanoseconds(statistics.MedianAbsoluteDeviationNanoseconds),
                BenchmarkResultFormatter.FormatBytesPerOperation(bytesPerOperation),
                FormatGcColumn(statistics, totalOperations),
            ]);
        }

        var widths = new int[headers.Length];
        for (var column = 0; column < headers.Length; column++)
        {
            widths[column] = headers[column].Length;
            foreach (var row in rows)
                widths[column] = Math.Max(widths[column], row[column].Length);
        }

        // The first column (benchmark name) is left-aligned; numeric columns
        // are right-aligned so magnitudes line up.
        AppendRow(builder, headers, widths, leftAlignFirst: true);
        AppendSeparator(builder, widths);
        foreach (var row in rows)
            AppendRow(builder, row, widths, leftAlignFirst: true);
    }

    private static void AppendRow(
        StringBuilder builder,
        string[] cells,
        int[] widths,
        bool leftAlignFirst
    )
    {
        builder.Append("| ");
        for (var column = 0; column < cells.Length; column++)
        {
            var cell = cells[column];
            var padded = column == 0 && leftAlignFirst
                ? cell.PadRight(widths[column])
                : cell.PadLeft(widths[column]);
            builder.Append(padded);
            builder.Append(column == cells.Length - 1 ? " |" : " | ");
        }
        builder.AppendLine();
    }

    private static void AppendSeparator(StringBuilder builder, int[] widths)
    {
        builder.Append('|');
        foreach (var width in widths)
        {
            builder.Append(new string('-', width + 2));
            builder.Append('|');
        }
        builder.AppendLine();
    }

    private static string FormatGcColumn(BenchmarkStatistics statistics, long totalOperations)
    {
        string Per1k(int collections) =>
            (totalOperations > 0 ? collections * 1_000d / totalOperations : 0d)
                .ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        return $"{Per1k(statistics.Gen0Collections)}/"
            + $"{Per1k(statistics.Gen1Collections)}/"
            + $"{Per1k(statistics.Gen2Collections)}";
    }

    private static string FlagSuffix(BenchmarkStatistics statistics)
    {
        var suffix = "";
        if (!statistics.IsStable)
            suffix += UnstableMarker;
        if (!statistics.CalibrationTargetReached)
            suffix += "~";
        return suffix;
    }

    private static string Name(BenchmarkResult result) =>
        $"{result.ClassDisplayName ?? result.ClassName}.{result.Name}";
}
