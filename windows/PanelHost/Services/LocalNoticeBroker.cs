using System.Text.Json.Serialization;

namespace PanelHost.Services;

internal sealed record LocalNotice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("page")] string? Page,
    [property: JsonPropertyName("durationSeconds")] int DurationSeconds,
    [property: JsonPropertyName("receivedAtUnixMs")] long ReceivedAtUnixMs,
    [property: JsonPropertyName("expiresAtUnixMs")] long ExpiresAtUnixMs);
internal sealed record LocalPublishedValue(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("updatedAtUnixMs")] long UpdatedAtUnixMs,
    [property: JsonPropertyName("expiresAtUnixMs")] long? ExpiresAtUnixMs);
internal sealed record LocalNoticesMessage(
    [property: JsonPropertyName("sampledAtUnixMs")] long SampledAtUnixMs,
    [property: JsonPropertyName("notices")] IReadOnlyList<LocalNotice> Notices,
    [property: JsonPropertyName("values")] IReadOnlyList<LocalPublishedValue> Values)
{
    [JsonPropertyName("type")] public string Type => "local_notices";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
}

internal sealed class LocalNoticeBroker(TimeProvider? clock = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<LocalNotice> _notices = [];
    private readonly Dictionary<string, LocalPublishedValue> _values = new(StringComparer.Ordinal);
    private bool _enabled;
    internal void SetEnabled(bool enabled) { lock (_sync) _enabled = enabled; }
    internal LocalNoticesMessage Snapshot()
    {
        lock (_sync)
        {
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            _notices.RemoveAll(x => x.ExpiresAtUnixMs <= now);
            foreach (var entry in _values.Where(x => x.Value.ExpiresAtUnixMs <= now).ToArray()) _values.Remove(entry.Key);
            return new(now, _notices.ToArray(), _values.Values.ToArray()) { Enabled = _enabled };
        }
    }

    internal void Publish(LocalNoticePayload notice)
    {
        lock (_sync)
        {
            var now = Snapshot().SampledAtUnixMs;
            if (notice.Key is not null) _notices.RemoveAll(x => x.Key == notice.Key);
            _notices.Add(new(Guid.NewGuid().ToString("N"), notice.Title, notice.Text, notice.Level, notice.Key, notice.Page,
                notice.DurationSeconds, now, now + notice.TtlSeconds * 1000L));
            while (_notices.Count > LocalIntegrationLimits.Notices) _notices.RemoveAt(0);
        }
    }

    internal bool Publish(LocalValuePayload value)
    {
        lock (_sync)
        {
            var now = Snapshot().SampledAtUnixMs;
            if (!_values.ContainsKey(value.Name) && _values.Count >= LocalIntegrationLimits.Values) return false;
            _values[value.Name] = new(value.Name, value.Value, value.Unit, value.Level, now,
                value.TtlSeconds is int ttl ? now + ttl * 1000L : null);
            return true;
        }
    }

    internal void Clear() { lock (_sync) { _notices.Clear(); _values.Clear(); } }
}
