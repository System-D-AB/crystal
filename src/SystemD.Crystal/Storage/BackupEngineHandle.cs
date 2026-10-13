// Spec 010 design § 3 — one RocksDB BackupEngine, owned. A SafeHandle, so a stop from another thread (a drop, a
// cancellation) never reaches an engine that is being or has been closed: each call adds a reference for its duration.

using System;
using System.Runtime.InteropServices;
using RocksDbSharp;

namespace SystemD.Crystal.Storage;

/// <summary>An open <c>BackupEngine</c> on one folder (spec 010). Nothing outside <c>SystemD.Crystal.Storage</c> sees its pointer.</summary>
internal sealed class BackupEngineHandle : SafeHandle
{
    // The environment the engine was opened with; it must outlive the engine.
    private readonly Env _env;

    private BackupEngineHandle(IntPtr engine, Env env)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _env = env;
        SetHandle(engine);
    }

    /// <summary>The engine's folder.</summary>
    public string Directory { get; private init; } = string.Empty;

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Opens the engine on <paramref name="directory"/> (spec 010 design § 3). Synchronous native work.</summary>
    public static BackupEngineHandle Open(string directory, int backgroundOperations = 2, ulong rateLimitBytesPerSecond = 0)
    {
        System.IO.Directory.CreateDirectory(directory);
        var env = Env.CreateDefaultEnv();
        return new BackupEngineHandle(NativeRocks.BackupEngineOpen(directory, env, backgroundOperations, rateLimitBytesPerSecond), env)
        {
            Directory = directory,
        };
    }

    /// <summary>Backs up <paramref name="instance"/> with <paramref name="metadata"/>, flushing first; returns the backup id.</summary>
    public uint Create(RocksInstance instance, byte[] metadata) => With(engine => NativeRocks.BackupCreate(engine, instance.Db, metadata));

    /// <summary>Stops a running <see cref="Create"/>, from any thread; nothing happens once the engine is closed.</summary>
    public void Stop()
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            NativeRocks.BackupStop(handle);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile: nothing is running.
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    /// <summary>The engine's backups, oldest first.</summary>
    public NativeRocks.BackupEntry[] List() => With(NativeRocks.BackupList);

    /// <summary>Every file of the backup present at its recorded size; throws otherwise.</summary>
    public void Verify(uint backupId) => With(engine => { NativeRocks.BackupVerify(engine, backupId); return 0; });

    /// <summary>Keeps the newest <paramref name="keep"/> backups.</summary>
    public void Purge(uint keep) => With(engine => { NativeRocks.BackupPurge(engine, keep); return 0; });

    /// <summary>Restores backup <paramref name="backupId"/> into <paramref name="directory"/>.</summary>
    public void Restore(uint backupId, string directory) => With(engine => { NativeRocks.BackupRestore(engine, backupId, directory); return 0; });

    private T With<T>(Func<IntPtr, T> call)
    {
        var added = false;
        try
        {
            DangerousAddRef(ref added);
            return call(handle);
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        NativeRocks.BackupEngineClose(handle);
        (_env as IDisposable)?.Dispose(); // as StoreResources frees its own
        return true;
    }
}
