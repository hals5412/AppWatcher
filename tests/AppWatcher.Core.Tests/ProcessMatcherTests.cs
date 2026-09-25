using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class ProcessMatcherTests
{
    private const string UserSid = "S-1-5-21-1000-1000-1000-1001";
    private const string TargetPath = @"C:\Apps\Target\target.exe";

    [Fact]
    public void MatchesSamePathPrivilegeUserAndSession()
    {
        Assert.True(ProcessMatcher.IsMatch(Identity(), Definition(PrivilegeLevel.Normal), UserSid, 1));
        Assert.True(ProcessMatcher.IsMatch(Identity(path: @"c:\apps\TARGET\Target.EXE"), Definition(PrivilegeLevel.Normal), UserSid, 1));
    }

    [Fact]
    public void RejectsInstanceWithDifferentPrivilege()
    {
        // 管理者Helperが通常権限で起動された同じexeを管理者アプリとして扱わない。
        Assert.False(ProcessMatcher.IsMatch(Identity(privilege: PrivilegeLevel.Normal), Definition(PrivilegeLevel.Administrator), UserSid, 1));
        Assert.False(ProcessMatcher.IsMatch(Identity(privilege: PrivilegeLevel.Administrator), Definition(PrivilegeLevel.Normal), UserSid, 1));
    }

    [Fact]
    public void RejectsOtherUserSessionOrPath()
    {
        Assert.False(ProcessMatcher.IsMatch(Identity(userSid: "S-1-5-21-1000-1000-1000-2002"), Definition(PrivilegeLevel.Normal), UserSid, 1));
        Assert.False(ProcessMatcher.IsMatch(Identity(sessionId: 2), Definition(PrivilegeLevel.Normal), UserSid, 1));
        Assert.False(ProcessMatcher.IsMatch(Identity(path: @"C:\Other\target.exe"), Definition(PrivilegeLevel.Normal), UserSid, 1));
    }

    [Fact]
    public void FindsCurrentProcessOnlyAtItsOwnPrivilege()
    {
        var own = Environment.IsPrivilegedProcess ? PrivilegeLevel.Administrator : PrivilegeLevel.Normal;
        var other = own == PrivilegeLevel.Normal ? PrivilegeLevel.Administrator : PrivilegeLevel.Normal;
        var matcher = new ProcessMatcher();

        using var found = matcher.FindExisting(new ApplicationDefinition { ExecutablePath = Environment.ProcessPath!, Privilege = own });
        Assert.NotNull(found);

        using var mismatched = matcher.FindExisting(new ApplicationDefinition { ExecutablePath = Environment.ProcessPath!, Privilege = other });
        Assert.Null(mismatched);
    }

    private static ProcessIdentity Identity(
        string path = TargetPath,
        PrivilegeLevel privilege = PrivilegeLevel.Normal,
        string? userSid = UserSid,
        int sessionId = 1) => new(path, privilege, userSid, sessionId);

    private static ApplicationDefinition Definition(PrivilegeLevel privilege) =>
        new() { ExecutablePath = TargetPath, Privilege = privilege };
}
