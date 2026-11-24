using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CustomGraphConnector.Tests;

[TestClass]
public sealed class ConnectorRegressionTests
{
    private const string Header = "PartNumber,Name,Description,Price,Inventory,Appliances\n";
    private const string ConnectionPath = "/v1.0/external/connections/testparts";
    private const string OperationUrl = "https://graph.microsoft.com/v1.0/external/connections/testparts/operations/schema1";

    [TestMethod]
    [DataRow("")]
    [DataRow("1,Pump,Replacement pump,12.5,2,Washer\n1,Pump,Replacement pump,12.5,2,Washer\n")]
    public void Load_EmptyOrDuplicateCsv_RejectsInput(string rows)
    {
        using var files = new TestFiles();
        var csv = files.Write("parts.csv", Header + rows);

        Assert.ThrowsExactly<InvalidDataException>(() => AppliancePart.Load(csv));
    }

    [TestMethod]
    public void Load_QuotedCsvAndSemicolonLists_UsesInvariantCulture()
    {
        using var files = new TestFiles();
        var csv = files.Write("parts.csv", Header +
            "123,\"Pump, \"\"premium\"\"\",\"First line\nSecond line\",12.5,2,\" Washer ; ; Dryer; \"\n");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var part = Assert.ContainsSingle(AppliancePart.Load(csv));

            Assert.AreEqual(123L, part.PartNumber);
            Assert.AreEqual("Pump, \"premium\"", part.Name);
            Assert.AreEqual("First line\nSecond line", part.Description);
            Assert.AreEqual(12.5, part.Price);
            Assert.AreEqual(2L, part.Inventory);
            Assert.HasCount(2, part.Appliances);
            Assert.AreEqual("Washer", part.Appliances[0]);
            Assert.AreEqual("Dryer", part.Appliances[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    [DataRow("https://example.invalid/parts", "https://example.invalid/parts#part-123")]
    [DataRow("https://example.invalid/parts/{id}.txt", "https://example.invalid/parts/123.txt")]
    public void ToExternalItem_ContentCollectionAndAcl_MapsTenantWideContract(string sourceUrl, string expectedUrl)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var item = JsonSerializer.SerializeToElement(Part().ToExternalItem(sourceUrl));

            Assert.AreEqual("123", item.GetProperty("id").GetString());
            var properties = item.GetProperty("properties");
            Assert.AreEqual("Collection(String)", properties.GetProperty("appliances@odata.type").GetString());
            Assert.AreEqual("[\"Washer\",\"Dryer\"]", properties.GetProperty("appliances").GetRawText());
            Assert.AreEqual(expectedUrl, properties.GetProperty("url").GetString());
            Assert.AreEqual("text", item.GetProperty("content").GetProperty("type").GetString());
            Assert.AreEqual("Pump\nPart number: 123\nReplacement pump\nPrice: 12.5\nInventory: 2\nAppliances: Washer, Dryer",
                item.GetProperty("content").GetProperty("value").GetString());
            var acl = item.GetProperty("acl");
            Assert.AreEqual(1, acl.GetArrayLength());
            Assert.AreEqual("everyone", acl[0].GetProperty("type").GetString());
            Assert.AreEqual("everyone", acl[0].GetProperty("value").GetString());
            Assert.AreEqual("grant", acl[0].GetProperty("accessType").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    [DataRow("draft")]
    [DataRow("schemainprogress")]
    public async Task ProvisionAsync_ExistingConnection_CompletesAndVerifiesSchema(string state)
    {
        using var files = new TestFiles();
        using var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var settings = Settings(files.Directory);
        handler.Add("GET", ConnectionPath, "{}");
        handler.Add("GET", ConnectionPath, State(state));
        if (state == "draft")
        {
            handler.Add("PATCH", ConnectionPath + "/schema", "{}", HttpStatusCode.Accepted,
                inspect: body => Assert.IsTrue(JsonNode.DeepEquals(Schema(), JsonNode.Parse(body!))),
                location: OperationUrl);
            handler.Add("GET", new Uri(OperationUrl).AbsolutePath, """{"status":"completed"}""");
        }
        handler.Add("GET", ConnectionPath, State("ready"));
        handler.Add("GET", ConnectionPath + "/schema", Schema().ToJsonString());

        await new GraphConnector(client, new FakeCredential(), settings).ProvisionAsync(CancellationToken.None);

        handler.AssertConsumed();
        var operationFile = Path.Combine(files.Directory, "testparts.schema-operation.txt");
        if (state == "draft")
            Assert.AreEqual(OperationUrl, await File.ReadAllTextAsync(operationFile));
        else
            Assert.IsFalse(File.Exists(operationFile));
    }

    [TestMethod]
    public async Task ProvisionAsync_LowercaseInprogress_CancelsThenResumesSavedOperationWithoutPatch()
    {
        using var files = new TestFiles();
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var connector = new GraphConnector(client, new FakeCredential(), Settings(files.Directory));
        handler.Add("GET", ConnectionPath, "{}");
        handler.Add("GET", ConnectionPath, State("draft"));
        handler.Add("PATCH", ConnectionPath + "/schema", "{}", HttpStatusCode.Accepted, location: OperationUrl);
        handler.Add("GET", new Uri(OperationUrl).AbsolutePath, """{"status":"inprogress"}""",
            inspect: _ => cancellation.CancelAfter(TimeSpan.FromMilliseconds(100)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => connector.ProvisionAsync(cancellation.Token));

        handler.AssertConsumed();
        Assert.AreEqual(OperationUrl, await File.ReadAllTextAsync(Path.Combine(files.Directory, "testparts.schema-operation.txt")));
        handler.Add("GET", ConnectionPath, "{}");
        handler.Add("GET", ConnectionPath, State("draft"));
        handler.Add("GET", new Uri(OperationUrl).AbsolutePath, """{"status":"completed"}""");
        handler.Add("GET", ConnectionPath, State("ready"));
        handler.Add("GET", ConnectionPath + "/schema", Schema().ToJsonString());

        await connector.ProvisionAsync(CancellationToken.None);

        handler.AssertConsumed();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepairCopilotSchemaAsync_AddsOnlyMissingIconAndPreservesExistingFields(bool alreadyComplete)
    {
        using var files = new TestFiles();
        using var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var schema = Schema();
        if (!alreadyComplete)
        {
            var properties = schema["properties"]!.AsArray();
            properties.RemoveAt(properties.Count - 1);
        }
        handler.Add("GET", ConnectionPath + "/schema", schema.ToJsonString());
        if (!alreadyComplete)
        {
            handler.Add("PATCH", ConnectionPath + "/schema", "{}", HttpStatusCode.Accepted,
                inspect: body => Assert.IsTrue(JsonNode.DeepEquals(Schema(), JsonNode.Parse(body!))),
                location: OperationUrl);
            handler.Add("GET", new Uri(OperationUrl).AbsolutePath, """{"status":"completed"}""");
        }
        handler.Add("GET", ConnectionPath + "/schema", Schema().ToJsonString());

        await new GraphConnector(client, new FakeCredential(), Settings(files.Directory))
            .RepairCopilotSchemaAsync(CancellationToken.None);

        handler.AssertConsumed();
    }

    [TestMethod]
    public async Task ProvisionAsync_ReadyWithMismatchedSchema_RejectsSchema()
    {
        using var files = new TestFiles();
        using var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var schema = Schema();
        schema["properties"]![0]!["type"] = "string";
        handler.Add("GET", ConnectionPath, "{}");
        handler.Add("GET", ConnectionPath, State("ready"));
        handler.Add("GET", ConnectionPath + "/schema", schema.ToJsonString());

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new GraphConnector(client, new FakeCredential(), Settings(files.Directory)).ProvisionAsync(CancellationToken.None));

        Assert.Contains("partNumber", exception.Message);
        Assert.Contains("type", exception.Message);
        handler.AssertConsumed();
    }

    [TestMethod]
    [DataRow("matching")]
    [DataRow("property")]
    [DataRow("content")]
    [DataRow("acl")]
    public async Task VerifyAsync_ItemResponse_AcceptsMatchingAndRejectsChangedData(string scenario)
    {
        using var files = new TestFiles();
        using var handler = new ScriptedHandler();
        using var client = new HttpClient(handler);
        var settings = Settings(files.Directory);
        var part = Part();
        var item = JsonSerializer.SerializeToNode(part.ToExternalItem(settings.SourceUrl))!;
        item["properties"]!.AsObject().Remove("appliances@odata.type");

        var expectedError = scenario switch
        {
            "property" => "property 'price'",
            "content" => "content 'value'",
            "acl" => "ACL",
            _ => ""
        };

        if (scenario == "property")
            item["properties"]!["price"] = 99;
        else if (scenario == "content")
            item["content"]!["value"] = "Changed content";
        else if (scenario == "acl")
            item["acl"]![0]!["accessType"] = "deny";

        handler.Add("GET", ConnectionPath + "/schema", Schema().ToJsonString());
        handler.Add("GET", ConnectionPath + "/items/123", item.ToJsonString());
        var connector = new GraphConnector(client, new FakeCredential(), settings);

        if (scenario == "matching")
            await connector.VerifyAsync([part], CancellationToken.None);
        else
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                connector.VerifyAsync([part], CancellationToken.None));
            Assert.Contains(expectedError, exception.Message);
        }

        handler.AssertConsumed();
    }

    private static AppliancePart Part() => new()
    {
        PartNumber = 123,
        Name = "Pump",
        Description = "Replacement pump",
        Price = 12.5,
        Inventory = 2,
        Appliances = ["Washer", "Dryer"]
    };

    private static ConnectorSettings Settings(string directory) => new()
    {
        TenantId = "fake-tenant",
        ClientId = "fake-client",
        CertificatePath = "",
        PrivateKeyPath = "",
        ConnectionId = "testparts",
        ConnectionName = "Test parts",
        CsvPath = "",
        SourceUrl = "https://example.invalid/parts",
        StateDirectory = directory
    };

    private static string State(string state) => JsonSerializer.Serialize(new { state });

    private static JsonNode Schema()
    {
        var properties = new JsonArray();
        foreach (var (name, type, searchable, queryable, labels) in new[]
        {
            ("partNumber", "int64", false, true, Array.Empty<string>()),
            ("name", "string", true, false, new[] { "title" }),
            ("description", "string", true, false, Array.Empty<string>()),
            ("price", "double", false, false, Array.Empty<string>()),
            ("inventory", "int64", false, true, Array.Empty<string>()),
            ("appliances", "stringCollection", true, false, Array.Empty<string>()),
            ("url", "string", false, false, new[] { "url" }),
            ("iconUrl", "string", false, false, new[] { "iconUrl" })
        })
            properties.Add(JsonSerializer.SerializeToNode(new
            {
                name,
                type,
                isSearchable = searchable,
                isQueryable = queryable,
                isRetrievable = true,
                labels
            }));
        return new JsonObject { ["baseType"] = "microsoft.graph.externalItem", ["properties"] = properties };
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.AreEqual("https://graph.microsoft.com/.default", Assert.ContainsSingle(requestContext.Scopes));
            return new AccessToken("fake-token-not-a-credential", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<ExpectedRequest> requests = new();

        public void Add(string method, string path, string json, HttpStatusCode status = HttpStatusCode.OK,
            Action<string?>? inspect = null, string? location = null)
            => requests.Enqueue(new(method, path, json, status, inspect, location));

        public void AssertConsumed() => Assert.IsEmpty(requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.IsNotEmpty(requests);
            var expected = requests.Dequeue();
            Assert.AreEqual(expected.Method, request.Method.Method);
            Assert.AreEqual("https://graph.microsoft.com" + expected.Path, request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("include-unknown-enum-members", Assert.ContainsSingle(request.Headers.GetValues("Prefer")));
            Assert.AreEqual("fake-token-not-a-credential", request.Headers.Authorization?.Parameter);
            expected.Inspect?.Invoke(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(expected.Status)
            {
                Content = new StringContent(expected.Json, Encoding.UTF8, "application/json")
            };
            if (expected.Location is not null)
                response.Headers.Location = new Uri(expected.Location);
            return response;
        }

        private sealed record ExpectedRequest(string Method, string Path, string Json, HttpStatusCode Status,
            Action<string?>? Inspect, string? Location);
    }

    private sealed class TestFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(
            AppContext.BaseDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));

        public TestFiles() => System.IO.Directory.CreateDirectory(Directory);

        public string Write(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
