using Microsoft.Agents.AI;

namespace PeopleAgent.Core;

/// <summary>
/// Per-run tool state, passed to the agent in <see cref="AgentRunOptions.AdditionalProperties"/> and read by the
/// tools through <see cref="AIAgent.CurrentRunContext"/>: the signed-in user's Graph token, documents numbered
/// for citation, any queued chart, and a failure that must end the turn.
/// </summary>
public sealed class AnswerRun
{
    private const string Key = "people-agent.answer-run";

    /// <summary>The signed-in user's delegated Graph token (Teams SSO); null for the bundled sample documents.</summary>
    public string? User { get; init; }

    /// <summary>Retrieved documents keyed by the citation number shown to the model.</summary>
    public Dictionary<int, KnowledgeDocument> Retrieved { get; } = [];

    public int Searches { get; set; }
    public AnswerChart? Chart { get; set; }

    /// <summary>A tool failure that must end the turn (e.g. Graph sign-in required) rather than be told to the model.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Run options carrying this state to the tools.</summary>
    public AgentRunOptions ToRunOptions() => new() { AdditionalProperties = new() { [Key] = this } };

    /// <summary>The state of the current run, or a fresh one when the caller supplied none (Responses hosting).</summary>
    public static AnswerRun Current =>
        AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?.TryGetValue(Key, out var value) == true
        && value is AnswerRun run ? run : new AnswerRun();

    /// <summary>Numbers documents not seen earlier in this answer (repeats keep their first number) and returns each one's number.</summary>
    public IReadOnlyList<int> AddRetrieved(IEnumerable<KnowledgeDocument> documents)
    {
        List<int> ids = [];
        foreach (var doc in documents)
        {
            var existing = Retrieved.FirstOrDefault(pair => pair.Value.Identity == doc.Identity);
            var id = existing.Value is null ? Retrieved.Count + 1 : existing.Key;
            Retrieved[id] = existing.Value ?? doc;
            ids.Add(id);
        }
        return ids;
    }
}
