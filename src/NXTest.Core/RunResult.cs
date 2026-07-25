using System;
using System.Collections.Generic;
using StaticCs;

namespace NXTest;

/// <summary>
/// Union of test and benchmark run results.
/// </summary>
[Closed]
public abstract record RunResult
{
    private RunResult(string id, string name, string className)
    {
        Id = id;
        Name = name;
        ClassName = className;
    }

    public string Id { get; }

    public string Name { get; }

    public string ClassName { get; }

    public string? DisplayName { get; init; }

    public string? ClassDisplayName { get; init; }

    public IReadOnlyList<string> OutputLines { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Represents the closed set of test execution results.
    /// </summary>
    [Closed]
    public abstract record TestResult : RunResult
    {
        private TestResult(string id, string name, string className)
            : base(id, name, className) { }

        public object?[]? TestCaseArguments { get; init; }

        public sealed record Passed(
            string Id,
            string Name,
            string ClassName,
            TimeSpan Duration
        ) : TestResult(Id, Name, ClassName);

        public sealed record Failed(
            string Id,
            string Name,
            string ClassName,
            TimeSpan Duration,
            string ErrorMessage
        ) : TestResult(Id, Name, ClassName)
        {
            public string? StackTrace { get; init; }
        }

        public sealed record Skipped(
            string Id,
            string Name,
            string ClassName,
            string Reason
        ) : TestResult(Id, Name, ClassName);

        public sealed record Faulted(
            string Id,
            string Name,
            string ClassName,
            string ErrorMessage
        ) : TestResult(Id, Name, ClassName)
        {
            public string? StackTrace { get; init; }
        }
    }

    /// <summary>
    /// Represents the closed set of benchmark execution results.
    /// </summary>
    [Closed]
    public abstract record BenchmarkResult : RunResult
    {
        private BenchmarkResult(string id, string name, string className)
            : base(id, name, className) { }

        public sealed record Completed(
            string Id,
            string Name,
            string ClassName,
            TimeSpan RunDuration,
            BenchmarkStatistics Statistics
        ) : BenchmarkResult(Id, Name, ClassName);

        public sealed record Failed(
            string Id,
            string Name,
            string ClassName,
            TimeSpan RunDuration,
            string ErrorMessage
        ) : BenchmarkResult(Id, Name, ClassName)
        {
            public string? StackTrace { get; init; }
        }

        public sealed record Skipped(
            string Id,
            string Name,
            string ClassName,
            string Reason
        ) : BenchmarkResult(Id, Name, ClassName);
    }
}

/// <summary>
/// Summarizes the measured iterations of a benchmark.
/// </summary>
public sealed record BenchmarkStatistics(
    int Iterations,
    int OperationsPerIteration,
    bool CalibrationTargetReached,
    int WarmupIterations,
    TimeSpan TotalMeasurementTime,
    IReadOnlyList<double> SamplesNanoseconds,
    double MeanNanoseconds,
    double MedianNanoseconds,
    double LowerQuantileNanoseconds,
    double MinimumNanoseconds,
    double MaximumNanoseconds,
    double StandardDeviationNanoseconds,
    double MedianAbsoluteDeviationNanoseconds,
    bool IsStable,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long AllocatedBytes
);
