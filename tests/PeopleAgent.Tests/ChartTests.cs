using System.Text;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PeopleAgent.Tests;

[TestClass]
public sealed class ChartTests(TestContext testContext) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "people-charts-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void FromToolCall_ReferencesCurrentRecords_UsesExactValuesAndSourceLinks()
    {
        var records = Records();
        using var args = JsonDocument.Parse("""{"field":"price","citationIds":[1,2]}""");
        var chart = AnswerChart.FromToolCall(args.RootElement, records);

        Assert.AreEqual(89.99m, chart.Points[0].Value);
        Assert.AreEqual("Water pump", chart.Points[0].Source.Document.Title);
        using var card = JsonDocument.Parse(JsonSerializer.Serialize(ActivityChartCard.Content(chart)));
        var body = card.RootElement.GetProperty("body");
        var graph = body.EnumerateArray().Single(element => element.GetProperty("type").GetString() == "Chart.HorizontalBar");
        Assert.AreEqual(89.99m, graph.GetProperty("data")[0].GetProperty("y").GetDecimal());
        Assert.IsNotNull(graph.GetProperty("fallback").GetProperty("text").GetString());
        Assert.Contains("Source date not provided", card.RootElement.GetProperty("speak").GetString()!);
        var sourceRow = body[4].GetProperty("items")[0].GetProperty("columns")[0].GetProperty("items")[0];
        Assert.Contains("https://parts.test/1", sourceRow.GetProperty("text").GetString()!);
        Assert.HasCount(2, body[4].GetProperty("items").EnumerateArray().ToArray());
    }

    [TestMethod]
    [DataRow("""{"field":"price","citationIds":[1,99]}""")]
    [DataRow("""{"field":"invented","citationIds":[1,2]}""")]
    [DataRow("""{"field":"price","citationIds":[1,1]}""")]
    [DataRow("""{"field":"price","citationIds":[1,"2"]}""")]
    [DataRow("""{"field":"price","citationIds":[1]}""")]
    [DataRow("""{"field":"price","citationIds":[1,2],"values":[9999,9999]}""")]
    [DataRow("""{"field":"price","citationIds":[1,2,3,4,5,6,7,8,9,10,11]}""")]
    public void FromToolCall_InvalidSelection_RejectsRatherThanInventingOrDroppingPoints(string input)
    {
        using var args = JsonDocument.Parse(input);
        Assert.ThrowsExactly<InvalidDataException>(() => AnswerChart.FromToolCall(args.RootElement, Records()));
    }

    [TestMethod]
    [DataRow("AUD", "USD")]
    [DataRow("AUD", null)]
    [DataRow("dollars", "dollars")]
    public void FromToolCall_IncompatibleCurrency_RejectsComparison(string? first, string? second)
    {
        var records = Records();
        records[1] = records[1] with { Currency = first };
        records[2] = records[2] with { Currency = second };
        using var args = JsonDocument.Parse("""{"field":"price","citationIds":[1,2]}""");
        Assert.ThrowsExactly<InvalidDataException>(() => AnswerChart.FromToolCall(args.RootElement, records));
    }

    [TestMethod]
    public async Task CompleteAsync_SuccessfulAnswer_SendsAttachmentOnlyAfterText()
    {
        var events = new List<string>();

        await ActivityChartCard.CompleteAsync(Result(), _ =>
        {
            events.Add("text completed");
            return Task.FromResult(StreamingResponseResult.Success);
        }, (activity, _) =>
        {
            events.Add("chart");
            var attachment = Assert.ContainsSingle(activity.Attachments);
            Assert.AreEqual("application/vnd.microsoft.card.adaptive", attachment.ContentType);
            return Task.CompletedTask;
        }, testContext.CancellationToken);
        Assert.AreEqual("text completed,chart", string.Join(",", events));
    }

    [TestMethod]
    public async Task CompleteAsync_FailedStreamOrTurnOrCancellation_DoesNotSendChart()
    {
        var sent = 0;

        Task Send(IActivity _, CancellationToken token)
        {
            sent++;
            return Task.CompletedTask;
        }
        await Assert.ThrowsExactlyAsync<IOException>(() => ActivityChartCard.CompleteAsync(Result(),
            _ => Task.FromException<StreamingResponseResult>(new IOException("Delivery failed")),
            Send, testContext.CancellationToken));
        var failedDelivery = Enum.GetValues<StreamingResponseResult>().First(value => value != StreamingResponseResult.Success);
        await Assert.ThrowsExactlyAsync<IOException>(() => ActivityChartCard.CompleteAsync(Result(),
            _ => Task.FromResult(failedDelivery), Send, testContext.CancellationToken));
        await ActivityChartCard.CompleteAsync(Result() with { Error = "model_failure" },
            _ => Task.FromResult(StreamingResponseResult.Success), Send, testContext.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ActivityChartCard.CompleteAsync(Result(),
            _ => Task.FromResult(StreamingResponseResult.Success), Send, cancellation.Token));
        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    public async Task ReplyAsync_SearchRefinementThenChart_EnforcesSearchLimitAndGroundsChart()
    {
        var model = FakeModel.Script(
            FakeModel.Tool("knowledge_search", """{"query":"pump"}"""),
            FakeModel.Tool("knowledge_search", """{"query":"seal"}"""),
            FakeModel.Tool("knowledge_search", """{"query":"more"}"""),
            FakeModel.Tool("present_chart", """{"field":"price","citationIds":[1,2]}"""),
            FakeModel.Text("Indexed prices: pump [1], seal [2]."));
        var chunks = new StringBuilder();

        var result = await Engine(model, allowCharts: true).ReplyAsync(new ConversationSession(),
            "compare prices in a chart", text => chunks.Append(text), testContext.CancellationToken);

        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Chart);
        Assert.AreEqual(result.Text, chunks.ToString());
        Assert.AreEqual(2, result.Usage!.KnowledgeSearches);
        Assert.AreEqual(89.99m, result.Chart.Points[0].Value);
        Assert.Contains("numericFields", FakeModel.ToolResults(model.Requests[1].Messages).First());
        Assert.Contains("Search limit reached", FakeModel.ToolResults(model.Requests[3].Messages).Last());
    }

    [TestMethod]
    public async Task ReplyAsync_ChartRejected_ReturnsToolErrorToModelWithoutChart()
    {
        var model = FakeModel.Script(FakeModel.Tool("knowledge_search", """{"query":"parts"}"""),
            FakeModel.Tool("present_chart", """{"field":"missing","citationIds":[1,2]}"""),
            FakeModel.Text("Found records [1] [2]."));
        var result = await Engine(model, allowCharts: true).ReplyAsync(new ConversationSession(), "chart", _ => { },
            testContext.CancellationToken);

        Assert.IsNull(result.Error);
        Assert.IsNull(result.Chart);
        Assert.Contains("\"error\"", FakeModel.ToolResults(model.Requests[^1].Messages).Last());
    }

    [TestMethod]
    public async Task ReplyAsync_FinalAnswerFailsAfterQueuedChart_DropsCardAndDoesNotSaveSession()
    {
        var model = FakeModel.Script(FakeModel.Tool("knowledge_search", """{"query":"parts"}"""),
            FakeModel.Tool("present_chart", """{"field":"price","citationIds":[1,2]}"""),
            FakeModel.Text("Unknown source [99]."));
        var session = new ConversationSession();
        var result = await Engine(model, allowCharts: true).ReplyAsync(session, "compare parts", _ => { },
            testContext.CancellationToken);
        Assert.IsNotNull(result.Error);
        Assert.IsNull(result.Chart);
        Assert.IsNull(session.AgentSession);
    }

    [TestMethod]
    public async Task ReplyAsync_ChartsNotEnabled_PreservesPlainAnswerAndDoesNotOfferChartTool()
    {
        var model = FakeModel.Script(FakeModel.Tool("knowledge_search", """{"query":"parts"}"""), FakeModel.Text("Pump [1]."));
        var result = await Engine(model, allowCharts: false).ReplyAsync(new ConversationSession(), "parts", _ => { },
            testContext.CancellationToken);
        Assert.IsNull(result.Chart);
        Assert.AreEqual("Pump [1].", result.Text);
        Assert.IsFalse(model.Requests.Any(request =>
            request.Options?.Tools?.Any(tool => tool.Name == "present_chart") == true));
    }

    private ConversationEngine Engine(FakeModel model, bool allowCharts)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:StorageDirectory"] = _directory,
            ["Demo:TimeZone"] = "UTC",
            ["Demo:SensitiveTopicPattern"] = @"\bbullying\b"
        }).Build();
        var agent = PeopleAgentFactory.Create(model,
            new PeopleAgentFactory.Options("Answer from retrieved records.", new Search(), allowCharts));
        return new ConversationEngine(new TopicPolicy(configuration), new ConversationAgent(agent),
            new Telemetry(configuration, NullLogger<Telemetry>.Instance), TimeProvider.System);
    }

    private static Dictionary<int, KnowledgeDocument> Records() => new()
    {
        [1] = new(1, "Water pump", [], "Indexed pump", "https://parts.test/1",
            NumericFields: new Dictionary<string, decimal> { ["price"] = 89.99m }),
        [2] = new(2, "Door seal", [], "Indexed seal", "https://parts.test/2",
            NumericFields: new Dictionary<string, decimal> { ["price"] = 42.50m })
    };

    private static TurnResult Result()
    {
        using var args = JsonDocument.Parse("""{"field":"price","citationIds":[1,2]}""");
        var chart = AnswerChart.FromToolCall(args.RootElement, Records());
        return new("Knowledge answer", "Parts [1] [2].", false,
            chart.Points.Select(point => point.Source).ToArray(), null, Chart: chart);
    }

    private sealed class Search : IKnowledgeSearch
    {
        public string ProviderName => "offline";
        public string ToolDescription => "Search indexed parts.";
        public Task<IReadOnlyList<KnowledgeDocument>> SearchAsync(string query, string? userToken, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocument>>(Records().Values.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
