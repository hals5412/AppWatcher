using System.Text;

namespace AppWatcher.Core;

public sealed class FallbackLog(string path, long maxBytes = 5 * 1024 * 1024)
{
    public async Task WriteAsync(string message, CancellationToken token = default)
    {
        try
        {
            await using var lease = await FileLease.AcquireAsync(path + ".lock", TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            var line = SecretMask.Text(message) + Environment.NewLine;
            if (Encoding.UTF8.GetByteCount(line) > maxBytes) line = "Log record exceeded the fallback size limit." + Environment.NewLine;
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > maxBytes)
            {
                if (File.Exists(path + ".2")) File.Delete(path + ".2");
                if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                File.Move(path, path + ".1");
            }
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), token).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested) { /* ログ障害で監視を止めない。 */ }
    }
}
