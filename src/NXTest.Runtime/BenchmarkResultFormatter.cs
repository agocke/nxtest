using System.Globalization;

namespace NXTest.Runtime;

internal static class BenchmarkResultFormatter
{
    public static string Format(BenchmarkStatistics statistics)
    {
        var totalOperations =
            (long)statistics.Iterations * statistics.OperationsPerIteration;
        var bytesPerOperation = totalOperations > 0
            ? statistics.AllocatedBytes / (double)totalOperations
            : 0d;
        var gen0Per1kOperations = CollectionsPerThousandOperations(
            statistics.Gen0Collections, totalOperations);
        var gen1Per1kOperations = CollectionsPerThousandOperations(
            statistics.Gen1Collections, totalOperations);
        var gen2Per1kOperations = CollectionsPerThousandOperations(
            statistics.Gen2Collections, totalOperations);

        return $"Median: {FormatNanoseconds(statistics.MedianNanoseconds)}; "
            + $"Floor (P{(int)(BenchmarkAnalysis.LowerQuantile * 100)}): "
            + $"{FormatNanoseconds(statistics.LowerQuantileNanoseconds)}; "
            + $"MAD: {FormatNanoseconds(statistics.MedianAbsoluteDeviationNanoseconds)}; "
            + $"Min: {FormatNanoseconds(statistics.MinimumNanoseconds)}; "
            + $"Max: {FormatNanoseconds(statistics.MaximumNanoseconds)}; "
            + $"Samples: {statistics.Iterations}; "
            + $"Operations/iteration: {statistics.OperationsPerIteration}; "
            + $"Allocated: {FormatBytesPerOperation(bytesPerOperation)}; "
            + $"GC/1k op: {FormatCollectionsPerThousand(gen0Per1kOperations)}/"
            + $"{FormatCollectionsPerThousand(gen1Per1kOperations)}/"
            + $"{FormatCollectionsPerThousand(gen2Per1kOperations)}"
            + (
                statistics.CalibrationTargetReached
                    ? ""
                    : "; Warning: calibration reached the operation limit before the target duration"
            )
            + (
                statistics.IsStable
                    ? ""
                    : "; Warning: unstable timing detected (distinct regimes across samples); treat the results with caution"
            );
    }

    public static string FormatNanoseconds(double nanoseconds)
    {
        if (nanoseconds < 1_000)
            return nanoseconds.ToString("F2", CultureInfo.InvariantCulture) + " ns";
        if (nanoseconds < 1_000_000)
            return (nanoseconds / 1_000).ToString("F2", CultureInfo.InvariantCulture) + " us";
        if (nanoseconds < 1_000_000_000)
            return (nanoseconds / 1_000_000).ToString("F2", CultureInfo.InvariantCulture) + " ms";

        return (nanoseconds / 1_000_000_000).ToString("F2", CultureInfo.InvariantCulture) + " s";
    }

    public static string FormatBytesPerOperation(double bytesPerOperation)
    {
        if (bytesPerOperation < 1_024)
            return bytesPerOperation.ToString("F2", CultureInfo.InvariantCulture) + " B/op";
        if (bytesPerOperation < 1_024 * 1_024)
            return (bytesPerOperation / 1_024).ToString("F2", CultureInfo.InvariantCulture) + " KB/op";

        return (bytesPerOperation / (1_024d * 1_024d)).ToString("F2", CultureInfo.InvariantCulture) + " MB/op";
    }

    private static double CollectionsPerThousandOperations(int collections, long totalOperations) =>
        totalOperations > 0 ? collections * 1_000d / totalOperations : 0d;

    private static string FormatCollectionsPerThousand(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);
}
