using AppWatcher.Core;

// 実運用フォルダーに対する誤実行を防ぎ、テスト専用の一時フォルダーだけを扱う。
if (args.Length != 2) return 2;
var directory = Path.GetFullPath(args[0]);
if (!directory.StartsWith(Path.Combine(Path.GetTempPath(), "AppWatcher-Audit-"), StringComparison.OrdinalIgnoreCase)) return 3;
var service = new ConfigService(directory);
await service.LoadAsync();
for (var i = 0; i < 20; i++)
    await service.UpdateAsync(config =>
    {
        if (args[1] == "retention") config.Global.EventRetentionDays++;
        else config.Global.UiRefreshSeconds++;
    });
return 0;
