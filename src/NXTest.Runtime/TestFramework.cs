
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Testing.Extensions;
using Microsoft.Testing.Extensions.TrxReport.Abstractions;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
using static NXTest.RunResult;

namespace NXTest.Runtime;

public sealed class TestFramework : ITestFramework, IDataProducer
{
    private const string s_benchmarkArgument = "--bench";
    private const string s_displayName = "nxtest Microsoft.Testing.Platform framework";
    private const string s_dotnetTestExecutionId =
        "TESTINGPLATFORM_DOTNETTEST_EXECUTIONID";

    public string Uid => "64e8dd3a-ae2c-448f-9481-587f0252bfb8";

    public string Version => "0.1.1";

    public string DisplayName => s_displayName;

    public string Description => s_displayName;

    public Type[] DataTypesProduced { get; } = [ typeof(TestNodeUpdateMessage) ];

    private readonly TestClassMetadata[] _testClasses;
    private TestExecutionOptions _options;
    private readonly bool _runBenchmarks;
    private readonly CancellationToken _cancellationToken;
    private readonly List<BenchmarkResult> _benchmarkResults = [];


    private TestFramework(
        TestClassMetadata[] testClasses,
        TestExecutionOptions options,
        bool runBenchmarks,
        CancellationToken cancellationToken
    )
    {
        _testClasses = testClasses;
        _options = options;
        _runBenchmarks = runBenchmarks;
        _cancellationToken = cancellationToken;
    }

    private sealed class TrxReportCapability : ITrxReportCapability
    {
        public bool IsSupported => true;

        public void Enable()
        {
            // No state to manage for this simple implementation
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        TestClassMetadata[] testClasses,
        TestExecutionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var runBenchmarks =
            options?.RunBenchmarks == true
            || args.Contains(s_benchmarkArgument, StringComparer.Ordinal);
        if (runBenchmarks && !IsMtpServerInvocation(args))
        {
            return await TestRunner.RunAsync(
                testClasses,
                CreateBenchmarkOptions(options),
                cancellationToken
            );
        }

        var platformArgs = GetPlatformArguments(args, runBenchmarks);

        var builder = await TestApplication.CreateBuilderAsync(platformArgs);
        builder.AddTrxReportProvider();
        builder.AddNXTest(testClasses, options, cancellationToken, runBenchmarks);

        var app = await builder.BuildAsync();
        return await app.RunAsync();
    }

    /// <summary>
    /// Registers the NXTest framework with a Microsoft.Testing.Platform application
    /// builder. This supports custom Microsoft.Testing.Platform hosts. TRX reporting is
    /// contributed separately by the host or by <see cref="RunAsync"/>.
    /// </summary>
    internal static void Register(
        ITestApplicationBuilder builder,
        TestClassMetadata[] testClasses,
        TestExecutionOptions? options,
        CancellationToken cancellationToken,
        bool? runBenchmarks = null
    )
    {
        // When a custom host does not specify, fall back to the options flag or the
        // process command line so `--bench` still works.
        var effectiveRunBenchmarks =
            runBenchmarks
            ?? (options?.RunBenchmarks == true
                || Environment.GetCommandLineArgs().Contains(s_benchmarkArgument, StringComparer.Ordinal));

        builder.RegisterTestFramework(
            serviceProvider => new TestFrameworkCapabilities(new TrxReportCapability()),
            (capabilities, serviceProvider) =>
            {
                return new TestFramework(
                    testClasses,
                    options ?? new TestExecutionOptions(),
                    effectiveRunBenchmarks,
                    cancellationToken
                );
            }
        );
    }

    internal static string[] GetPlatformArguments(string[] args, bool runBenchmarks)
    {
        // Benchmark results are presented through our own summary table
        // (see PrintBenchmarkSummary), so the platform output is left at its
        // default verbosity to keep the run quiet. We only strip our own
        // custom flag before handing the arguments to the platform.
        return args
            .Where(arg => !string.Equals(arg, s_benchmarkArgument, StringComparison.Ordinal))
            .ToArray();
    }

    internal static bool IsMtpServerInvocation(IReadOnlyList<string> args) =>
        args.Contains("--server", StringComparer.Ordinal);

    private static TestExecutionOptions CreateBenchmarkOptions(
        TestExecutionOptions? options
    )
    {
        options ??= TestExecutionOptions.Default;
        return new TestExecutionOptions
        {
            Mode = options.Mode,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            StopOnFirstFailure = options.StopOnFirstFailure,
            RunBenchmarks = true,
            GlobalTimeout = options.GlobalTimeout,
        };
    }

