using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CustomGraphConnector;

public sealed class GraphConnector(HttpClient http, TokenCredential credential, ConnectorSettings settings)
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0/";
    private string ConnectionPath => $"external/connections/{settings.ConnectionId}";

    private static object Property(string name, string type, bool searchable = false,
        bool queryable = false, string[]? labels = null) => new
        {
            name,
            type,
            isSearchable = searchable,
            isQueryable = queryable,
            isRetrievable = true,
            labels = labels ?? []
        };

    private static object Schema => new
    {
        baseType = "microsoft.graph.externalItem",
        properties = new[]
        {
            Property("partNumber", "int64", queryable: true),
            Property("name", "string", searchable: true, labels: ["title"]),
            Property("description", "string", searchable: true),
            Property("price", "double"),
            Property("inventory", "int64", queryable: true),
            Property("appliances", "stringCollection", searchable: true),
            Property("url", "string", labels: ["url"]),
            Property("iconUrl", "string", labels: ["iconUrl"])
        }
    };

    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        using var lookup = await SendAsync(HttpMethod.Get, ConnectionPath, null, cancellationToken);

        if (lookup.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await SendAsync(HttpMethod.Post, "external/connections", new
            {
                id = settings.ConnectionId,
                name = settings.ConnectionName,
                description = settings.ConnectionDescription ??
                    "Appliance parts from the Microsoft CSV connector tutorial, ingested by a local C# connector.",
                contentCategory = settings.ContentCategory
            }, cancellationToken);
            await EnsureSuccessAsync(created, cancellationToken);
            Console.WriteLine($"Created connection {settings.ConnectionId}.");
        }
        else
        {
            await EnsureSuccessAsync(lookup, cancellationToken);
            Console.WriteLine($"Using existing connection {settings.ConnectionId}.");
        }

        using var connectionResponse = await SendAsync(HttpMethod.Get, ConnectionPath, null, cancellationToken);
        await EnsureSuccessAsync(connectionResponse, cancellationToken);
        var connection = await ReadJsonAsync(connectionResponse, cancellationToken);
        var state = connection.GetProperty("state").GetString()?.ToLowerInvariant();

        if (state == "ready")
        {
            await VerifySchemaAsync(cancellationToken);
            Console.WriteLine("Schema is ready and matches the local connector.");

            return;
        }

        if (state == "draft")
        {
            var operationPath = Path.Combine(settings.StateDirectory, $"{settings.ConnectionId}.schema-operation.txt");

            if (File.Exists(operationPath))
            {
                var location = new Uri((await File.ReadAllTextAsync(operationPath, cancellationToken)).Trim(), UriKind.Absolute);
                await WaitForOperationAsync(location, cancellationToken);
            }
            else
            {
                using var schema = await SendAsync(HttpMethod.Patch, $"{ConnectionPath}/schema", Schema, cancellationToken);

                if (schema.StatusCode == HttpStatusCode.Conflict &&
                    (await ReadJsonAsync(schema, cancellationToken)).GetProperty("error")
                        .GetProperty("innerError").GetProperty("code").GetString() == "OperationInProgress")
                {
                    Console.WriteLine("Graph reports an existing schema operation; waiting for the connection to become ready.");
                }
                else
                {
                    await EnsureSuccessAsync(schema, cancellationToken);
                    var location = schema.Headers.Location
                        ?? throw new InvalidDataException("Graph accepted the schema without an operation Location header.");

                    Directory.CreateDirectory(settings.StateDirectory);
                    await File.WriteAllTextAsync(operationPath, location.ToString(), cancellationToken);
                    await WaitForOperationAsync(location, cancellationToken);
                }
            }
        }
        else if (state != "schemainprogress")
        {
            throw new InvalidOperationException($"Connection cannot be provisioned in state '{state}'.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));

        while (true)
        {
            using var response = await SendAsync(HttpMethod.Get, ConnectionPath, null, timeout.Token);
            await EnsureSuccessAsync(response, timeout.Token);
            var current = await ReadJsonAsync(response, timeout.Token);
            state = current.GetProperty("state").GetString()?.ToLowerInvariant();

            if (state == "ready")
                break;

            if (state is not ("schemainprogress" or "draft"))
                throw new InvalidOperationException($"Schema provisioning ended in state '{state}'.");

            Console.WriteLine($"Connection state: {state}; checking again in 15 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(15), timeout.Token);
        }

        await VerifySchemaAsync(cancellationToken);
        Console.WriteLine("Schema is ready and verified.");
    }

    public async Task RepairCopilotSchemaAsync(CancellationToken cancellationToken)
    {
        var operationPath = Path.Combine(settings.StateDirectory, $"{settings.ConnectionId}.icon-schema-operation.txt");
        if (File.Exists(operationPath))
        {
            var location = new Uri((await File.ReadAllTextAsync(operationPath, cancellationToken)).Trim(), UriKind.Absolute);
            await WaitForOperationAsync(location, cancellationToken);
        }
        else if (!await VerifySchemaAsync(cancellationToken, allowMissingIcon: true))
        {
            using var response = await SendAsync(HttpMethod.Patch, $"{ConnectionPath}/schema", Schema, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var location = response.Headers.Location
                ?? throw new InvalidDataException("Schema update did not return an operation Location header.");
            Directory.CreateDirectory(settings.StateDirectory);
            await File.WriteAllTextAsync(operationPath, location.ToString(), cancellationToken);
            await WaitForOperationAsync(location, cancellationToken);
        }
        await VerifySchemaAsync(cancellationToken);
        Console.WriteLine("Copilot schema verified, including title, url and iconUrl semantic labels.");
    }

    public async Task SyncAsync(IReadOnlyList<AppliancePart> parts, CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            using var response = await SendAsync(HttpMethod.Put, $"{ConnectionPath}/items/{part.Id}",
                part.ToExternalItem(settings.SourceUrl), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            Console.WriteLine($"Upserted {part.Id}: {part.Name}");
        }
        Console.WriteLine($"Uploaded {parts.Count} items.");
    }

    public async Task VerifyAsync(IReadOnlyList<AppliancePart> parts, CancellationToken cancellationToken)
    {
        await VerifySchemaAsync(cancellationToken);

        foreach (var part in parts)
        {
            using var response = await SendAsync(HttpMethod.Get, $"{ConnectionPath}/items/{part.Id}", null, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var actual = await ReadJsonAsync(response, cancellationToken);
            var expected = JsonSerializer.SerializeToElement(part.ToExternalItem(settings.SourceUrl));

            if (actual.GetProperty("id").GetString() != part.Id)
                throw new InvalidDataException($"Item ID mismatch for {part.Id}.");

            foreach (var property in expected.GetProperty("properties").EnumerateObject())
            {
                if (property.Name.EndsWith("@odata.type", StringComparison.Ordinal))
                    continue;

                if (!actual.GetProperty("properties").TryGetProperty(property.Name, out var actualValue) ||
                    !JsonNode.DeepEquals(JsonNode.Parse(property.Value.GetRawText()), JsonNode.Parse(actualValue.GetRawText())))
                    throw new InvalidDataException($"Item {part.Id} property '{property.Name}' does not match the CSV.");
            }

            foreach (var field in new[] { "type", "value" })
                if (actual.GetProperty("content").GetProperty(field).GetString() !=
                    expected.GetProperty("content").GetProperty(field).GetString())
                    throw new InvalidDataException($"Item {part.Id} content '{field}' mismatch.");

            var acl = actual.GetProperty("acl").EnumerateArray().ToArray();

            if (acl.Length != 1 || acl[0].GetProperty("type").GetString() != "everyone" ||
                acl[0].GetProperty("value").GetString() != "everyone" ||
                acl[0].GetProperty("accessType").GetString() != "grant")
                throw new InvalidDataException($"Item {part.Id} ACL does not match the approved tenant-wide access.");

            Console.WriteLine($"Verified {part.Id}: properties, content and ACL.");
        }

        Console.WriteLine($"Verified all {parts.Count} items against the source CSV.");
    }

    private async Task<bool> VerifySchemaAsync(CancellationToken cancellationToken, bool allowMissingIcon = false)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{ConnectionPath}/schema", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var actual = await ReadJsonAsync(response, cancellationToken);
        var expected = JsonSerializer.SerializeToElement(Schema);

        if (actual.GetProperty("baseType").GetString() != "microsoft.graph.externalItem")
            throw new InvalidDataException("Connection schema has an unexpected base type.");

        var properties = actual.GetProperty("properties").EnumerateArray()
            .ToDictionary(property => property.GetProperty("name").GetString()!);
        var missingIcon = allowMissingIcon && !properties.ContainsKey("iconUrl");
        var expectedCount = expected.GetProperty("properties").GetArrayLength() - (missingIcon ? 1 : 0);

        if (properties.Count != expectedCount)
            throw new InvalidDataException("Existing schema has different properties; use a separate connection ID.");

        foreach (var property in expected.GetProperty("properties").EnumerateArray())
        {
            var name = property.GetProperty("name").GetString()!;

            if (name == "iconUrl" && missingIcon)
                continue;

            if (!properties.TryGetValue(name, out var found))
                throw new InvalidDataException($"Schema is missing property '{name}'.");

            foreach (var field in new[] { "type", "isSearchable", "isQueryable", "isRetrievable", "labels" })
                if (!found.TryGetProperty(field, out var value) ||
                    !JsonNode.DeepEquals(JsonNode.Parse(value.GetRawText()), JsonNode.Parse(property.GetProperty(field).GetRawText())))
                    throw new InvalidDataException(
                        $"Schema property '{name}' field '{field}' differs from the local schema: Graph returned {value}, expected {property.GetProperty(field)}.");
        }

        return !missingIcon;
    }

    private async Task WaitForOperationAsync(Uri location, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));

        while (true)
        {
            using var response = await SendAsync(HttpMethod.Get, location.ToString(), null, timeout.Token);
            await EnsureSuccessAsync(response, timeout.Token);

            var operation = await ReadJsonAsync(response, timeout.Token);
            var status = operation.GetProperty("status").GetString()?.ToLowerInvariant();
            Console.WriteLine($"Schema operation: {status}");

            if (status == "completed")
                return;

            if (status is not ("inprogress" or "notstarted"))
                throw new InvalidOperationException($"Schema operation failed: {operation.GetRawText()}");

            await Task.Delay(TimeSpan.FromSeconds(15), timeout.Token);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(GraphRoot), path);

        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "graph.microsoft.com")
            throw new InvalidDataException("Refusing to send a Graph token to an unexpected operation URL.");

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Add("Prefer", "include-unknown-enum-members");

            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            if (body is not null)
                request.Content = JsonContent.Create(body);

            var response = await http.SendAsync(request, cancellationToken);

            // Only replay idempotent calls; a failed POST may already have created the connection.
            if (method == HttpMethod.Post || attempt >= 5 ||
                response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                return response;

            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));

            Console.Error.WriteLine($"Graph returned {(int)response.StatusCode}; retrying in {Math.Max(0, delay.TotalSeconds):F0}s.");
            response.Dispose();
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, cancellationToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Graph {(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }
}
