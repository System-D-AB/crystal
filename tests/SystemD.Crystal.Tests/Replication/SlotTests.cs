// Spec 003 R2.2–R2.4, R13.1, R13.2 (task T7, completed 2026-10-10) — standby slots on a primary: per standby and
// database, an acknowledged position, epoch, last contact and state; lagging past a quarter of the lag cap, dropped past
// the cap or after a silence, each move raising its event once; revived by the next contact (an observation, never a
// hold); lag in sequences and in time. Ported from Highway's SlotAgingTests, per database.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SystemD.Crystal;
using SystemD.Crystal.Replication;
using SystemD.Crystal.Replication.Resp;
using SystemD.Crystal.Tests.Support;
using Xunit;
using Text = System.Text.Encoding;

namespace SystemD.Crystal.Tests.Replication;

[Collection("replication-e2e")]
public sealed class SlotTests
{
    private const string Secret = "test-secret";

    private static CrystalOptions Options(ManualClock clock, long lagCap = 100_000, TimeSpan? staleAfter = null, bool tagged = false) => new()
    {
        Clock = clock,
        TagMetricsWithDatabase = tagged,
        Replication = new ReplicationOptions
        {
            Secret = Secret,
            ListenEndpoint = "127.0.0.1:0",
            LagCap = lagCap,
            SlotStaleAfter = staleAfter ?? TimeSpan.FromSeconds(30),
        },
    };

