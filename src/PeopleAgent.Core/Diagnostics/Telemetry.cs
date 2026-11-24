using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PeopleAgent.Core;

/// <summary>
/// Writes the agent's own application events (to a local file and the log) and its app-level metrics.
/// Model and HTTP telemetry is not produced here: the OpenAI SDK and the Foundry host emit it.
/// </summary>
public sealed class Telemetry(IConfiguration configuration, ILogger<Telemetry> logger, IMeterFactory? meterFactory = null)
{
    /// <summary>Activity and metric source name used by this application.</summary>
    public const string SourceName = "PeopleAgent";
    /// <summary>Activity source for conversation and knowledge-search traces.</summary>
    public static readonly ActivitySource Activities = new(SourceName);
    private readonly Meter? _meter = meterFactory?.Create(SourceName);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _directory = Path.GetFullPath(configuration["Demo:StorageDirectory"] ?? ".local");

    /// <summary>Absolute directory used for local telemetry and feedback files.</summary>
    public string DirectoryPath => _directory;

    private Counter<long>? _eventCount;
    private Histogram<double>? _turnDuration;

    /// <summary>Writes a structured event and emits its event-count metric.</summary>
    public async Task RecordAsync(string eventName, object data, CancellationToken cancellationToken)
    {
        var row = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, eventName, data });
        await AppendAsync("telemetry.jsonl", row, cancellationToken);
        logger.LogInformation("Agent event {EventName}: {Data}", eventName, JsonSerializer.Serialize(data));
        if (_meter is not null)
        {
            _eventCount ??= _meter.CreateCounter<long>("agent.events");
            _eventCount.Add(1, new KeyValuePair<string, object?>("event.name", eventName));
        }
    }

    /// <summary>Records elapsed turn time when metrics are enabled.</summary>
    public void RecordTurnDuration(double milliseconds, string topic)
    {
        if (_meter is null)
            return;
        _turnDuration ??= _meter.CreateHistogram<double>("agent.turn.duration", "ms");
        _turnDuration.Record(milliseconds, new KeyValuePair<string, object?>("topic", topic));
    }

    /// <summary>Appends one line to a local file while serializing concurrent writes.</summary>
    public async Task AppendAsync(string file, string row, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            await File.AppendAllTextAsync(Path.Combine(_directory, file), row + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns a short, one-way hash suitable for telemetry identifiers.</summary>
    public static string HashIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
