// Spec 011 R5.2 — events are delivered on Crystal's own thread, after the call that raised them may have returned. A test
// that asserts an event waits for delivery first, with a bound, never with a sleep.

using System;
using SystemD.Crystal;
using Xunit;

namespace SystemD.Crystal.Tests.Replication;

internal static class EventDelivery
{
    /// <summary>Waits until every event <paramref name="store"/> has raised so far reached its handlers.</summary>
    public static void Drain(CrystalStore store) =>
        Assert.True(store.Replication.DrainEventsForTests(TimeSpan.FromSeconds(10)), "replication events were not delivered within 10 s");
}
