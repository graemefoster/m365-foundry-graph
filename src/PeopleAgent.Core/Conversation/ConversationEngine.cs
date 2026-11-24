using System.ClientModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Microsoft.Agents.AI;

namespace PeopleAgent.Core;

/// <summary>
/// Routes each Teams / Microsoft 365 Copilot turn: scripted replies, session expiry and the sensitive-topic notice
/// are handled here; questions are answered by the Agent Framework agent, whose session is kept in
/// <see cref="ConversationSession.AgentSession"/>.
/// </summary>
public sealed class ConversationEngine(
    TopicPolicy policy, ConversationAgent conversationAgent, Telemetry telemetry, TimeProvider clock)
{
    private readonly AIAgent _agent = conversationAgent.Agent;

    private const int MaximumQuestionLength = 12000;

    private const string ExpiredSessionNotice =
        "Your previous session expired after a period of inactivity, so I've started a new one.";
    private const string ContentPolicyRefusal =
        "This request was blocked by the service's content policy. Please rephrase your question and try again.";
    private const string GenerationFailure =
        "Something went wrong and the answer may be incomplete. No action was taken. Please try again.";

    /// <summary>
    /// Handles one user turn end to end: routes the question, applies session expiry and the
    /// sensitive-topic notice, then either sends a scripted reply or streams a model answer.
    /// <paramref name="userToken"/> is the signed-in user's delegated Graph token (Teams SSO), if any.
    /// </summary>
    public async Task<TurnResult> ReplyAsync(ConversationSession session, string question,
        Action<string> onText, CancellationToken cancellationToken, string? userToken = null)
    {
        ValidateQuestion(question);

        var now = clock.GetUtcNow();
        var sensitive = policy.IsSensitive(question);
        var (topic, directText) = policy.Route(question, now);

        var expired = await ResetIfExpiredAsync(session, now, onText, cancellationToken);
        if (topic == "Start Over")
            session.Reset();
        session.LastSeenUtc = now;
        await telemetry.RecordAsync("Telemetry - User Turn", new { topic, sensitive, expired }, cancellationToken);

        if (sensitive)
            await SendSupportNoticeAsync(onText, cancellationToken);

        if (directText is not null)
            return await SendScriptedReplyAsync(topic, directText, sensitive, onText, cancellationToken);

        return await GenerateAnswerAsync(session, question, topic, sensitive, onText, userToken, cancellationToken);
    }

    /// <summary>
    /// Starts a fresh session when the user has been idle longer than the policy timeout,
    /// and tells them why. Returns whether the session had expired.
    /// </summary>
    private async Task<bool> ResetIfExpiredAsync(ConversationSession session, DateTimeOffset now,
        Action<string> onText, CancellationToken cancellationToken)
    {
        var expired = session.LastSeenUtc is { } lastSeen && now - lastSeen >= policy.SessionTimeout;
        if (!expired)
            return false;

        session.Reset();
        await telemetry.RecordAsync("Telemetry - Conversation Inactive", new { reason = "inactivity" }, cancellationToken);
        onText(ExpiredSessionNotice + "\n\n" + policy.CurrentGreeting + "\n\n");
        return true;
    }

    /// <summary>Prefixes the reply with the support notice for sensitive topics.</summary>
    private async Task SendSupportNoticeAsync(Action<string> onText, CancellationToken cancellationToken)
    {
        onText(TopicPolicy.SupportNotice + "\n\n");

        await telemetry.RecordAsync("Sensitive Topic Disclaimer", new { matched = true }, cancellationToken);
    }

    /// <summary>Sends a fixed reply chosen by the topic policy (greeting, help, etc.) without calling the model.</summary>
    private async Task<TurnResult> SendScriptedReplyAsync(string topic, string directText, bool sensitive,
        Action<string> onText, CancellationToken cancellationToken)
    {
        onText(directText);

        await telemetry.RecordAsync("Telemetry - Plan Completed", new { topic, modelCalls = 0 }, cancellationToken);
        return new TurnResult(topic, directText, sensitive, [], null);
    }

    /// <summary>
    /// Streams a model-generated answer. Known failures (content filter, Graph sign-in needed,
    /// transient service errors) become a friendly message and a failed <see cref="TurnResult"/>.
    /// </summary>
    private async Task<TurnResult> GenerateAnswerAsync(ConversationSession session, string question,
        string topic, bool sensitive, Action<string> onText, string? userToken, CancellationToken cancellationToken)
    {
        TurnResult answer;
        try
        {
            answer = await RunAgentAsync(session, question, topic, sensitive, onText, userToken, cancellationToken);
        }
        catch (ClientResultException exception) when (IsContentFilterRefusal(exception))
        {
            return await ReportContentPolicyRefusalAsync(exception, sensitive, onText, cancellationToken);
        }
        catch (Exception exception) when (policy.GraphEnabled && IsGraphSignInRequired(exception))
        {
            return await ReportSignInRequiredAsync(session, exception, sensitive, onText, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableGenerationFailure(exception))
        {
            return await ReportGenerationFailureAsync(exception, sensitive, onText, cancellationToken);
        }

        await telemetry.RecordAsync("Telemetry - Plan Completed",
            new { topic, sources = answer.Sources.Select(s => s.Document.Id), usage = answer.Usage }, cancellationToken);
        return answer;
    }

    /// <summary>True when the model service rejected the request with its content filter.</summary>
    private static bool IsContentFilterRefusal(ClientResultException exception) =>
        exception.Status == 400 && exception.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the failure means the user's delegated Graph token is missing or no longer valid.</summary>
    private static bool IsGraphSignInRequired(Exception exception) =>
        exception is AuthenticationRequiredException or AuthenticationFailedException
            or Azure.Identity.AuthenticationRequiredException
        || exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized };

    /// <summary>Errors from the model, Graph or response parsing that should end the turn gracefully.</summary>
    private static bool IsRecoverableGenerationFailure(Exception exception) =>
        exception is ClientResultException or AuthenticationFailedException
            or IOException or InvalidDataException or HttpRequestException or JsonException;

    private async Task<TurnResult> ReportContentPolicyRefusalAsync(ClientResultException exception, bool sensitive,
        Action<string> onText, CancellationToken cancellationToken)
    {
        onText("\n\n" + ContentPolicyRefusal);
        await telemetry.RecordAsync("model.content_policy_refusal", new { status = exception.Status }, cancellationToken);
        return new TurnResult("Content policy refusal", ContentPolicyRefusal, sensitive, [], null, "content_filter");
    }

    /// <summary>
    /// Clears the session and asks the user to sign in again. The "graph_auth_required" outcome
    /// tells the channel layer to sign the user out of Teams SSO so the next turn re-prompts.
    /// </summary>
    private async Task<TurnResult> ReportSignInRequiredAsync(ConversationSession session, Exception exception,
        bool sensitive, Action<string> onText, CancellationToken cancellationToken)
    {
        session.Reset();
        onText("\n\n" + GraphCopy.NotSignedIn);
        await telemetry.RecordAsync("graph.auth.required", new { reason = exception.GetType().Name }, cancellationToken);
        return new TurnResult("Sign in required", GraphCopy.NotSignedIn, sensitive, [], null, "graph_auth_required");
    }

    private async Task<TurnResult> ReportGenerationFailureAsync(Exception exception, bool sensitive,
        Action<string> onText, CancellationToken cancellationToken)
    {
        System.Diagnostics.Activity.Current?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
        onText("\n\n" + GenerationFailure);
        await telemetry.RecordAsync("On Error", new
        {
            errorType = exception.GetType().Name,
            status = (exception as ClientResultException)?.Status
        }, cancellationToken);
        return new TurnResult("On Error", GenerationFailure, sensitive, [], null, exception.GetType().Name);
    }

    /// <summary>Rejects empty or oversized questions before any work is done.</summary>
    private static void ValidateQuestion(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (question.Length > MaximumQuestionLength)
            throw new ArgumentException("Messages must be 12,000 characters or shorter.", nameof(question));
    }

    /// <summary>
    /// Runs the agent on the conversation's session, streaming its text. A tool failure that ends the turn
    /// (see <see cref="AnswerRun.Failure"/>) is rethrown here. Every [n] citation must refer to a document
    /// retrieved in this turn.
    /// </summary>
    private async Task<TurnResult> RunAgentAsync(ConversationSession session, string question, string topic,
        bool sensitive, Action<string> onText, string? userToken, CancellationToken cancellationToken)
    {
        var agentSession = session.AgentSession is { } saved
            ? await _agent.DeserializeSessionAsync(JsonDocument.Parse(saved).RootElement, cancellationToken: cancellationToken)
            : await _agent.CreateSessionAsync(cancellationToken);
        var scope = new AnswerRun { User = userToken };
        List<AgentResponseUpdate> updates = [];
        string? messageId = null;
        await foreach (var update in _agent.RunStreamingAsync(question, agentSession, scope.ToRunOptions(), cancellationToken))
        {
            updates.Add(update);
            if (string.IsNullOrEmpty(update.Text))
                continue;
            // Text before a tool call and the answer after it arrive as separate messages.
            if (messageId is not null && update.MessageId != messageId)
                onText("\n\n");
            messageId = update.MessageId;
            onText(update.Text);
        }
        if (scope.Failure is { } failure)
            ExceptionDispatchInfo.Throw(failure);
        var response = updates.ToAgentResponse();
        if (string.IsNullOrWhiteSpace(response.Text))
            throw new InvalidDataException("The agent returned no answer text.");
        var citedIds = CitationIds(response.Text);

        if (citedIds.Any(id => !scope.Retrieved.ContainsKey(id)))
            throw new InvalidDataException("The answer referenced a document that was not retrieved.");

        session.AgentSession = (await _agent.SerializeSessionAsync(agentSession, cancellationToken: cancellationToken)).GetRawText();
        var sources = citedIds.Concat(scope.Chart?.Points.Select(point => point.Source.CitationId) ?? [])
            .Distinct().Select(id => new AnswerSource(id, scope.Retrieved[id])).ToArray();
        var usage = response.Usage;

        return new TurnResult(topic, response.Text, sensitive, sources,
            new AnswerUsage(scope.Searches, usage?.InputTokenCount, usage?.OutputTokenCount,
                usage?.CachedInputTokenCount, usage?.ReasoningTokenCount), Chart: scope.Chart);
    }

    /// <summary>Distinct citation numbers written as [n] in the answer text.</summary>
    private static int[] CitationIds(string text) =>
        Regex.Matches(text, @"\[(\d+)\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct().ToArray();
}
