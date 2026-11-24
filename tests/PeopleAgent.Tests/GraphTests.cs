using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PeopleAgent.Tests;

[TestClass]
public sealed class GraphTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "people-graph-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GraphOptions _options;
    private readonly Telemetry _telemetry;
    private const string _user = "delegated-user-token";

    public GraphTests()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Graph:Enabled"] = "true",
            ["Graph:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["Graph:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["Graph:ConnectionId"] = "caldovaapplianceparts",
            ["Graph:OAuthConnectionName"] = "graph-connector",
            ["Demo:StorageDirectory"] = _directory
        }).Build();
        _options = new GraphOptions(configuration);
        _telemetry = new Telemetry(configuration, NullLogger<Telemetry>.Instance);
    }

    [TestMethod]
    public async Task SearchAsync_RealUserScope_UsesPerRequestTokenAndConfiguredConnection()
    {
        var handler = new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response()) });
        using var http = new HttpClient(handler);
        var search = new GraphKnowledgeSearch(http, _options, _telemetry);

        var results = await search.SearchAsync("water pump", _user, TestContext.CancellationToken);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Water pump", result.Title);
        Assert.AreEqual("https://example.test/parts/1009", result.Url);
        Assert.AreEqual("caldovaapplianceparts/stable-hit", result.Identity);
        Assert.Contains("89.99", result.Content);
        Assert.AreEqual(89.99m, result.NumericFields!["price"]);
        Assert.AreEqual(42m, result.NumericFields["inventory"]);
        Assert.DoesNotContain("<c0>", result.Content);
        Assert.AreEqual("Bearer delegated-user-token", handler.Authorization);
        Assert.IsNull(http.DefaultRequestHeaders.Authorization);
        using var request = JsonDocument.Parse(handler.Body!);
        var query = request.RootElement.GetProperty("requests")[0];
        Assert.AreEqual("/external/connections/caldovaapplianceparts", query.GetProperty("contentSources")[0].GetString());
        Assert.AreEqual("water pump", query.GetProperty("query").GetProperty("queryString").GetString());
        var logs = await File.ReadAllTextAsync(Path.Combine(_directory, "telemetry.jsonl"), TestContext.CancellationToken);
        Assert.DoesNotContain("delegated-user-token", logs);
        Assert.DoesNotContain("water pump", logs);
        Assert.DoesNotContain("89.99", logs);
    }

    [TestMethod]
    [DataRow("89.99", true)]
    [DataRow("89,99", false)]
    [DataRow("$89.99", false)]
    [DataRow("NaN", false)]
    public async Task SearchAsync_IndexedNumericString_OnlyExposesUnambiguousNumericFields(string price, bool chartable)
    {
        var search = await SearchWithResponse(Response(price: price));

        using (search.Http)
        {
            var result = Assert.ContainsSingle(await search.Search.SearchAsync("pump", _user, TestContext.CancellationToken));
            Assert.AreEqual(chartable, result.NumericFields!.ContainsKey("price"));
            if (chartable)
                Assert.AreEqual(89.99m, result.NumericFields["price"]);
        }
    }

    [TestMethod]
    public async Task SearchAsync_UnsignedConversation_DoesNotMakeNetworkRequest()
    {
        var handler = new Handler(_ => throw new AssertFailedException("Unauthenticated Graph request."));
        using var http = new HttpClient(handler);
        var search = new GraphKnowledgeSearch(http, _options, _telemetry);
        await Assert.ThrowsExactlyAsync<AuthenticationRequiredException>(() =>
            search.SearchAsync("water pump", null, TestContext.CancellationToken));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task SearchAsync_NoResults_ReturnsEmptyWithoutSyntheticFallback()
    {
        var search = await SearchWithResponse("""{"value":[{"hitsContainers":[{"total":0,"hits":[]}]}]}""");
        using (search.Http)
        {
            Assert.IsEmpty(await search.Search.SearchAsync("no-such-part", _user, TestContext.CancellationToken));
            Assert.AreEqual(1, search.Handler.Calls);
        }
    }

    [TestMethod]
    [DataRow("otherconnection", "https://example.test/parts/1009")]
    [DataRow("caldovaapplianceparts", "javascript:alert(1)")]
    public async Task SearchAsync_ForeignSourceOrUnsafeUrl_RejectsResult(string source, string url)
    {
        var search = await SearchWithResponse(Response(source, url));
        using (search.Http)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                search.Search.SearchAsync("water pump", _user, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SearchAsync_Forbidden_RecordsStatusWithoutResponseBodyOrSyntheticFallback()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("sensitive-service-error")
            };
            response.Headers.Add("request-id", "test-request");
            return response;
        });
        using var http = new HttpClient(handler);
        var search = new GraphKnowledgeSearch(http, _options, _telemetry);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            search.SearchAsync("private-query", _user, TestContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("sensitive-service-error", error.Message);
        var logs = await File.ReadAllTextAsync(Path.Combine(_directory, "telemetry.jsonl"), TestContext.CancellationToken);
        Assert.Contains("test-request", logs);
        Assert.DoesNotContain("private-query", logs);
        Assert.DoesNotContain("sensitive-service-error", logs);
        Assert.AreEqual(1, handler.Calls);
    }

    private async Task<(GraphKnowledgeSearch Search, HttpClient Http, Handler Handler)> SearchWithResponse(string response)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        var http = new HttpClient(handler);
        return (new GraphKnowledgeSearch(http, _options, _telemetry), http, handler);
    }

    private static string Response(string source = "caldovaapplianceparts", string url = "https://example.test/parts/1009",
        object? price = null) =>
        JsonSerializer.Serialize(new
        {
            value = new[] { new { hitsContainers = new[] { new { hits = new[] { new
            {
                hitId = "stable-hit", contentSource = source, summary = "<c0>Water</c0> pump<ddd/>",
                resource = new { properties = new { name = "Water pump", url, price = price ?? 89.99m, inventory = 42 } }
            } } } } } }
        });

    public TestContext TestContext { get; set; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
