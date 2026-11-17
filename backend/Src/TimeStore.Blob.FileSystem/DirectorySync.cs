using System.Reflection;
using System.Runtime.InteropServices;

namespace TimeStore.Blob.FileSystem;

/// <summary>
/// Makes a directory's entries durable (<c>fsync</c> on the directory). Needed after a rename: the file's data is
/// flushed, but the new name lives in the directory, and without this a power loss can lose it even though the
/// metadata commit already happened. .NET has no API for it, so this calls libc (LibraryImport: source-generated,
/// AOT-safe). Windows: no-op (NTFS journals directory changes; directory handles cannot be flushed this way).
/// </summary>
internal static partial class DirectorySync
{
    private const string LibC = "libc";
    private const int ReadOnly = 0; // O_RDONLY

    // Library names per platform; "libc" alone is not resolvable everywhere (glibc ships libc.so.6, musl differs).
    private static readonly string[] Candidates =
    [
        "/usr/lib/libSystem.B.dylib", // macOS
        "libc.so.6", // glibc
        "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", // musl (Alpine)
        "libc.so",
    ];

    static DirectorySync() => NativeLibrary.SetDllImportResolver(typeof(DirectorySync).Assembly, Resolve);

    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;

        var fd = Open(directory, ReadOnly);
        if (fd < 0)
            throw new IOException($"Cannot open directory '{directory}' to sync it (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (Fsync(fd) != 0)
                throw new IOException($"Cannot sync directory '{directory}' (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibC)
            return IntPtr.Zero;
        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [LibraryImport(LibC, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport(LibC, EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
