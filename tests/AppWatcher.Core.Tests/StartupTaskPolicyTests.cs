using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class StartupTaskPolicyTests
{
    [Fact]
    public void RegisteredPriorityRunsTargetsAtNormalPriority()
    {
        Assert.True(StartupTaskPolicy.LaunchesTargetsAtNormalPriority(StartupTaskPolicy.TaskPriority));
    }

    [Theory]
    [InlineData(StartupTaskPolicy.TaskSchedulerDefaultPriority)]
    [InlineData(8)]
    [InlineData(10)]
    public void TaskSchedulerDefaultAndLowerPrioritiesAreFlagged(int priority)
    {
        Assert.False(StartupTaskPolicy.LaunchesTargetsAtNormalPriority(priority));
    }
}
