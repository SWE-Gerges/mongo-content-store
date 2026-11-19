using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Content.Mongo.Serialization;

/// <summary>
/// Thread-safe <see cref="IMalformedFieldSink"/> that counts defects per field and reason and publishes them
/// through the <c>Content.Mongo</c> meter as <c>content.mongo.malformed_fields</c>.
/// </summary>
public sealed class MalformedFieldCounter : IMalformedFieldSink, IDisposable
{
    public const string MeterName = "Content.Mongo";
    public const string CounterName = "content.mongo.malformed_fields";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _counter;
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public MalformedFieldCounter()
    {
        _counter = _meter.CreateCounter<long>(CounterName, unit: "{field}", description: "Field values dropped because they could not be decoded.");
    }

    public long Total => _counts.Values.Sum();

    public void Report(MalformedField defect)
    {
        _counts.AddOrUpdate(Key(defect.Field, defect.Reason), 1, static (_, current) => current + 1);
        _counter.Add(
            1,
            new KeyValuePair<string, object?>("serializer", defect.Serializer),
            new KeyValuePair<string, object?>("field", defect.Field),
            new KeyValuePair<string, object?>("reason", defect.Reason));
    }

    /// <summary>Number of defects recorded for one field and reason.</summary>
    public long Count(string field, string reason) =>
        _counts.TryGetValue(Key(field, reason), out var value) ? value : 0;

    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(_counts, StringComparer.Ordinal);

    public void Dispose() => _meter.Dispose();

    private static string Key(string field, string reason) => $"{field}:{reason}";
}
