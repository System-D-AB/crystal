// Spec 011 R9.1 — a rename is durable only once its directory is: on Linux and macOS, a crash after rename() but before
// the directory's own metadata reaches the disk can undo the rename, so a promotion, a follow record or an install's
// folder move could come back undone after a power loss. After each rename that publishes state, Crystal syncs the
// parent directory.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SystemD.Crystal.Storage;

/// <summary>Durability of renames (spec 011 R9.1): syncing the directory that holds a renamed file or folder.</summary>
internal static partial class DurableFile
{
    /// <summary>Raised with the directory after each sync, on every platform (spec 011 R9.3 tests count the calls).</summary>
    internal static event Action<string>? DirectorySynced;

    /// <summary>
    /// Makes the entries of <paramref name="directory"/> durable, so a rename into it survives a power loss. On Linux and
    /// macOS it opens the directory and calls <c>fsync</c> (.NET cannot open a directory as a file handle); a failure
    /// throws <see cref="IOException"/>, since the caller cannot know whether its rename is on disk. On Windows it does
    /// nothing: .NET cannot flush a directory there, and NTFS journals its metadata.
    /// </summary>
    public static void SyncDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!OperatingSystem.IsWindows())
        {
            var fd = Open(directory, 0 /* O_RDONLY */);
            if (fd < 0)
            {
                throw new IOException($"'{directory}' could not be opened to make a rename durable (errno {Marshal.GetLastPInvokeError()}).");
            }

            try
            {
                if (Fsync(fd) != 0)
                {
                    throw new IOException($"'{directory}' could not be synced to make a rename durable (errno {Marshal.GetLastPInvokeError()}).");
                }
            }
            finally
            {
                _ = Close(fd);
            }
        }

        DirectorySynced?.Invoke(directory);
    }

    /// <summary>Deletes <paramref name="path"/> if it exists and syncs its directory, so the deletion survives a power loss.</summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, replacing it, and syncs the directory of <paramref name="to"/>.</summary>
    public static void Replace(string from, string to)
    {
        File.Move(from, to, overwrite: true);
        SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