    [Fact]
    public async Task APull_CreatesAnActiveSlot_WithItsPositionEpochAndContact()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock), out var db, writes: 10);
        await using var standby = await Standby.Connect(primary);

        await standby.Pull(db.Uid, from: 4);

        var slot = Assert.Single(primary.Replication.Status().Slots);
        Assert.Equal("standby-1", slot.Standby);
        Assert.Equal(db.Uid, slot.DatabaseUid);
        Assert.Equal(4, slot.Acknowledged);
        Assert.Equal((long)db.Slot.LatestSequence - 4, slot.LagSequences);
        Assert.Equal(ReplicationSlotState.Active, slot.State);
        Assert.Equal(clock.GetUtcNow(), slot.LastContact);
    }

    [Fact]
    public async Task ASilentSlot_IsDroppedPastTheBound_ByTheTimer_WithOneEvent()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock, staleAfter: TimeSpan.FromSeconds(30)), out var db, writes: 1);
        var events = Collect(primary);
        await using var standby = await Standby.Connect(primary);
        await standby.Pull(db.Uid, from: (long)db.Slot.LatestSequence);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ReplicationSlotState.Active, primary.Replication.Status().Slots.Single().State);

        clock.Advance(TimeSpan.FromSeconds(60)); // the timer ages it; nobody pulls or reads the status

        EventDelivery.Drain(primary);
        Assert.Single(events, e => e.Kind == ReplicationEventKind.SlotDropped);
        Assert.Equal(ReplicationSlotState.Dropped, primary.Replication.Status().Slots.Single().State);
        clock.Advance(TimeSpan.FromSeconds(60));
        EventDelivery.Drain(primary);
        Assert.Single(events, e => e.Kind == ReplicationEventKind.SlotDropped); // once
    }

    [Fact]
    public async Task SlotStaleAfterZero_DisablesTheTimeRule()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock, staleAfter: TimeSpan.Zero), out var db, writes: 1);
        await using var standby = await Standby.Connect(primary);
        await standby.Pull(db.Uid, from: (long)db.Slot.LatestSequence);

        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ReplicationSlotState.Active, primary.Replication.Status().Slots.Single().State);
    }

    [Fact]
    public async Task PastAQuarterOfTheCap_IsLagging_PastTheCap_IsDropped_EachRaisedOnce()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock, lagCap: 100), out var db, writes: 0);
        var events = Collect(primary);
        var start = (long)db.Slot.LatestSequence;
        await using var standby = await Standby.Connect(primary);
        await standby.Pull(db.Uid, from: start);

        Write(db, 30); // 30 behind: past 25, a quarter of the cap
        await standby.Pull(db.Uid, from: start);
        Assert.Equal(ReplicationSlotState.Lagging, primary.Replication.Status().Slots.Single().State);
        await standby.Pull(db.Uid, from: start);
        EventDelivery.Drain(primary);
        Assert.Single(events, e => e.Kind == ReplicationEventKind.SlotLagging);

        Write(db, 90); // 120 behind: past the cap
        primary.Replication.Status();
        Assert.Equal(ReplicationSlotState.Dropped, primary.Replication.Status().Slots.Single().State);
        EventDelivery.Drain(primary);
        Assert.Single(events, e => e.Kind == ReplicationEventKind.SlotDropped);
    }

    [Fact]
    public async Task ADroppedSlot_IsAnObservation_TheNextContactRevivesIt()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock, staleAfter: TimeSpan.FromSeconds(30)), out var db, writes: 1);
        await using var standby = await Standby.Connect(primary);
        var latest = (long)db.Slot.LatestSequence;
        await standby.Pull(db.Uid, from: latest);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(ReplicationSlotState.Dropped, primary.Replication.Status().Slots.Single().State);

        var reply = await standby.Pull(db.Uid, from: latest);

        Assert.Equal(ReplicationProtocol.EntryOk, Text.UTF8.GetString(reply.Items![2].Items![0].Items![1].Bytes!));
        Assert.Equal(ReplicationSlotState.Active, primary.Replication.Status().Slots.Single().State);
    }

    [Fact]
    public async Task LagTime_IsTheAgeOfTheOldestBatchTheStandbyLacks_AndZeroWhenCaughtUp()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock), out var db, writes: 0);
        await using var standby = await Standby.Connect(primary);
        var start = (long)db.Slot.LatestSequence;
        await standby.Pull(db.Uid, from: start); // serving starts: commit times are kept from here

        Write(db, 1);
        clock.Advance(TimeSpan.FromSeconds(10));
        Write(db, 1);
        clock.Advance(TimeSpan.FromSeconds(5));

        var behind = primary.Replication.Status().Slots.Single();
        Assert.Equal(2, behind.LagSequences);
        Assert.Equal(TimeSpan.FromSeconds(15), behind.LagTime); // the first batch it lacks, 15 s old

        await standby.Pull(db.Uid, from: start + 1);
        Assert.Equal(TimeSpan.FromSeconds(5), primary.Replication.Status().Slots.Single().LagTime);

        await standby.Pull(db.Uid, from: (long)db.Slot.LatestSequence);
        var caughtUp = primary.Replication.Status().Slots.Single();
        Assert.Equal(0, caughtUp.LagSequences);
        Assert.Equal(TimeSpan.Zero, caughtUp.LagTime);
    }

    [Fact]
    public async Task ADemotion_ClearsTheSlots()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock), out var db, writes: 1);
        await using var standby = await Standby.Connect(primary);
        await standby.Pull(db.Uid, from: 0);
        Assert.NotEmpty(primary.Replication.Status().Slots);

        primary.Replication.Demote();

        Assert.Empty(primary.Replication.Status().Slots);
    }

    [Fact]
    public async Task TheMetrics_ReportSlotsByState_AndTheWorstLag()
    {
        var clock = new ManualClock();
        using var dir = new TempStoreDirectory();
        using var primary = Primary(dir, Options(clock, lagCap: 100), out var db, writes: 0);
        await using var standby = await Standby.Connect(primary);
        var start = (long)db.Slot.LatestSequence;
        await standby.Pull(db.Uid, from: start);
        Write(db, 30);
        clock.Advance(TimeSpan.FromSeconds(2));

        var seen = new List<(string Name, double Value, string? Tag)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "SystemD.Crystal" && instrument.Name.StartsWith("crystal.replication.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => seen.Add((i.Name, v, TagOf(tags))));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => seen.Add((i.Name, v, TagOf(tags))));
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Contains(seen, m => m.Name == "crystal.replication.slots" && m.Tag == "lagging" && m.Value == 1);
        Assert.Contains(seen, m => m.Name == "crystal.replication.slots" && m.Tag == "active" && m.Value == 0);
        Assert.Contains(seen, m => m.Name == "crystal.replication.lag" && m.Value == 30);
        Assert.Contains(seen, m => m.Name == "crystal.replication.lag_time" && m.Value == 2.0);
    }

    // ------------------------------------------------------------------ helpers

    private static string? TagOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "crystal.slot.state")
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private static CrystalStore Primary(TempStoreDirectory dir, CrystalOptions options, out CrystalDatabase db, int writes)
    {
        var store = CrystalStore.Open(dir.Path, options);
        db = store.CreateDatabase("db", replicated: true);
        db.Boxes.Create("box");
        Write(db, writes);
        store.Replication.Listen();
        return store;
    }

    private static int _key;

    private static void Write(CrystalDatabase db, int batches)
    {
        var box = db.Boxes.Find("box")!.Value;
        for (var i = 0; i < batches; i++)
        {
            using var batch = db.NewBatch();
            batch.Put(box, Text.UTF8.GetBytes($"k{Interlocked.Increment(ref _key)}"), "v"u8);
            batch.Commit(DurabilityMode.Strict);
        }
    }

    private static List<ReplicationEvent> Collect(CrystalStore store)
    {
        var events = new List<ReplicationEvent>();
        store.Replication.Events += (_, e) => { lock (events) { events.Add(e); } };
        return events;
    }

    private sealed class Standby : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly ReplicationClient _client;

        private Standby(TcpClient tcp, ReplicationClient client)
        {
            _tcp = tcp;
            _client = client;
        }

        public static async Task<Standby> Connect(CrystalStore primary)
        {
            var endpoint = primary.Replication.ListenerEndpoint!;
            var colon = endpoint.LastIndexOf(':');
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(endpoint[..colon], int.Parse(endpoint[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            var client = new ReplicationClient(tcp.GetStream(), "standby-1", Secret);
            await client.HandshakeAsync(ReplicationProtocol.RoleStandby, 1, CancellationToken.None);
            return new Standby(tcp, client);
        }

        public Task<RespValue> Pull(string uid, long from) =>
            _client.RequestAsync(
                [Bytes(ReplicationProtocol.Pull), Bytes("1048576"), Bytes("0"), Bytes(uid), Bytes(from.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return default;
        }

        private static byte[] Bytes(string text) => Text.UTF8.GetBytes(text);
    }
}
