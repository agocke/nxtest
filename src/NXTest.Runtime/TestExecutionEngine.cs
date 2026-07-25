using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static NXTest.RunResult;

namespace NXTest.Runtime;

/// <summary>
/// Core test execution engine. Takes test metadata and produces test results.
/// This is a pure function with no presentation logic.
/// </summary>
public static class TestExecutionEngine
{
    private const int BenchmarkMinimumWarmupIterationCount = 6;
    private const int BenchmarkMaximumWarmupIterationCount = 50;
    private const int BenchmarkWarmupStabilityWindow = 6;
    private const int BenchmarkFinalBatchSettlingIterationCount = 2;
    private const int BenchmarkMeasurementSampleCount = 10;
    private const double BenchmarkWarmupStabilityThreshold = 0.05;
    private const int BenchmarkMaximumOperationsPerIteration = 1 << 24;
    private static readonly TimeSpan BenchmarkPilotTargetTime =
        TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan BenchmarkMeasurementTargetTime =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BenchmarkMeasurementDuration =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BenchmarkCalibrationResolutionTime =
        TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan BenchmarkMaximumWarmupTime =
        TimeSpan.FromSeconds(10);

    /// <summary>
    /// Executes all tests and returns results.
    /// </summary>
    /// <param name="testClasses">The test class metadata with execution delegates.</param>
    /// <param name="options">Execution options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of test results.</returns>
    public static async Task<RunResult[]> ExecuteTestsAsync(
        IReadOnlyList<TestClassMetadata> testClasses,
        TestExecutionOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var allTests = CollectAllTests(testClasses);
        if (options.RunBenchmarks)
        {
            allTests.RemoveAll(
                test => test.Method is not TestMethodMetadata.Benchmark
            );
            SortBenchmarks(allTests);
            return await ExecuteSequentiallyAsync(allTests, options, cancellationToken);
        }

        allTests.RemoveAll(test => test.Method is TestMethodMetadata.Benchmark);
        return options.Mode switch
        {
            ParallelMode.None => await ExecuteSequentiallyAsync(allTests, options, cancellationToken),
            ParallelMode.Classes => await ExecuteClassesInParallelAsync(allTests, options, cancellationToken),
            _ => await ExecuteTestsInParallelAsync(allTests, options, cancellationToken),
        };
    }

    internal static List<TestDescriptor> CollectAllTests(
        IReadOnlyList<TestClassMetadata> testClasses
    )
    {
        var tests = new List<TestDescriptor>();

        foreach (var testClass in testClasses)
        {
            foreach (var testMethod in testClass.TestMethods)
            {
                // Pattern match on Fact vs Theory
                switch (testMethod)
                {
                    case TestMethodMetadata.Fact fact:
                        // Fact: Execute once
                        tests.Add(
                            new TestDescriptor
                            {
                                Method = fact,
                                Class = testClass,
                            }
                        );
                        break;

                    case TestMethodMetadata.Theory theory:
                        // Theory: a single descriptor; ExecuteTestAsync expands the cases.
                        tests.Add(
                            new TestDescriptor
                            {
                                Method = theory,
                                Class = testClass,
                            }
                        );
                        break;

                    case TestMethodMetadata.Benchmark benchmark:
                        if (benchmark.TestCases.Count == 0)
                        {
                            tests.Add(
                                new TestDescriptor
                                {
                                    Method = benchmark,
                                    Class = testClass,
                                }
                            );
                        }
                        else
                        {
                            foreach (var testCase in benchmark.TestCases)
                            {
                                tests.Add(
                                    new TestDescriptor
                                    {
                                        Method = benchmark,
                                        Class = testClass,
                                        BenchmarkCase = testCase,
                                    }
                                );
                            }
                        }
                        break;
                }
            }
        }

        // Randomly permute tests on each run to surface accidental ordering dependencies.
        Shuffle(tests);

        return tests;
    }

    private static void Shuffle(List<TestDescriptor> tests)
    {
        // Fisher-Yates shuffle using a thread-safe shared RNG.
        for (int i = tests.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (tests[i], tests[j]) = (tests[j], tests[i]);
        }
    }

