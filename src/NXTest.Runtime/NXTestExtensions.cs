using System.Threading;
using Microsoft.Testing.Platform.Builder;

namespace NXTest.Runtime;

/// <summary>
/// Extension methods for wiring NXTest into a Microsoft.Testing.Platform application.
/// </summary>
public static class NXTestExtensions
{
    /// <summary>
    /// Registers the NXTest test framework with the given Microsoft.Testing.Platform
    /// application builder. This supports custom Microsoft.Testing.Platform hosts.
    /// </summary>
    /// <param name="builder">The test application builder.</param>
    /// <param name="testClasses">The test metadata to run (typically <c>TestRegistry.GetAllTests()</c>).</param>
    /// <param name="options">Optional execution options.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static ITestApplicationBuilder AddNXTest(
        this ITestApplicationBuilder builder,
        TestClassMetadata[] testClasses,
        TestExecutionOptions? options = null,
        CancellationToken cancellationToken = default,
        bool? runBenchmarks = null
    )
    {
        TestFramework.Register(builder, testClasses, options, cancellationToken, runBenchmarks);
        return builder;
    }
}
