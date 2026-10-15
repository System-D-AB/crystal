using System.Collections.Generic;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Storage;

namespace SystemD.Crystal.Tests.Replication;

/// <summary>
/// The synchronous form the source tests call (spec 014 T2 made the source's handler asynchronous, R5.1): it runs the
/// handler to completion, so the tests assert exactly what they asserted before.
/// </summary>
internal static class ReplicationSourceTestExtensions
{
    public static bool TryHandle(this ReplicationSource source, string command, IReadOnlyList<byte[]> args, string peerNodeId, RespWriter reply) =>
        source.TryHandleAsync(command, args, peerNodeId, reply, default).AsTask().GetAwaiter().GetResult();
}
