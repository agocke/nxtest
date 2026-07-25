using XunitAssert = Xunit.Assert;

namespace NXTest.Runtime.Tests;

public class TestFrameworkTests
{
    [Fact]
    public void GetPlatformArguments_StripsBenchmarkFlag()
    {
        var arguments = TestFramework.GetPlatformArguments(["--bench"], runBenchmarks: true);

        XunitAssert.Empty(arguments);
    }

    [Fact]
    public void GetPlatformArguments_DoesNotInjectOutputVerbosity()
    {
        var arguments = TestFramework.GetPlatformArguments(
            ["--bench", "--timeout", "30s"],
            runBenchmarks: true
        );

        XunitAssert.Equal(["--timeout", "30s"], arguments);
    }

    [Fact]
    public void GetPlatformArguments_TestModePassesArgumentsThrough()
    {
        var arguments = TestFramework.GetPlatformArguments([], runBenchmarks: false);

        XunitAssert.Empty(arguments);
    }

    [Fact]
    public void IsDotnetTestInvocation_RequiresExecutionId()
    {
        XunitAssert.False(TestFramework.IsDotnetTestInvocation(null));
        XunitAssert.False(TestFramework.IsDotnetTestInvocation(""));
        XunitAssert.True(TestFramework.IsDotnetTestInvocation("execution-id"));
    }

    [Fact]
    public void IsMtpServerInvocation_RequiresServerArgument()
    {
        XunitAssert.False(TestFramework.IsMtpServerInvocation(["--bench"]));
        XunitAssert.True(
            TestFramework.IsMtpServerInvocation(
                ["--bench", "--server", "dotnettestcli"]
            )
        );
    }
}
