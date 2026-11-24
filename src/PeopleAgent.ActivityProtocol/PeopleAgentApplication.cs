using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace PeopleAgent.ActivityProtocol;

/// <summary>
/// The Teams / Microsoft 365 Copilot front end. Each incoming Activity becomes a call to the
/// <see cref="ConversationEngine"/> (which runs the Agent Framework agent); the result is streamed back with source links, citations,
/// an optional chart card and a feedback card.
/// </summary>
public sealed class PeopleAgentApplication : AgentApplication
{
    private const string TurnFailedMessage =
        "Something went wrong and the answer may be incomplete. No action was taken. Please try again.";
    private const int MaxRememberedAnswers = 50;
    /// <summary>The Agents SDK sign-in handler for Teams SSO (see ActivityProtocolHosting).</summary>
    public const string GraphHandler = "graph";

    private readonly ConversationEngine _engine;
    private readonly FeedbackService _feedback;
    private readonly Telemetry _telemetry;
    private readonly ILogger<PeopleAgentApplication> _logger;
    private readonly GraphOptions _graph;
    private readonly TopicPolicy _policy;
    private readonly TimeProvider _clock;
    private InteractiveBrowserCredential? _localCredential;

    public PeopleAgentApplication(AgentApplicationOptions options, ConversationEngine engine,
        FeedbackService feedback, Telemetry telemetry, ILogger<PeopleAgentApplication> logger,
        GraphOptions graph, TopicPolicy policy, TimeProvider clock) : base(options)
    {
        _engine = engine;
        _feedback = feedback;
        _telemetry = telemetry;
        _logger = logger;
        _graph = graph;
        _policy = policy;
        _clock = clock;
        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeAsync);
        if (UsesChannelOAuth)
            RegisterChannelOAuthRoutes();
        OnActivity(ActivityTypes.Message, MessageAsync, rank: RouteRank.Last);
    }

    private bool UsesChannelOAuth => _graph.Enabled && _graph.UseChannelOAuth;

    /// <summary>
    /// Sign-out is handled first. Graph questions and identity commands then go through a route that makes the
    /// SDK obtain the user's channel OAuth token (Teams SSO) before <see cref="MessageAsync"/> runs.
    /// Everything else (greetings, help, feedback) falls through to the unauthenticated message route.
    /// </summary>
    private void RegisterChannelOAuthRoutes()
    {
        AddRoute((context, _) => Task.FromResult(IsMessage(context) && CommandOf(context.Activity.Text) is "sign out" or "/signout"),
            ChannelSignOutAsync, rank: RouteRank.First);
        AddRoute(RequiresChannelAuthorization, MessageAsync, rank: RouteRank.First,
            autoSignInHandlers: [GraphHandler]);
    }

    /// <summary>True for identity commands and for any question the topic policy can't answer locally (so it needs Graph).</summary>
    private Task<bool> RequiresChannelAuthorization(ITurnContext context, CancellationToken cancellationToken)
    {
        var text = context.Activity.Text;
        return Task.FromResult(IsMessage(context) && context.Activity.Value is null
            && !string.IsNullOrWhiteSpace(text)
            && (IsIdentityCommand(CommandOf(text)) || _policy.Route(text, _clock.GetUtcNow()).Text is null));
    }

    /// <summary>Signs the user out of the channel OAuth connection and forgets the conversation.</summary>
    private async Task ChannelSignOutAsync(ITurnContext context, ITurnState state, CancellationToken cancellationToken)
    {
        await UserAuthorization.SignOutUserAsync(context, state, GraphHandler, cancellationToken);
        SessionOf(state, UserKeyOf(context)).Reset();
        await _telemetry.RecordAsync("graph.auth.signed_out", new { method = "bot-service-oauth" }, cancellationToken);
        await ReplyAsync(context, GraphCopy.SignedOut, cancellationToken);
    }

    /// <summary>Greets users (not the bot itself) when they join the conversation.</summary>
    private async Task WelcomeAsync(ITurnContext context, ITurnState state, CancellationToken cancellationToken)
    {
        if (context.Activity.MembersAdded?.Any(member => member.Id != context.Activity.Recipient?.Id) == true)
            await ReplyAsync(context, _policy.CurrentGreeting, cancellationToken);
    }

    /// <summary>Handles one user message: a feedback card submission, an identity command, or a question to answer.</summary>
    private async Task MessageAsync(ITurnContext context, ITurnState state, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var userKey = UserKeyOf(context);
        var session = SessionOf(state, userKey);

        if (context.Activity.Value is { } submitted)
        {
            await HandleFeedbackSubmitAsync(context, session, submitted, cancellationToken);
            return;
        }
        var text = context.Activity.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(context, _policy.CurrentHelp, cancellationToken);
            return;
        }
        if (UsesChannelOAuth && IsIdentityCommand(CommandOf(text)))
        {
            await ReplyWithSignInStatusAsync(context, cancellationToken);
            return;
        }
        await AnswerQuestionAsync(context, state, session, text, userKey, stopwatch, cancellationToken);
    }

    /// <summary>Records a Yes/No feedback card submission and replaces the card with "Feedback received.".</summary>
    private async Task HandleFeedbackSubmitAsync(ITurnContext context, ConversationSession session, object submitted,
        CancellationToken cancellationToken)
    {
        var element = submitted is JsonElement json ? json : JsonSerializer.SerializeToElement(submitted);
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("action", out var action)
            || action.GetString() != "feedback.submit")
        {
            await ReplyAsync(context, "Sorry, I couldn't record that feedback.", cancellationToken);
            return;
        }
        var request = element.Deserialize<FeedbackRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reply = await _feedback.SubmitAsync(session, request!, cancellationToken);
        if (request?.AnswerId is { } answerId
            && session.Answers.TryGetValue(answerId, out var answer)
            && answer.Submitted && answer.CardActivityId is { } cardId)
            await ReplaceFeedbackCardAsync(context, cardId, cancellationToken);
        await ReplyAsync(context, reply, cancellationToken);
    }

    /// <summary>Overwrites the original feedback card so it can't be submitted twice.</summary>
    private static async Task ReplaceFeedbackCardAsync(ITurnContext context, string cardActivityId, CancellationToken cancellationToken)
    {
        var completedCard = MessageFactory.Attachment(new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = new
            {
                type = "AdaptiveCard",
                version = "1.4",
                body = new[] { new { type = "TextBlock", text = "Feedback received.", wrap = true } }
            }
        });
        completedCard.Id = cardActivityId;
        await context.UpdateActivityAsync(completedCard, cancellationToken);
    }

    /// <summary>Replies to "sign in" / "who am i" by checking whether the channel already holds a token for this user.</summary>
    private async Task ReplyWithSignInStatusAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        var token = await UserAuthorization.GetTurnTokenAsync(context, GraphHandler, cancellationToken);
        await ReplyAsync(context, string.IsNullOrWhiteSpace(token)
            ? GraphCopy.NotSignedIn : "Graph OAuth sign-in is active for this conversation.", cancellationToken);
    }

    /// <summary>
    /// Streams the engine's answer, then finishes the stream with source links and citations, sends any chart card,
    /// and offers a feedback card. Any failure ends the turn with a short apology instead of a partial answer.
    /// </summary>
    private async Task AnswerQuestionAsync(ITurnContext context, ITurnState state, ConversationSession session,
        string text, string userKey, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var stream = context.StreamingResponse;
        var user = await AcquireUserTokenAsync(context, cancellationToken);
        var platform = ClientPlatform(context.Activity);
        var isMobile = IsMobilePlatform(platform);
        var streamEnded = false;
        try
        {
            var result = await _engine.ReplyAsync(session, text, stream.QueueTextChunk, cancellationToken, user, isMobile);

            // Graph rejected the SSO token: clear it so the next turn prompts a fresh sign-in.
            if (result.Error == "graph_auth_required" && _graph.UseChannelOAuth)
                await UserAuthorization.SignOutUserAsync(context, state, GraphHandler, cancellationToken);

            if (result.Sources.Count > 0)
                stream.QueueTextChunk("\n\n" + string.Join("\n", result.Sources.Select(SourceLine)));
            if (result.Usage is not null)
                stream.QueueTextChunk("\n\n" + FormFactorTag(platform));
            stream.FinalMessage = FinalMessageWithCitations(stream.Message, result);

            streamEnded = true;
            await ActivityChartCard.CompleteAsync(result, stream.EndStreamAsync,
                async (activity, token) => { await context.SendActivityAsync(activity, token); }, cancellationToken);

            if (result.Usage is not null && result.Sources.Count > 0)
                await SendFeedbackCardAsync(context, session, cancellationToken);

            await RecordTurnCompletedAsync(userKey, result, stopwatch, cancellationToken);
        }
        catch (Exception exception) when (exception is ClientResultException or AuthenticationFailedException
            or IOException or JsonException or ArgumentException)
        {
            await HandleTurnFailureAsync(context, exception, streamEnded, stopwatch, cancellationToken);
        }
    }

    /// <summary>
    /// Gets the signed-in user's delegated Graph token: from Bot Service channel OAuth in Teams/M365,
    /// or via InteractiveBrowserCredential when Graph:LocalInteractiveAuth=true in local Playground testing.
    /// </summary>
    private async Task<string?> AcquireUserTokenAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        if (UsesChannelOAuth)
            return await UserAuthorization.GetTurnTokenAsync(context, GraphHandler, cancellationToken);
        if (!_graph.Enabled || !_graph.LocalInteractiveAuth)
            return null;
        _localCredential ??= new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = _graph.TenantId,
            ClientId = _graph.ClientId,
            RedirectUri = new Uri("http://localhost")
        });
        var token = await _localCredential.GetTokenAsync(new TokenRequestContext([GraphOptions.Scope]), cancellationToken);
        return token.Token;
    }

    /// <summary>Reads the optional <c>clientInfo.platform</c> value from the incoming Activity.</summary>
    public static string? ClientPlatform(IActivity activity)
    {
        var clientInfo = activity.Entities?.FirstOrDefault(
            entity => string.Equals(entity.Type, "clientInfo", StringComparison.OrdinalIgnoreCase));
        if (clientInfo?.Properties is null || !clientInfo.Properties.TryGetValue("platform", out var raw))
            return null;
        return raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();
    }

    /// <summary>
    /// Detects mobile Teams/M365 clients from the Activity protocol's optional <c>clientInfo</c> entity
    /// (<c>platform</c>: <c>iOS</c>, <c>Android</c>, or <c>mobile</c>). Defaults to desktop when omitted.
    /// </summary>
    public static bool IsMobileClient(IActivity activity) => IsMobilePlatform(ClientPlatform(activity));

    private static bool IsMobilePlatform(string? platform) =>
        string.Equals(platform, "iOS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, "Android", StringComparison.OrdinalIgnoreCase)
        || string.Equals(platform, "mobile", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compact form-factor footer tag shown on model answers for demo visibility.</summary>
    public static string FormFactorTag(string? platform)
    {
        var label = IsMobilePlatform(platform) ? "📱 Mobile" : "🖥️ Desktop";
        return string.IsNullOrWhiteSpace(platform) ? $"*{label}*" : $"*{label} ({platform})*";
    }

    /// <summary>A visible "Source: ..." line under the answer, linked when the document has a URL.</summary>
    private static string SourceLine(AnswerSource source)
    {
        var document = source.Document;
        var title = document.Url is null ? document.Title : $"[{document.Title}]({document.Url})";
        return $"Source: {title}{(document.IsSynthetic ? " (synthetic sample)" : "")}.";
    }

    /// <summary>
    /// Builds the final streamed message. Model answers carry an AI-generated-content entity with one citation per
    /// source; it is attached only here, never to intermediate stream chunks.
    /// </summary>
    private static IActivity FinalMessageWithCitations(string text, TurnResult result)
    {
        var finalMessage = MessageFactory.Text(text);
        if (result.Usage is null)
            return finalMessage;
        var entity = new AIEntity
        {
            Citation = result.Sources.Select(source => new ClientCitation
            {
                Position = source.CitationId,
                Appearance = new ClientCitationAppearance
                {
                    Name = source.Document.Title + (source.Document.IsSynthetic ? " (synthetic sample)" : ""),
                    Abstract = source.Document.Content[..Math.Min(150, source.Document.Content.Length)],
                    Url = source.Document.Url
                }
            }).ToList()
        };
        entity.AdditionalType.Add(AIEntity.AdditionalTypeAIGeneratedContent);
        finalMessage.Entities ??= [];
        finalMessage.Entities.Add(entity);
        return finalMessage;
    }

    /// <summary>Sends a Yes/No feedback card and remembers it (up to the last 50 answers) so it can be replaced on submit.</summary>
    private static async Task SendFeedbackCardAsync(ITurnContext context, ConversationSession session, CancellationToken cancellationToken)
    {
        var answerId = Guid.NewGuid().ToString("N");
        var answer = new FeedbackAnswer();
        session.Answers[answerId] = answer;
        if (session.Answers.Count > MaxRememberedAnswers)
            session.Answers.Remove(session.Answers.Keys.First());
        var card = MessageFactory.Attachment(new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = FeedbackCard(answerId)
        });
        var sent = await context.SendActivityAsync(card, cancellationToken);
        answer.CardActivityId = sent.Id;
    }

    /// <summary>Adaptive Card asking "Was this answer helpful?" with an optional comment.</summary>
    public static object FeedbackCard(string answerId) => new
    {
        type = "AdaptiveCard",
        version = "1.4",
        body = new object[]
        {
            new { type = "TextBlock", text = "Was this answer helpful?", weight = "Bolder", wrap = true },
            new { type = "Input.Text", id = "comment", placeholder = "Optional comment",
                isMultiline = true, maxLength = 500 }
        },
        actions = new[]
        {
            new { type = "Action.Submit", id = "feedbackYes", title = "Yes",
                data = new { action = "feedback.submit", rating = "helpful", answerId } },
            new { type = "Action.Submit", id = "feedbackNo", title = "No",
                data = new { action = "feedback.submit", rating = "notHelpful", answerId } }
        }
    };

    private async Task RecordTurnCompletedAsync(string userKey, TurnResult result, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await _telemetry.RecordAsync("turn.completed", new
        {
            user = userKey,
            result.Topic,
            result.Error,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds
        }, cancellationToken);
        _telemetry.RecordTurnDuration(stopwatch.Elapsed.TotalMilliseconds, result.Topic);
    }

    /// <summary>
    /// Logs the failure and tells the user. If the answer stream is still open the apology is appended to it;
    /// otherwise (the stream already ended) it is sent as a separate message.
    /// </summary>
    private async Task HandleTurnFailureAsync(ITurnContext context, Exception exception, bool streamEnded,
        Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        // Marks the Activity SDK's invoke_agent span for this turn as failed.
        System.Diagnostics.Activity.Current?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        _logger.LogError(exception, "Agent turn failed; no successful answer is recorded.");
        await _telemetry.RecordAsync("turn.error", new
        {
            errorType = exception.GetType().Name,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds
        }, cancellationToken);
        if (streamEnded)
        {
            await ReplyAsync(context, TurnFailedMessage, cancellationToken);
            return;
        }
        var stream = context.StreamingResponse;
        stream.QueueTextChunk("\n\n" + TurnFailedMessage);
        stream.FinalMessage = MessageFactory.Text(stream.Message);
        await stream.EndStreamAsync(cancellationToken);
    }

    private static Task ReplyAsync(ITurnContext context, string text, CancellationToken cancellationToken) =>
        context.SendActivityAsync(MessageFactory.Text(text), cancellationToken);

    private static bool IsMessage(ITurnContext context) => context.Activity.Type == ActivityTypes.Message;

    /// <summary>User text normalised for command matching ("Sign in." → "sign in").</summary>
    private static string CommandOf(string? text) => TopicPolicy.Normalize(text ?? "").TrimEnd('.', '!', '?');

    private static bool IsIdentityCommand(string command) =>
        command is "sign in" or "/signin" or "who am i" or "identity" or "/identity";

    /// <summary>Hashed user ID, used for state keys and telemetry so raw IDs are never stored.</summary>
    private static string UserKeyOf(ITurnContext context) => Telemetry.HashIdentifier(context.Activity.From?.Id ?? "local-user");

    /// <summary>The per-user conversation state, persisted by the Activity host's conversation storage.</summary>
    private static ConversationSession SessionOf(ITurnState state, string userKey) =>
        state.Conversation.GetValue("people-" + userKey, () => new ConversationSession());
}