    public async Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context)
    {
        // No special teardown needed for NXTest
        return new CloseTestSessionResult() { IsSuccess = true };
    }

    public async Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context)
    {
        // No special setup needed for NXTest
        return new CreateTestSessionResult() { IsSuccess = true };
    }

    public async Task ExecuteRequestAsync(ExecuteRequestContext context)
    {
        if (context.Request is DiscoverTestExecutionRequest)
        {
            try
            {
                // Enumerate all tests and publish them as discovered nodes (no execution).
                await DiscoverTestsAsync(context);
            }
            finally
            {
                context.Complete();
            }
        }
        else if (context.Request is RunTestExecutionRequest)
        {
            // Filters are not supported yet
            try
            {
                // Run all the tests and publish the results to the MTP IMessageBus
                await ExecuteTestsAsync(context);
            }
            finally
            {
                context.Complete();
            }
        }
    }

    private async Task DiscoverTestsAsync(ExecuteRequestContext context)
    {
        var sessionUid = context.Request.Session.SessionUid;

        // Walk test classes in parallel; the message bus accepts concurrent publishes (the run
        // path already relies on this). Discovery has no ordering dependencies, so the run-time
        // shuffle is intentionally skipped here.
        await Parallel.ForEachAsync(
            _testClasses,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                CancellationToken = context.CancellationToken,
            },
            async (testClass, ct) =>
            {
                foreach (var method in testClass.TestMethods)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    // A skipped test (fact or theory) runs as a single node named after the
                    // method, so discovery must mirror that to keep Uids correlated with a run.
                    if (method.Skip)
                    {
                        await PublishDiscoveredNodeAsync(
                            context, sessionUid, testClass.ClassName, $"{testClass.ClassName}.{method.MethodName}");
                        continue;
                    }

                    switch (method)
                    {
                        case TestMethodMetadata.Theory theory:
                            foreach (var testCase in theory.TestCases)
                            {
                                await PublishDiscoveredNodeAsync(
                                    context,
                                    sessionUid,
                                    testClass.ClassName,
                                    $"{testClass.ClassName}.{theory.MethodName}({testCase.DisplayName})"
                                );
                            }
                            break;

                        default:
                            await PublishDiscoveredNodeAsync(
                                context, sessionUid, testClass.ClassName, $"{testClass.ClassName}.{method.MethodName}");
                            break;
                    }
                }
            }
        );
    }

    // Publishes a single discovered TestNode. The fully-qualified name matches what a run
    // produces as TestNode Uid/DisplayName so discovered nodes correlate with executed ones.
    private async Task PublishDiscoveredNodeAsync(
        ExecuteRequestContext context,
        SessionUid sessionUid,
        string className,
        string fqn
    )
    {
        var testNode = new TestNode()
        {
            Uid = fqn,
            DisplayName = fqn,
            Properties = new PropertyBag(
                new TrxFullyQualifiedTypeNameProperty(className),
                DiscoveredTestNodeStateProperty.CachedInstance
            )
        };
        await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(sessionUid, testNode));
    }

    private async Task ExecuteTestsAsync(ExecuteRequestContext context)
    {
        var stopCts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken,
            stopCts.Token,
            context.CancellationToken
        ).Token;

        var tests = TestExecutionEngine.CollectAllTests(_testClasses);
        if (_runBenchmarks)
        {
            tests.RemoveAll(
                test => test.Method is not TestMethodMetadata.Benchmark
            );
            TestExecutionEngine.SortBenchmarks(tests);
            foreach (var benchmark in tests)
            {
                if (linkedCt.IsCancellationRequested)
                    break;

                await RunAndPublishAsync(context, benchmark, stopCts, linkedCt);
            }

            PrintBenchmarkSummary();
            return;
        }

        tests.RemoveAll(test => test.Method is TestMethodMetadata.Benchmark);

        switch (_options.Mode)
        {
            case ParallelMode.None:
                foreach (var test in tests)
                {
                    if (linkedCt.IsCancellationRequested)
                        break;

                    await RunAndPublishAsync(context, test, stopCts, linkedCt);
                }
                break;

            case ParallelMode.Classes:
                var classGroups = tests.GroupBy(t => t.ClassName, StringComparer.Ordinal);

                await Parallel.ForEachAsync(
                    classGroups,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                        CancellationToken = linkedCt,
                    },
                    async (group, ct) =>
                    {
                        foreach (var test in group)
                        {
                            if (ct.IsCancellationRequested)
                                break;

                            await RunAndPublishAsync(context, test, stopCts, ct);
                        }
                    }
                );
                break;

            default: // ParallelMode.Tests
                await Parallel.ForEachAsync(
                    tests,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
                        CancellationToken = linkedCt,
                    },
                    async (test, ct) => await RunAndPublishAsync(context, test, stopCts, ct)
                );
                break;
        }

    }

    private async Task RunAndPublishAsync(
        ExecuteRequestContext context,
        TestExecutionEngine.TestDescriptor test,
        CancellationTokenSource stopCts,
        CancellationToken ct
    )
    {
        var results = await TestExecutionEngine.ExecuteTestAsync(test, _options, ct);

        if (_options.StopOnFirstFailure && results.Any(TestExecutionEngine.IsFailure))
            stopCts.Cancel();

        await PublishRunResultsAsync(context, results);
    }

    private async Task PublishRunResultsAsync(ExecuteRequestContext context, RunResult[] results)
    {
        foreach (var result in results)
        {
            if (result is BenchmarkResult benchmarkResult)
                _benchmarkResults.Add(benchmarkResult);

            var testNode = result switch
            {
                TestResult testResult => CreateTestNode(testResult),
                BenchmarkResult br => CreateBenchmarkNode(br),
                _ => throw new InvalidOperationException(
                    $"Unknown run result type: {result.GetType()}"
                ),
            };
            await context.MessageBus.PublishAsync(this, new TestNodeUpdateMessage(context.Request.Session.SessionUid, testNode));
        }
    }

    private void PrintBenchmarkSummary()
    {
        if (_benchmarkResults.Count == 0)
            return;

        if (!IsDotnetTestInvocation(
            Environment.GetEnvironmentVariable(s_dotnetTestExecutionId)
        ))
        {
            Console.Write(
                BenchmarkTextFormatter.Format(
                    _benchmarkResults,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow
                )
            );
            return;
        }

        Console.WriteLine();
        Console.Write(BenchmarkSummaryFormatter.FormatSummary(_benchmarkResults));
    }

    internal static bool IsDotnetTestInvocation(string? executionId) =>
        !string.IsNullOrEmpty(executionId);

    private static TestNode CreateTestNode(TestResult result)
    {
        var fullyQualifiedName = $"{result.ClassDisplayName ?? result.ClassName}.{result.Name}";
        var trxTypeName = new TrxFullyQualifiedTypeNameProperty(result.ClassName);
        return result switch
        {
            TestResult.Skipped skipped => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(trxTypeName, new SkippedTestNodeStateProperty(skipped.Reason))
            },
            TestResult.Passed => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(trxTypeName, new PassedTestNodeStateProperty())
            },
            TestResult.Failed failed => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(
                    trxTypeName,
                    new FailedTestNodeStateProperty(failed.ErrorMessage),
                    new TrxExceptionProperty(failed.ErrorMessage, failed.StackTrace)
                )
            },
            TestResult.Faulted faulted => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(
                    trxTypeName,
                    new ErrorTestNodeStateProperty(faulted.ErrorMessage),
                    new TrxExceptionProperty(faulted.ErrorMessage, faulted.StackTrace)
                )
            },
        };
    }

    private static TestNode CreateBenchmarkNode(BenchmarkResult result)
    {
        var fullyQualifiedName = $"{result.ClassDisplayName ?? result.ClassName}.{result.Name}";
        var trxTypeName = new TrxFullyQualifiedTypeNameProperty(result.ClassName);
        return result switch
        {
            BenchmarkResult.Completed completed => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(trxTypeName, new PassedTestNodeStateProperty())
            },
            BenchmarkResult.Failed failed => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(
                    trxTypeName,
                    new FailedTestNodeStateProperty(failed.ErrorMessage),
                    new TrxExceptionProperty(failed.ErrorMessage, failed.StackTrace)
                )
            },
            BenchmarkResult.Skipped skipped => new TestNode()
            {
                Uid = fullyQualifiedName,
                DisplayName = fullyQualifiedName,
                Properties = new PropertyBag(
                    trxTypeName,
                    new SkippedTestNodeStateProperty(skipped.Reason)
                )
            },
        };
    }

    public async Task<bool> IsEnabledAsync() => true;
}