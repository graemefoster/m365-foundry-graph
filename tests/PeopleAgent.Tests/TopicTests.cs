using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]

namespace PeopleAgent.Tests;

[TestClass]
public sealed class TopicTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "people-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TopicPolicy _policy;
    private readonly Telemetry _telemetry;
    private readonly ManualClock _clock = new();
    private readonly FakeModel _model = FakeModel.SearchThenAnswer("generated answer");
    private readonly Search _search = new();

    public TopicTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:StorageDirectory"] = _directory,
            ["Demo:SensitiveTopicPattern"] = @"\b(bullying|harassment|self[- ]?harm|overwhelmed)\b",
            ["Demo:SessionTimeoutMinutes"] = "720",
            ["Demo:TimeZone"] = "Australia/Sydney"
        }).Build();
        _policy = new TopicPolicy(configuration);
        _telemetry = new Telemetry(configuration, NullLogger<Telemetry>.Instance);
    }

    [TestMethod]
    [DataRow(" HELLO! ", "Greeting")]
    [DataRow("thank you", "Thank you")]
    [DataRow("goodbye", "Goodbye")]
    [DataRow("leave", "Seek Clarification to Avoid Ambiguous Answers")]
    [DataRow("Book my leave", "Capability boundary")]
    [DataRow("What is my leave balance?", "Capability boundary")]
    [DataRow("Approve this now but do not act until I confirm", "Seek Clarification to Avoid Ambiguous Answers")]
    [DataRow("How do I book annual leave?", "Knowledge answer")]
    public void Route_KnownInput_SelectsCorrectTopic(string input, string expectedTopic)
    {
        Assert.AreEqual(expectedTopic, _policy.Route(input, _clock.GetUtcNow()).Topic);
    }

    [TestMethod]
    public void Route_DateQuestion_UsesInjectedClockAndConfiguredTimeZone()
    {
        var result = _policy.Route("what is the current date and time?", _clock.GetUtcNow());

        Assert.AreEqual("Get Current Date and Time", result.Topic);
        Assert.Contains("17 November 2026", result.Text!);
        Assert.Contains("Australia/Sydney", result.Text!);
    }

    [TestMethod]
    [DataRow("I'm experiencing BULLYING", true)]
    [DataRow("I feel overwhelmed", true)]
    [DataRow("the annual leave policy", false)]
    public void IsSensitive_NormalisedInput_MatchesIllustrativeRules(string text, bool expected)
    {
        Assert.AreEqual(expected, _policy.IsSensitive(text));
    }

    [TestMethod]
    public async Task ReplyAsync_ExpiredSession_ClearsHistoryBeforeHandlingCurrentQuestion()
    {
        var session = new ConversationSession();
        var engine = Engine();
        await engine.ReplyAsync(session, "old private question", _ => { }, TestContext.CancellationToken);

        session.LastSeenUtc = _clock.GetUtcNow().AddHours(-12);
        session.Answers.Add("old-answer", new FeedbackAnswer());
        var output = new StringBuilder();

        await engine.ReplyAsync(session, "annual leave for Australian packaged employees", chunk => output.Append(chunk),
            TestContext.CancellationToken);

        Assert.IsFalse(AnyMessageContains(_model.Requests[^1].Messages, "old private question"));
        Assert.IsEmpty(session.Answers);
        Assert.Contains("previous session expired", output.ToString());
        Assert.Contains("generated answer", output.ToString());
    }

    [TestMethod]
    public async Task ReplyAsync_SensitiveQuestion_ShowsNoticeWithoutBlockingAnswer()
    {
        var output = new StringBuilder();

        var result = await Engine().ReplyAsync(new ConversationSession(), "help with bullying",
            chunk => output.Append(chunk), TestContext.CancellationToken);

        Assert.IsTrue(result.SensitiveNotice);
        Assert.StartsWith(TopicPolicy.SupportNotice, output.ToString());
        Assert.Contains("generated answer", output.ToString());
        var logs = await File.ReadAllTextAsync(Path.Combine(_directory, "telemetry.jsonl"), TestContext.CancellationToken);
        Assert.DoesNotContain("help with bullying", logs);
    }

    [TestMethod]
    public async Task ReplyAsync_StartOver_ClearsHistoryAndFeedbackWithoutCallingModel()
    {
        var session = new ConversationSession { AgentSession = "{}" };
        session.Answers.Add("old-answer", new FeedbackAnswer());

        await Engine().ReplyAsync(session, "start over", _ => { }, TestContext.CancellationToken);

        Assert.IsNull(session.AgentSession);
        Assert.IsEmpty(session.Answers);
        Assert.IsEmpty(_model.Requests);
    }

    [TestMethod]
    public async Task ReplyAsync_ServerSideConversationId_PreservesConversationAcrossTurns()
    {
        var turn = 0;
        var model = new FakeModel(messages =>
        {
            var response = messages[^1].Role == Microsoft.Extensions.AI.ChatRole.Tool
                ? FakeModel.Text("generated answer")
                : FakeModel.Tool("knowledge_search", """{"query":"policy"}""");
            response.ConversationId = $"resp_{++turn}";
            return response;
        });
        var engine = new ConversationEngine(_policy, new ConversationAgent(PeopleAgentFactory.Create(model,
            new PeopleAgentFactory.Options("Answer from policy.", _search, AllowCharts: true))), _telemetry, _clock);
        var session = new ConversationSession();

        var first = await engine.ReplyAsync(session, "first question", _ => { }, TestContext.CancellationToken);
        var second = await engine.ReplyAsync(session, "follow up", _ => { }, TestContext.CancellationToken);

        Assert.IsNull(first.Error);
        Assert.IsNull(second.Error);
        Assert.AreEqual("resp_2", model.Requests[2].Options?.ConversationId);
    }

    [TestMethod]
    public async Task SubmitAsync_DuplicateOrForeignFeedback_DoesNotCreateAdditionalRecords()
    {
        var session = new ConversationSession();
        session.Answers.Add("answer-1", new FeedbackAnswer());
        var service = new FeedbackService(_telemetry, _policy, _clock);
        var request = new FeedbackRequest("feedback.submit", "helpful", "answer-1", "Useful");

        Assert.AreEqual("Thanks for your feedback.", await service.SubmitAsync(session, request, TestContext.CancellationToken));
        Assert.Contains("already", await service.SubmitAsync(session, request, TestContext.CancellationToken));
        Assert.Contains("couldn't record", await service.SubmitAsync(new ConversationSession(), request, TestContext.CancellationToken));
        var rows = await File.ReadAllLinesAsync(Path.Combine(_directory, "feedback.jsonl"), TestContext.CancellationToken);
        Assert.HasCount(1, rows);
    }

    [TestMethod]
    public async Task SubmitAsync_ExpiredSession_RejectsPreviouslyIssuedCard()
    {
        var session = new ConversationSession { LastSeenUtc = _clock.GetUtcNow().AddHours(-12) };
        session.Answers.Add("answer-1", new FeedbackAnswer());
        var service = new FeedbackService(_telemetry, _policy, _clock);
        var reply = await service.SubmitAsync(session,
            new FeedbackRequest("feedback.submit", "helpful", "answer-1", null), TestContext.CancellationToken);
        Assert.Contains("ended or was reset", reply);
        Assert.IsEmpty(session.Answers);
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "feedback.jsonl")));
    }

    [TestMethod]
    public async Task ReplyAsync_ModelFailure_ReturnsExplicitErrorWithoutSavingAnswerAndAllowsRetry()
    {
        var session = new ConversationSession();
        var engine = Engine();
        _model.Failure = new IOException("Synthetic model failure");
        var failed = await engine.ReplyAsync(session, "annual leave", _ => { }, TestContext.CancellationToken);

        Assert.AreEqual("On Error", failed.Topic);
        Assert.AreEqual(nameof(IOException), failed.Error);
        Assert.IsNull(session.AgentSession);
        _model.Failure = null;
        var retry = await engine.ReplyAsync(session, "annual leave", _ => { }, TestContext.CancellationToken);
        Assert.IsNull(retry.Error);
        Assert.IsNotNull(session.AgentSession);
    }

    [TestMethod]
    public async Task ReplyAsync_SearchFailure_EndsTurnWithErrorInsteadOfLettingModelAnswer()
    {
        _search.Failure = new IOException("Synthetic retrieval failure");
        var session = new ConversationSession();
        var failed = await Engine().ReplyAsync(session, "annual leave", _ => { }, TestContext.CancellationToken);
        Assert.AreEqual(nameof(IOException), failed.Error);
        Assert.HasCount(1, _model.Requests);
        Assert.IsNull(session.AgentSession);
    }

    public TestContext TestContext { get; set; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task ReplyAsync_FollowUpCitingPriorSearchWithoutReSearching_ResolvesCitation()
    {
        var model = FakeModel.Script(
            FakeModel.Tool("knowledge_search", """{"query":"annual leave"}"""),
            FakeModel.Text("Full-time employees get 20 days [1]."),
            FakeModel.Text("Part-time leave is pro rata [1]."));
        var engine = new ConversationEngine(_policy, new ConversationAgent(PeopleAgentFactory.Create(model,
            new PeopleAgentFactory.Options("Answer from policy.", _search, AllowCharts: true))), _telemetry, _clock);
        var session = new ConversationSession();

        var first = await engine.ReplyAsync(session, "How many days of annual leave?", _ => { }, TestContext.CancellationToken);
        var second = await engine.ReplyAsync(session, "What about part-time?", _ => { }, TestContext.CancellationToken);

        Assert.IsNull(first.Error);
        Assert.IsNull(second.Error);
        Assert.HasCount(1, second.Sources);
        Assert.AreEqual("Leave policy", second.Sources[0].Document.Title);
    }

    [TestMethod]
    public async Task ReplyAsync_MobileClient_IncludesCompactFormattingHint()
    {
        var session = new ConversationSession();
        var engine = Engine();

        await engine.ReplyAsync(session, "annual leave", _ => { }, TestContext.CancellationToken, isMobile: true);
        Assert.IsTrue(AnyMessageContains(_model.Requests[0].Messages, "[Client: mobile"));

        session.Reset();
        await engine.ReplyAsync(session, "annual leave", _ => { }, TestContext.CancellationToken, isMobile: false);
        Assert.IsFalse(AnyMessageContains(_model.Requests[^1].Messages, "[Client: mobile"));
    }

    private sealed class ManualClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);
    }

    private ConversationEngine Engine() => new(_policy, new ConversationAgent(PeopleAgentFactory.Create(_model,
            new PeopleAgentFactory.Options("Answer from policy.", _search, AllowCharts: true))), _telemetry, _clock);

    private static bool AnyMessageContains(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, string text) =>
        messages.Any(message => message.Text.Contains(text, StringComparison.Ordinal));

    private sealed class Search : IKnowledgeSearch
    {
        public Exception? Failure { get; set; }
        public string ProviderName => "offline";
        public string ToolDescription => "Search HR policy.";
        public Task<IReadOnlyList<KnowledgeDocument>> SearchAsync(string query, string? userToken, CancellationToken token) =>
            Failure is not null
                ? Task.FromException<IReadOnlyList<KnowledgeDocument>>(Failure)
                : Task.FromResult<IReadOnlyList<KnowledgeDocument>>([new(1, "Leave policy", [], "Annual leave is 20 days.", null)]);
    }
}
