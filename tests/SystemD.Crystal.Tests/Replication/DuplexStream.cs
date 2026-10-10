using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace SystemD.Crystal.Tests.Replication;

/// <summary>
/// A one-direction-each <see cref="Stream"/> over a <see cref="PipeReader"/> and <see cref="PipeWriter"/>, so two of
/// them wired to opposite pipes form an in-memory connection — a deterministic stand-in for a socket in the
/// replication protocol tests.
/// </summary>
internal sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var seq = result.Buffer;
        if (seq.IsEmpty && result.IsCompleted)
        {
            return 0;
        }

        var toCopy = (int)Math.Min(seq.Length, buffer.Length);
        seq.Slice(0, toCopy).CopyTo(buffer.Span);
        reader.AdvanceTo(seq.GetPosition(toCopy));
        return toCopy;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            writer.Complete();
            reader.Complete();
        }

        base.Dispose(disposing);
    }
}
