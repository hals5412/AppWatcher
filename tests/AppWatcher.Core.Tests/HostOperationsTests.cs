using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class HostOperationsTests
{
    [Fact]
    public async Task RequiredHelperFailureIsReportedWithoutRetryingSuccessfulAgent()
    {
        var calls = new List<PrivilegeLevel>();
        var config = new AppWatcherConfiguration { Applications = [new ApplicationDefinition { Privilege = PrivilegeLevel.Administrator }] };
        var errors = await HostOperations.ExecuteAsync(config, new SupervisorRequest(SupervisorCommandType.StartMaintenance), (host, command) =>
        {
            calls.Add(host);
            return Task.FromResult(new SupervisorResponse(host == PrivilegeLevel.Normal, "timed out"));
        });
        Assert.Single(errors);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task UnusedOfflineHelperIsOptional()
    {
        var commands = new List<SupervisorCommandType>();
        var errors = await HostOperations.ExecuteAsync(new AppWatcherConfiguration(), new SupervisorRequest(SupervisorCommandType.ResumeMaintenance), (host, command) =>
        {
            commands.Add(command.Command);
            return Task.FromResult(new SupervisorResponse(host == PrivilegeLevel.Normal));
        });
        Assert.Empty(errors);
        Assert.Single(commands, c => c == SupervisorCommandType.ResumeMaintenance);
        Assert.Contains(SupervisorCommandType.Ping, commands);
    }
}
