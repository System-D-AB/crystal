using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace SystemD.Crystal.Tests.Support;

/// <summary>
/// A meter factory that records what its own meters measure (spec 007 R6.1, open question 3: no test-only package).
/// A store given it as <c>CrystalOptions.MeterFactory</c> is measured in isolation: parallel tests' stores have meters
/// of their own, which this listener never enables.
/// </summary>
internal sealed class MetricsCapture : IMeterFactory
{
    private readonly ConcurrentBag<Meter> _meters = [];
    private readonly ConcurrentQueue<Recorded> _recorded = new();
    private readonly MeterListener _listener = new();

    public MetricsCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (_meters.Contains(instrument.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    public sealed record Recorded(string Name, double Value, IReadOnlyDictionary<string, string?> Tags);

    public IReadOnlyList<Recorded> All => [.. _recorded];

    /// <summary>Every measurement of <paramref name="name"/> so far, optionally only those with the given tags.</summary>
    public IReadOnlyList<Recorded> Of(string name, params (string Key, string Value)[] tags) =>
        [.. _recorded.Where(r => r.Name == name && tags.All(t => r.Tags.TryGetValue(t.Key, out var v) && v == t.Value))];

    /// <summary>Collects every observable gauge now, and returns what <paramref name="name"/> reported.</summary>
    public IReadOnlyList<Recorded> Collect(string name)
    {
        while (_recorded.TryDequeue(out _))
        {
        }

        _listener.RecordObservableInstruments();
        return Of(name);
    }

    public void Clear()
    {
        while (_recorded.TryDequeue(out _))
        {
        }
    }

    public Meter Create(MeterOptions options)
    {
        // Registered before any instrument is created on it, so InstrumentPublished always finds it.
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var map = new Dictionary<string, string?>();
        foreach (var tag in instrument.Meter.Tags ?? [])
        {
            map[tag.Key] = tag.Value?.ToString();   // the meter's own tags, such as crystal.node
        }

        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        _recorded.Enqueue(new Recorded(instrument.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), map));
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}
