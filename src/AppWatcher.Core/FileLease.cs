using System.Diagnostics;

namespace AppWatcher.Core;

internal static class FileLease
{
    public static async Task<FileStream> AcquireAsync(string path, TimeSpan timeout, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= timeout) throw new TimeoutException("Timed out waiting for the data file lock.", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
            }
        }
    }
}
