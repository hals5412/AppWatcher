namespace AppWatcher.Core;

public static class HostOperations
{
    public static async Task<IReadOnlyList<string>> ExecuteAsync(AppWatcherConfiguration configuration, SupervisorRequest request,
        Func<PrivilegeLevel, SupervisorRequest, Task<SupervisorResponse>>? send = null)
    {
        send ??= (privilege, command) => new SupervisorClient(privilege).SendAsync(command,
            command.Command == SupervisorCommandType.Ping ? TimeSpan.FromMilliseconds(350) : TimeSpan.FromSeconds(30));
        async Task<string?> Execute(PrivilegeLevel privilege)
        {
            if (privilege == PrivilegeLevel.Administrator && !configuration.Applications.Any(a => a.Privilege == privilege))
            {
                var ping = await send(privilege, new SupervisorRequest(SupervisorCommandType.Ping)).ConfigureAwait(false);
                if (!ping.Success) return null;
            }
            var response = await send(privilege, request).ConfigureAwait(false);
            return response.Success ? null : Localization.F("HostOperationFailureFormat",
                Localization.T(privilege == PrivilegeLevel.Normal ? "StartupTaskAgentLabel" : "StartupTaskElevatedLabel"), response.Error);
        }
        var errors = await Task.WhenAll(Execute(PrivilegeLevel.Normal), Execute(PrivilegeLevel.Administrator)).ConfigureAwait(false);
        return errors.Where(e => e is not null).Select(e => e!).ToArray();
    }
}
