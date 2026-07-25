using System;
using System.Collections.Generic;
using System.Diagnostics;
using NXTest;

namespace NXTest.Runtime;

internal static class BenchmarkAnalysis
{
    internal const double InstabilityThreshold = 0.10;

    // The low quantile reported as the "floor": an estimate of the intrinsic
    // cost with the least measurement interference, but more stable across
    // sample counts than the raw minimum.
    internal const double LowerQuantile = 0.10;

    internal static BenchmarkStatistics Calculate(
        double[] samples,
        int operationsPerIteration,
        bool calibrationTargetReached,
        int warmupIterations,
        long totalMeasurementTimestampTicks,
        TestExecutionEngine.BenchmarkGcStatistics gcStatistics = default
    )
    {
        if (samples.Length == 0)
            throw new ArgumentException("At least one benchmark sample is required.", nameof(samples));

        var sortedSamples = (double[])samples.Clone();
        Array.Sort(sortedSamples);

        var summary = CalculateMeanAndVariance(samples);
        var standardDeviation = Math.Sqrt(summary.SampleVariance);
        var median = Percentile(sortedSamples, 0.5);
        var lowerQuantile = Percentile(sortedSamples, LowerQuantile);
        var medianAbsoluteDeviation = MedianAbsoluteDeviation(samples, median);
        var isStable = IsStable(samples);

        var retainedSamples = Array.AsReadOnly((double[])samples.Clone());
        return new BenchmarkStatistics(
            samples.Length,
            operationsPerIteration,
            calibrationTargetReached,
            warmupIterations,
            TimeSpan.FromSeconds(
                (double)totalMeasurementTimestampTicks / Stopwatch.Frequency
            ),
            retainedSamples,
            summary.Mean,
            median,
            lowerQuantile,
            sortedSamples[0],
            sortedSamples[^1],
            standardDeviation,
            medianAbsoluteDeviation,
            isStable,
            gcStatistics.Gen0Collections,
            gcStatistics.Gen1Collections,
            gcStatistics.Gen2Collections,
            gcStatistics.AllocatedBytes
        );
    }

    /// <summary>
    /// Detects non-stationary execution by comparing the median of the first
    /// half of the (chronologically ordered) samples with the median of the
    /// second half. A material difference signals distinct timing regimes,
    /// so the run is reported as unstable rather than as a deceptively precise
    /// mean. Comparing medians of sample groups also blunts the effect of
    /// autocorrelation between adjacent samples.
    /// </summary>
    internal static bool IsStable(IReadOnlyList<double> samples)
    {
        if (samples.Count < 4)
            return true;

        var midpoint = samples.Count / 2;
        var firstHalf = new double[midpoint];
        var secondHalf = new double[samples.Count - midpoint];
        for (var i = 0; i < midpoint; i++)
            firstHalf[i] = samples[i];
        for (var i = midpoint; i < samples.Count; i++)
            secondHalf[i - midpoint] = samples[i];

        Array.Sort(firstHalf);
        Array.Sort(secondHalf);
        var firstMedian = Percentile(firstHalf, 0.5);
        var secondMedian = Percentile(secondHalf, 0.5);
        if (firstMedian <= 0)
            return true;

        return Math.Abs(secondMedian - firstMedian) / firstMedian <= InstabilityThreshold;
    }

    private static double MedianAbsoluteDeviation(IReadOnlyList<double> samples, double median)
    {
        var deviations = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            deviations[i] = Math.Abs(samples[i] - median);
        Array.Sort(deviations);
        return Percentile(deviations, 0.5);
    }

    private static (double Mean, double SampleVariance) CalculateMeanAndVariance(
        IReadOnlyList<double> samples
    )
    {
        double mean = 0;
        double sumOfSquaredDifferences = 0;

        for (var i = 0; i < samples.Count; i++)
        {
            var difference = samples[i] - mean;
            mean += difference / (i + 1);
            sumOfSquaredDifferences += difference * (samples[i] - mean);
        }

        var sampleVariance =
            samples.Count > 1 ? sumOfSquaredDifferences / (samples.Count - 1) : 0;
        return (mean, sampleVariance);
    }

    private static double Percentile(double[] sortedSamples, double percentile)
    {
        var position = (sortedSamples.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sortedSamples[lowerIndex];

        var fraction = position - lowerIndex;
        return sortedSamples[lowerIndex]
            + (sortedSamples[upperIndex] - sortedSamples[lowerIndex]) * fraction;
    }
}
