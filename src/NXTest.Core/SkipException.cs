using System;

namespace NXTest;

/// <summary>
/// Exception that, when thrown from a test, causes the test to be reported as skipped
/// rather than failed.
/// </summary>
public sealed class SkipException : Exception
{
    public SkipException(string reason)
        : base(reason) { }
}
