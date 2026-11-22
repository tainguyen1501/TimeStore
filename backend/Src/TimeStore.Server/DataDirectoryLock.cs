namespace TimeStore.Server;

/// <summary>
/// An exclusive lock on <c>&lt;DataPath&gt;/.timestore-lock</c>, held by a running server for its whole life (released
/// by the operating system when the process ends, even killed). A second server on the same data directory refuses to
/// start, and a metadata restore refuses to replace the database under a running one.
/// </summary>
internal sealed class DataDirectoryLock : IDisposable
{
    public const string FileName = ".timestore-lock";

    private readonly FileStream _file;

    private DataDirectoryLock(FileStream file) => _file = file;

    /// <summary>The lock, or null when another process holds it.</summary>
    public static DataDirectoryLock? TryAcquire(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        try
        {
            // FileShare.None is an advisory exclusive lock (flock) on Unix, a share lock on Windows.
            return new DataDirectoryLock(new FileStream(
                Path.Combine(dataPath, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
