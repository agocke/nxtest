using System;

namespace NXTest;

// XUnit Compatibility Attributes
// These attributes provide direct compatibility with XUnit syntax
// allowing existing XUnit tests to work with NXTest without modification

/// <summary>
/// Marks a concrete class as a test class, including public test methods inherited from base classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TestClassAttribute : Attribute
{
    public TestClassAttribute() { }
}

/// <summary>
/// XUnit-compatible attribute for marking individual test methods.
/// Maps directly to NXTest's TestAttribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute
{
    public FactAttribute() { }

    /// <summary>
    /// Gets or sets the reason this test is skipped. If non-null, the test is not run
    /// and is instead reported as skipped with this value as the reason.
    /// </summary>
    public string? Skip { get; set; }
}

/// <summary>
/// XUnit-compatible attribute for parameterized tests.
/// Maps to NXTest's TestAttribute with data sources.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TheoryAttribute : Attribute
{
    public TheoryAttribute() { }
}

/// <summary>
/// Marks a method as a benchmark. Parameters are supplied with
/// <see cref="InlineDataAttribute"/>; each data row is measured independently.
/// Instance benchmarks reuse one class instance per case for every preparation,
/// pilot, warmup, and measured invocation. Construction and disposal are not
/// measured.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchAttribute : Attribute
{
    public BenchAttribute() { }
}

/// <summary>
/// XUnit-compatible inline data for theories and parameterized benchmarks.
/// Maps directly to NXTest's TestDataAttribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineDataAttribute : Attribute
{
    public object?[] Data { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="InlineDataAttribute"/> class.
    /// </summary>
    /// <param name="data">The test data arguments.</param>
    public InlineDataAttribute(params object?[] data)
    {
        Data = data;
    }
}
