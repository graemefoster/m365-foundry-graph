namespace PeopleAgent.Core;

/// <summary>Stores the state needed to continue one conversation.</summary>
public sealed class ConversationSession
{
    /// <summary>Time of the most recent user activity.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }
    /// <summary>The Agent Framework session (recent conversation history), serialized.</summary>
    public string? AgentSession { get; set; }
    /// <summary>Feedback cards that are still eligible for submission.</summary>
    public Dictionary<string, FeedbackAnswer> Answers { get; set; } = [];
    /// <summary>Documents from the most recent knowledge search, used to resolve follow-up citations when the model answers from history.</summary>
    public Dictionary<int, KnowledgeDocument> LastRetrieved { get; set; } = [];

    /// <summary>Clears conversation history, feedback state, and activity time.</summary>
    public void Reset()
    {
        AgentSession = null;
        Answers.Clear();
        LastRetrieved.Clear();
        LastSeenUtc = null;
    }
}

/// <summary>Tracks whether a feedback answer was submitted and its card identity.</summary>
public sealed class FeedbackAnswer
{
    /// <summary>Whether feedback has already been accepted for this answer.</summary>
    public bool Submitted { get; set; }
    /// <summary>Activity ID of the card to replace after submission.</summary>
    public string? CardActivityId { get; set; }
}

/// <summary>A searchable source document, including its citation and optional numeric metadata.</summary>
public sealed record KnowledgeDocument(int Id, string Title, string[] Keywords, string Content,
    string? Url = null, string? SourceKey = null, bool IsSynthetic = true,
    IReadOnlyDictionary<string, decimal>? NumericFields = null, string? Currency = null)
{
    /// <summary>Stable source identity, falling back to the synthetic document ID.</summary>
    public string Identity => SourceKey ?? $"synthetic/{Id}";
}

/// <summary>A retrieved document associated with its citation number.</summary>
public sealed record AnswerSource(int CitationId, KnowledgeDocument Document);

/// <summary>Knowledge searches and model token usage for an answer.</summary>
public sealed record AnswerUsage(int KnowledgeSearches, long? InputTokens,
    long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens);

/// <summary>The response and outcome returned after processing a conversation turn.</summary>
public sealed record TurnResult(string Topic, string Text, bool SensitiveNotice,
    IReadOnlyList<AnswerSource> Sources, AnswerUsage? Usage, string? Error = null, AnswerChart? Chart = null);