    internal static void SortBenchmarks(List<TestDescriptor> benchmarks)
    {
        benchmarks.Sort(
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName)
        );
    }

    private static async Task<RunResult[]> ExecuteSequentiallyAsync(
        List<TestDescriptor> tests,
        TestExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        var allResults = new List<RunResult>(tests.Count);

        foreach (var test in tests)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var results = await ExecuteTestAsync(test, options, cancellationToken);
            allResults.AddRange(results);

            if (options.StopOnFirstFailure && results.Any(IsFailure))
                break;
        }

        return allResults.ToArray();
    }

    private static async Task<RunResult[]> ExecuteTestsInParallelAsync(
        List<TestDescriptor> tests,
        TestExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        var allResults = new System.Collections.Concurrent.ConcurrentBag<RunResult>();
        var shouldStop = false;

        await Parallel.ForEachAsync(
            tests,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            async (test, ct) =>
            {
                if (options.StopOnFirstFailure && shouldStop)
                    return;

                var results = await ExecuteTestAsync(test, options, ct);
                foreach (var result in results)
                {
                    allResults.Add(result);
                }

                if (options.StopOnFirstFailure && results.Any(IsFailure))
                    shouldStop = true;
            }
        );

        return allResults.OrderBy(r => r.Id).ToArray();
    }

    private static async Task<RunResult[]> ExecuteClassesInParallelAsync(
        List<TestDescriptor> tests,
        TestExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        // Classes run in parallel; tests within a class run sequentially.
        var classGroups = tests.GroupBy(t => t.ClassName, StringComparer.Ordinal);

        var allResults = new System.Collections.Concurrent.ConcurrentBag<RunResult>();
        var shouldStop = false;

        await Parallel.ForEachAsync(
            classGroups,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken,
            },
            async (group, ct) =>
            {
                foreach (var test in group)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (options.StopOnFirstFailure && shouldStop)
                        break;

                    var results = await ExecuteTestAsync(test, options, ct);
                    foreach (var result in results)
                    {
                        allResults.Add(result);

                        if (options.StopOnFirstFailure && IsFailure(result))
                            shouldStop = true;
                    }
                }
            }
        );

        return allResults.OrderBy(r => r.Id).ToArray();
    }

    internal static async Task<RunResult[]> ExecuteTestAsync(
        TestDescriptor test,
        TestExecutionOptions options,
        CancellationToken cancellationToken
    )
    {
        var testId = GenerateTestId(test);
        var methodName = test.Method.MethodName;
        var resultName = test.BenchmarkCase is { } benchmarkCase
            ? $"{methodName}({benchmarkCase.DisplayName})"
            : methodName;

        // Check if test method should be skipped
        if (test.Method.Skip)
        {
            return
            [
                test.Method is TestMethodMetadata.Benchmark
                    ? new BenchmarkResult.Skipped(
                        testId,
                        resultName,
                        test.ClassName,
                        test.Method.SkipReason ?? "Benchmark marked as skipped"
                    )
                    : new TestResult.Skipped(
                        testId,
                        methodName,
                        test.ClassName,
                        test.Method.SkipReason ?? "Test marked as skipped"
                    )
            ];
        }

        object? testClassInstance = null;
        if (!test.Method.IsStatic)
        {
            try
            {
                testClassInstance = test.Class.CreateInstance();
            }
            catch (Exception ex)
            {
                // If the delegate throws, create a fault result
                return
                [
                    test.Method is TestMethodMetadata.Benchmark
                        ? new BenchmarkResult.Failed(
                            testId,
                            resultName,
                            test.ClassName,
                            TimeSpan.Zero,
                            ex.Message
                        )
                        {
                            StackTrace = ex.StackTrace,
                        }
                        : new TestResult.Faulted(
                            testId,
                            methodName,
                            test.ClassName,
                            ex.Message
                        )
                        {
                            StackTrace = ex.StackTrace,
                        }
                ];
            }
        }

        // Pattern match on Fact vs Theory
        try
        {
            switch (test.Method)
            {
                case TestMethodMetadata.Fact fact:
                    {
                        return
                        [
                            await RunTest(
                                testId,
                                methodName,
                                test.Class.TestDispatch,
                                testClassInstance,
                                methodName,
                                null,
                                test.ClassName,
                                cancellationToken
                            )
                        ];
                    }

                case TestMethodMetadata.Theory theory:
                    var cases = theory.TestCases;
                    var results = new RunResult[cases.Count];
                    for (int i = 0; i < cases.Count; i++)
                    {
                        // Match xUnit's per-case naming: MethodName(displayName)
                        var caseName = $"{methodName}({cases[i].DisplayName})";
                        var caseId = $"{testId}({cases[i].DisplayName})";
                        results[i] = await RunTest(
                            caseId,
                            caseName,
                            test.Class.TestDispatch,
                            testClassInstance,
                            methodName,
                            cases[i].Arguments,
                            test.ClassName,
                            cancellationToken
                        );
                    }
                    return results;

                case TestMethodMetadata.Benchmark benchmark:
                    return
                    [
                        await RunBenchmark(
                            testId,
                            resultName,
                            benchmark.BenchmarkDispatch,
                            testClassInstance,
                            test.BenchmarkCase?.Arguments,
                            test.ClassName,
                            cancellationToken
                        )
                    ];

                default:
                    throw new InvalidOperationException(
                        $"Unknown test method type: {test.Method.GetType()}"
                    );
            }
        }
        finally
        {
            if (testClassInstance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<TestResult> RunTest(
        string testId,
        string testName,
        TestClassMetadata.DispatchFunc dispatch,
        object? testClassInstance,
        string methodName,
        object? theoryArgs,
        string className,
        CancellationToken cancellationToken
    )
    {
        var sw = new Stopwatch();
        sw.Start();
        if (testClassInstance is TestBase testBase)
        {
            testBase.SetCts(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        }

        try
        {
            await dispatch(testClassInstance, methodName, theoryArgs);
        }
        catch (Exception ex)
        {
            // If the delegate throws, create a failure result
            sw.Stop();
            return new TestResult.Failed(
                testId,
                testName,
                className,
                sw.Elapsed,
                ex.Message
            )
            {
                StackTrace = ex.StackTrace,
            };
        }
        sw.Stop();
        return new TestResult.Passed(
            testId,
            testName,
            className,
            sw.Elapsed
        );
    }

    private static async Task<BenchmarkResult> RunBenchmark(
        string testId,
        string testName,
        TestMethodMetadata.Benchmark.DispatchFunc dispatch,
        object? testClassInstance,
        object? benchmarkArguments,
        string className,
        CancellationToken cancellationToken
    )
    {
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            if (testClassInstance is TestBase testBase)
            {
                testBase.SetCts(
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            RuntimeHelpers.PrepareDelegate(dispatch);
            await dispatch(testClassInstance, benchmarkArguments, 1);

            var calibration = await CalibrateOperationsPerIteration(
                dispatch,
                testClassInstance,
                benchmarkArguments,
                cancellationToken
            );

            var pilotWarmup = await WarmUp(
                dispatch,
                testClassInstance,
                benchmarkArguments,
                calibration.OperationsPerIteration,
                cancellationToken
            );
            var warmupIterations = pilotWarmup.Iterations;

            var recalibration = RecalibrateOperations(
                calibration.OperationsPerIteration,
                pilotWarmup.StableNanosecondsPerOperation,
                BenchmarkMeasurementTargetTime
            );
            var operationsPerIteration = recalibration.OperationsPerIteration;
            var calibrationTargetReached =
                calibration.TargetReached && recalibration.TargetReached;

            // Final-sized batches can trigger another Dynamic PGO transition.
            // Correct the operation count after each discarded settling batch.
            for (
                var iteration = 0;
                iteration < BenchmarkFinalBatchSettlingIterationCount;
                iteration++
            )
            {
                var settling = await CollectSamples(
                    dispatch,
                    testClassInstance,
                    benchmarkArguments,
                    operationsPerIteration,
                    new double[1],
                    cancellationToken
                );
                warmupIterations++;
                recalibration = RecalibrateOperations(
                    operationsPerIteration,
                    settling.Samples[0],
                    BenchmarkMeasurementTargetTime
                );
                operationsPerIteration = recalibration.OperationsPerIteration;
                calibrationTargetReached &= recalibration.TargetReached;
            }

            MeasurementEpoch measurement = default;
            for (var epoch = 0; epoch < 2; epoch++)
            {
                if (epoch == 1)
                {
                    // A shifted first epoch violates stationarity. Discard it,
                    // warm at the already-selected plan, and retry exactly once.
                    var retryWarmup = await WarmUp(
                        dispatch,
                        testClassInstance,
                        benchmarkArguments,
                        operationsPerIteration,
                        cancellationToken
                    );
                    warmupIterations += retryWarmup.Iterations;
                }

                measurement = await CollectMeasurementEpoch(
                    dispatch,
                    testClassInstance,
                    benchmarkArguments,
                    operationsPerIteration,
                    cancellationToken
                );

                if (
                    BenchmarkAnalysis.IsStable(measurement.Measurement.Samples)
                    || epoch == 1
                )
                    break;
            }

            totalStopwatch.Stop();
            return new BenchmarkResult.Completed(
                testId,
                testName,
                className,
                totalStopwatch.Elapsed,
                BenchmarkAnalysis.Calculate(
                    measurement.Measurement.Samples,
                    operationsPerIteration,
                    calibrationTargetReached,
                    warmupIterations,
                    measurement.Measurement.TotalTimestampTicks,
                    measurement.GcStatistics
                )
            );
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            return new BenchmarkResult.Failed(
                testId,
                testName,
                className,
                totalStopwatch.Elapsed,
                ex.Message
            )
            {
                StackTrace = ex.StackTrace,
            };
        }
    }

    private static async Task<CalibrationResult> CalibrateOperationsPerIteration(
        TestMethodMetadata.Benchmark.DispatchFunc dispatch,
        object? testClassInstance,
        object? benchmarkArguments,
        CancellationToken cancellationToken
    )
    {
        var operationsPerIteration = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startTimestamp = Stopwatch.GetTimestamp();
            await dispatch(
                testClassInstance,
                benchmarkArguments,
                operationsPerIteration
            );
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

            if (
                elapsed >= BenchmarkPilotTargetTime
                || operationsPerIteration >= BenchmarkMaximumOperationsPerIteration
            )
            {
                return new CalibrationResult(
                    operationsPerIteration,
                    elapsed >= BenchmarkPilotTargetTime
                );
            }

            operationsPerIteration = CalculateNextOperationCount(
                operationsPerIteration,
                elapsed
            );
        }
    }

    private static async Task<MeasurementResult> CollectSamples(
        TestMethodMetadata.Benchmark.DispatchFunc dispatch,
        object? testClassInstance,
        object? benchmarkArguments,
        int operationsPerIteration,
        double[] samples,
        CancellationToken cancellationToken
    )
    {
        long totalMeasurementTimestampTicks = 0;

        for (var sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startTimestamp = Stopwatch.GetTimestamp();
            await dispatch(testClassInstance, benchmarkArguments, operationsPerIteration);
            var elapsedTimestampTicks = Stopwatch.GetTimestamp() - startTimestamp;
            totalMeasurementTimestampTicks += elapsedTimestampTicks;
            samples[sampleIndex] =
                elapsedTimestampTicks
                * (1_000_000_000d / Stopwatch.Frequency)
                / operationsPerIteration;
        }

        return new MeasurementResult(samples, totalMeasurementTimestampTicks);
    }

    private static async Task<MeasurementEpoch> CollectMeasurementEpoch(
        TestMethodMetadata.Benchmark.DispatchFunc dispatch,
        object? testClassInstance,
        object? benchmarkArguments,
        int operationsPerIteration,
        CancellationToken cancellationToken
    )
    {
        // Avoid counting runner bookkeeping if a late speedup requires extra samples.
        var samples = new List<double>(256);
        var targetTimestampTicks = (long)(
            BenchmarkMeasurementDuration.TotalSeconds * Stopwatch.Frequency
        );
        ForceFullCollection();
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        long totalMeasurementTimestampTicks = 0;
        while (
            samples.Count < BenchmarkMeasurementSampleCount
            || totalMeasurementTimestampTicks < targetTimestampTicks
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startTimestamp = Stopwatch.GetTimestamp();
            await dispatch(testClassInstance, benchmarkArguments, operationsPerIteration);
            var elapsedTimestampTicks = Stopwatch.GetTimestamp() - startTimestamp;
            totalMeasurementTimestampTicks += elapsedTimestampTicks;
            samples.Add(
                elapsedTimestampTicks
                * (1_000_000_000d / Stopwatch.Frequency)
                / operationsPerIteration
            );
        }

        var gcStatistics = new BenchmarkGcStatistics(
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            Math.Max(0, GC.GetTotalAllocatedBytes() - allocatedBefore)
        );
        var measurement = new MeasurementResult(
            samples.ToArray(),
            totalMeasurementTimestampTicks
        );
        return new MeasurementEpoch(measurement, gcStatistics);
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Warms up the benchmark until the rolling window of per-operation batch
    /// timings is stable, promoting the method through the JIT's compilation
    /// tiers and settling static caches before measurement. Bounded by a
    /// minimum iteration floor and a maximum iteration/time cap.
    /// </summary>
    private static async Task<WarmupResult> WarmUp(
        TestMethodMetadata.Benchmark.DispatchFunc dispatch,
        object? testClassInstance,
        object? benchmarkArguments,
        int operationsPerIteration,
        CancellationToken cancellationToken
    )
    {
        var nanosecondsPerOperation = new List<double>();
        var warmupStartTimestamp = Stopwatch.GetTimestamp();
        var iterations = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startTimestamp = Stopwatch.GetTimestamp();
            await dispatch(
                testClassInstance,
                benchmarkArguments,
                operationsPerIteration
            );
            var elapsedTimestampTicks = Stopwatch.GetTimestamp() - startTimestamp;

            nanosecondsPerOperation.Add(
                elapsedTimestampTicks
                * (1_000_000_000d / Stopwatch.Frequency)
                / operationsPerIteration
            );
            iterations++;

            if (iterations < BenchmarkMinimumWarmupIterationCount)
                continue;
            if (iterations >= BenchmarkMaximumWarmupIterationCount)
                break;
            if (Stopwatch.GetElapsedTime(warmupStartTimestamp) >= BenchmarkMaximumWarmupTime)
                break;
            if (IsWarmupStable(nanosecondsPerOperation))
                break;
        }

        return new WarmupResult(
            iterations,
            RecentMedian(nanosecondsPerOperation, BenchmarkWarmupStabilityWindow)
        );
    }

    /// <summary>
    /// Considers warmup stable when the most recent window is narrow and no
    /// longer moves monotonically in one direction.
    /// </summary>
    internal static bool IsWarmupStable(IReadOnlyList<double> nanosecondsPerOperation)
    {
        if (nanosecondsPerOperation.Count < BenchmarkWarmupStabilityWindow)
            return false;

        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        for (var i = nanosecondsPerOperation.Count - BenchmarkWarmupStabilityWindow;
            i < nanosecondsPerOperation.Count;
            i++)
        {
            minimum = Math.Min(minimum, nanosecondsPerOperation[i]);
            maximum = Math.Max(maximum, nanosecondsPerOperation[i]);
        }

        var median = RecentMedian(nanosecondsPerOperation, BenchmarkWarmupStabilityWindow);
        if (median <= 0)
            return true;

        var relativeRange = (maximum - minimum) / median;
        return relativeRange <= BenchmarkWarmupStabilityThreshold
            && (maximum == minimum || CountTimingDirectionChanges(nanosecondsPerOperation) >= 4);
    }

    private static int CountTimingDirectionChanges(
        IReadOnlyList<double> nanosecondsPerOperation
    )
    {
        var start = nanosecondsPerOperation.Count - BenchmarkWarmupStabilityWindow;
        var direction = 0;
        var directionChanges = 0;
        for (var i = start + 1; i < nanosecondsPerOperation.Count; i++)
        {
            var difference = nanosecondsPerOperation[i] - nanosecondsPerOperation[i - 1];
            var nextDirection = Math.Sign(difference);
            if (nextDirection == 0)
                continue;

            if (direction != 0 && direction != nextDirection)
                directionChanges++;
            direction = nextDirection;
        }

        return directionChanges;
    }

    private static double RecentMedian(IReadOnlyList<double> values, int windowSize)
    {
        var count = Math.Min(windowSize, values.Count);
        if (count == 0)
            return 0;

        var window = new double[count];
        for (var i = 0; i < count; i++)
            window[i] = values[values.Count - count + i];
        Array.Sort(window);

        return count % 2 == 1
            ? window[count / 2]
            : (window[count / 2 - 1] + window[count / 2]) / 2;
    }

    /// <summary>
    /// Recomputes the batch size against the warmed per-operation timing so the
    /// measured samples target the intended per-sample duration. Initial
    /// calibration runs on cold, un-tiered code and can therefore over-count the
    /// operations needed once the method is optimized.
    /// </summary>
    private static CalibrationResult RecalibrateOperations(
        int currentOperationsPerIteration,
        double stableNanosecondsPerOperation,
        TimeSpan targetIterationTime
    )
    {
        if (stableNanosecondsPerOperation <= 0)
            return new CalibrationResult(currentOperationsPerIteration, false);

        var targetNanoseconds = targetIterationTime.TotalMilliseconds * 1_000_000d;
        var projected = (long)Math.Ceiling(
            targetNanoseconds / stableNanosecondsPerOperation
        );
        projected = Math.Max(projected, 1L);
        return new CalibrationResult(
            (int)Math.Min(projected, BenchmarkMaximumOperationsPerIteration),
            projected <= BenchmarkMaximumOperationsPerIteration
        );
    }

    private static int CalculateNextOperationCount(
        int operationsPerIteration,
        TimeSpan elapsed
    )
    {
        if (elapsed < BenchmarkCalibrationResolutionTime)
        {
            return (int)Math.Min(
                (long)operationsPerIteration * 10,
                BenchmarkMaximumOperationsPerIteration
            );
        }

        var projectedOperations = (long)Math.Ceiling(
            operationsPerIteration
            * (BenchmarkPilotTargetTime.TotalSeconds / elapsed.TotalSeconds)
            * 1.1
        );
        projectedOperations = Math.Max(projectedOperations, operationsPerIteration + 1L);
        projectedOperations = Math.Min(
            projectedOperations,
            (long)operationsPerIteration * 100
        );
        return (int)Math.Min(
            projectedOperations,
            BenchmarkMaximumOperationsPerIteration
        );
    }

    private readonly record struct CalibrationResult(
        int OperationsPerIteration,
        bool TargetReached
    );

    private readonly record struct WarmupResult(
        int Iterations,
        double StableNanosecondsPerOperation
    );

    private readonly record struct MeasurementResult(
        double[] Samples,
        long TotalTimestampTicks
    );

    private readonly record struct MeasurementEpoch(
        MeasurementResult Measurement,
        BenchmarkGcStatistics GcStatistics
    );

    internal readonly record struct BenchmarkGcStatistics(
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long AllocatedBytes
    );

    internal static bool IsFailure(RunResult result) =>
        result switch
        {
            TestResult.Failed or TestResult.Faulted => true,
            BenchmarkResult.Failed => true,
            _ => false,
        };

    private static string GenerateTestId(TestDescriptor test)
    {
        var baseId = test.DisplayName;

        // Regular test without cases
        return baseId;
    }

    internal class TestDescriptor
    {
        public required TestClassMetadata Class { get; init; }
        public required TestMethodMetadata Method { get; init; }
        public TestCaseInfo? BenchmarkCase { get; init; }

        public string ClassName => Class.ClassName;
        public string MethodDisplayName =>
            BenchmarkCase is { } benchmarkCase
                ? $"{Method.MethodName}({benchmarkCase.DisplayName})"
                : Method.MethodName;
        public string DisplayName =>
            $"{Class.DisplayName ?? Class.ClassName}.{MethodDisplayName}";
    }
}
