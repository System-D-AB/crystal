using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SystemD.Crystal.Process.Tests.Support;

/// <summary>A unique directory under the temp folder, deleted when disposed, even when the test failed (spec 004 R1.2).</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "crystal-process")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
    }

    public string Path { get; }

    public void Dispose()
    {
        // A killed child's files may stay locked for a moment while the OS reclaims them.
        for (var attempt = 0; attempt < 20 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>A TCP port free at the moment of asking, on the loopback interface (spec 004 R1.2: tests make their own ports).</summary>
internal static class FreePort
{
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
