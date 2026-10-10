using System;
using System.IO;

namespace SystemD.Crystal.Storage;

/// <summary>
/// Exclusive file lock for the store lifetime (spec 005 R1.3).
/// Refuses a second open in-process or cross-process immediately with <see cref="StoreLockedException"/>.
/// </summary>
internal sealed class StoreLock : IDisposable
{
    public const string LockFileName = "crystal.lock";

    private FileStream? _stream;
    private readonly string _lockFilePath;
    private readonly string _dataDirectory;

    private StoreLock(FileStream stream, string lockFilePath, string dataDirectory)
    {
        _stream = stream;
        _lockFilePath = lockFilePath;
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// Attempts to exclusively acquire the store lock at <paramref name="dataDirectory"/>.
    /// Throws <see cref="StoreLockedException"/> if already held.
    /// </summary>
    public static StoreLock Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var lockFilePath = Path.Combine(dataDirectory, LockFileName);

        try
        {
            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            return new StoreLock(stream, lockFilePath, dataDirectory);
        }
        catch (IOException ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException))
        {
            // Only a sharing conflict means the store is held. A missing directory or a permission problem is
            // reported as itself, never as "already open" (design addendum A7).
            throw new StoreLockedException(
                dataDirectory,
                $"The store at '{dataDirectory}' is already open (lock file '{LockFileName}' could not be acquired exclusively).",
                ex);
        }
    }

    public void Dispose()
    {
        var s = _stream;
        _stream = null;
        s?.Dispose();
    }
}
