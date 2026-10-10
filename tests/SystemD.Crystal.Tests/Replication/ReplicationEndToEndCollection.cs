// The socket-based, timing-sensitive replication end-to-end tests share one xUnit collection so they run serially,
// not in parallel with each other. Each spins up TCP listeners, follower loops and bootstrap downloads with catch-up
// waits; running them in parallel starves CPU and ports and makes the waits flaky. The pure/unit replication tests
// (RespTests, BatchApplierTests, divergence rule, etc.) stay parallel.

using Xunit;

namespace SystemD.Crystal.Tests.Replication;

/// <summary>The serial collection for socket-based replication end-to-end tests (no parallelism within it).</summary>
[CollectionDefinition("replication-e2e", DisableParallelization = true)]
public sealed class ReplicationEndToEndCollection
{
}
