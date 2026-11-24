using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;

namespace CustomGraphConnector;

public static class RetrievalTest
{
    public static async Task RunAsync(ConnectorSettings settings, IReadOnlyList<AppliancePart> parts,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(settings.RetrievalClientId, out _))
            throw new InvalidDataException("A retrieval client ID is required. Run scripts/setup-retrieval.ps1 first.");

        Directory.CreateDirectory(settings.StateDirectory);
        var authenticationPath = Path.Combine(settings.StateDirectory,
            $"retrieval-authentication-{settings.TenantId}-{settings.RetrievalClientId}.json");

        AuthenticationRecord? authentication = null;

        if (File.Exists(authenticationPath))
        {
            await using var saved = File.OpenRead(authenticationPath);
            authentication = await AuthenticationRecord.DeserializeAsync(saved, cancellationToken);
        }

        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = settings.TenantId,
            ClientId = settings.RetrievalClientId,
            RedirectUri = new Uri("http://localhost:8400"),
            AuthenticationRecord = authentication,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = $"CaldovaGraphRetrieval-{settings.TenantId}-{settings.RetrievalClientId}",
                UnsafeAllowUnencryptedStorage = false
            }
        });

        var tokenContext = new TokenRequestContext(["https://graph.microsoft.com/ExternalItem.Read.All"]);

        if (authentication is null)
        {
            Console.WriteLine("Sign in to Caldova in the browser; subsequent tests use the OS-protected token cache.");
            authentication = await credential.AuthenticateAsync(tokenContext, cancellationToken);

            var temporaryPath = authenticationPath + ".tmp";
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write
            };

            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var saved = new FileStream(temporaryPath, fileOptions))
                await authentication.SerializeAsync(saved, cancellationToken);

            File.Move(temporaryPath, authenticationPath, overwrite: true);
        }
        else
        {
            Console.WriteLine("Using the OS-protected retrieval token cache; browser sign-in occurs only if required.");
        }

        var token = await credential.GetTokenAsync(tokenContext, cancellationToken);
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var tokenParts = token.Token.Split('.');

        if (tokenParts.Length == 3)
        {
            var payload = tokenParts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');

            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            var identity = new
            {
                tenantId = claims.RootElement.GetProperty("tid").GetString(),
                objectId = claims.RootElement.GetProperty("oid").GetString(),
                scopes = claims.RootElement.GetProperty("scp").GetString()
            };

            Console.WriteLine($"Retrieval caller tenant: {identity.tenantId}; user object: {identity.objectId}; scopes: {identity.scopes}");
            await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, $"retrieval-{runId}-identity.json"),
                JsonSerializer.Serialize(identity), cancellationToken);
        }
        else
        {
            Console.WriteLine("The Graph token is opaque; diagnostic identity claim inspection is unavailable.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var cases = settings.RetrievalCasesPath is null ? new[]
        {
            new RetrievalCase("water-pump", "What is the price and inventory of the water pump for Contoso dishwashers?", null, ["1009"]),
            new RetrievalCase("compressor", "What is the price and stock availability of the compressor for Contoso refrigerators?", "partNumber:1004", ["1004"]),
            new RetrievalCase("low-stock", "Which parts for Contoso dishwashers have fewer than 50 units available?", "inventory<50", ["1007", "1009"]),
            new RetrievalCase("no-match", "What appliance part has part number 9999?", "partNumber:9999", [])
        } : JsonSerializer.Deserialize<RetrievalCase[]>(File.ReadAllText(settings.RetrievalCasesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Retrieval test cases are empty.");
        if (cases.Length == 0 || cases.Any(test =>
            string.IsNullOrWhiteSpace(test.Name) || !test.Name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
            string.IsNullOrWhiteSpace(test.Query) || test.Query.Length > 1500 || test.ExpectedIds is null ||
            test.ExpectedIds.Any(id => !parts.Any(part => part.Id == id))))
            throw new InvalidDataException("Retrieval cases require safe names, queries and expected IDs from the source dataset.");
        var failedCases = new List<string>();

        foreach (var test in cases)
        {
            var payload = new
            {
                queryString = test.Query,
                dataSource = "externalItem",
                dataSourceConfiguration = new
                {
                    externalItem = new { connections = new[] { new { connectionId = settings.ConnectionId } } }
                },
                filterExpression = test.Filter,
                resourceMetadata = new[] { "partNumber", "name", "description", "price", "inventory", "appliances", "url", "iconUrl" }
            };
            Console.WriteLine($"Retrieval test: {test.Name}");
            using var response = await http.PostAsJsonAsync("https://graph.microsoft.com/v1.0/copilot/retrieval",
                payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }, cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var evidence = Path.Combine(settings.StateDirectory, $"retrieval-{runId}-{test.Name}.json");
            await File.WriteAllTextAsync(evidence, body, cancellationToken);

            var requestId = response.Headers.TryGetValues("request-id", out var requestIds)
                ? string.Join(",", requestIds) : null;

            await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, $"retrieval-{runId}-{test.Name}.trace.json"),
                JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    connectionId = settings.ConnectionId,
                    query = test.Query,
                    filter = test.Filter,
                    status = (int)response.StatusCode,
                    requestId
                }), cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Retrieval API {(int)response.StatusCode}: {body}", null, response.StatusCode);

            using var document = JsonDocument.Parse(body);
            var hits = document.RootElement.GetProperty("retrievalHits").EnumerateArray().ToArray();
            var returnedIds = new HashSet<string>();

            foreach (var hit in hits)
            {
                if (hit.GetProperty("resourceType").GetString() != "externalItem")
                    throw new InvalidDataException("Retrieval returned an unexpected resource type.");

                var metadata = hit.GetProperty("resourceMetadata");
                var id = metadata.GetProperty("partNumber").ToString();
                var part = parts.SingleOrDefault(part => part.Id == id)
                    ?? throw new InvalidDataException($"Retrieved part '{id}' is not in the source CSV.");

                if (hit.GetProperty("webUrl").GetString() != part.GetUrl(settings.SourceUrl))
                    throw new InvalidDataException($"Retrieved part '{id}' has an unexpected source URL.");

                var expected = JsonSerializer.SerializeToElement(part.ToExternalItem(settings.SourceUrl));

                foreach (var property in expected.GetProperty("properties").EnumerateObject())
                {
                    if (property.Name.EndsWith("@odata.type", StringComparison.Ordinal))
                        continue;

                    if (!metadata.TryGetProperty(property.Name, out var value) ||
                        !JsonElement.DeepEquals(value, property.Value))
                        throw new InvalidDataException($"Retrieved part '{id}' metadata '{property.Name}' differs from the CSV.");
                }

                var extracts = hit.GetProperty("extracts").EnumerateArray().ToArray();

                if (extracts.Length == 0)
                    throw new InvalidDataException($"Retrieved part '{id}' has no grounding extracts.");

                var sourceContent = Normalize(expected.GetProperty("content").GetProperty("value").GetString()!);

                foreach (var extract in extracts)
                {
                    var text = extract.GetProperty("text").GetString();

                    if (string.IsNullOrWhiteSpace(text) || !sourceContent.Contains(Normalize(text), StringComparison.Ordinal))
                        throw new InvalidDataException($"Retrieved extract for part '{id}' does not match the source content.");

                    Console.WriteLine($"  Part {id}: {text}");
                }

                returnedIds.Add(id);
            }

            if (!test.ExpectedIds.All(returnedIds.Contains) ||
                (test.Filter is not null && !returnedIds.SetEquals(test.ExpectedIds)))
            {
                failedCases.Add(test.Name);
                Console.Error.WriteLine(
                    $"FAIL {test.Name}: expected [{string.Join(", ", test.ExpectedIds)}], returned [{string.Join(", ", returnedIds)}]. Evidence: {evidence}");
            }
            else
            {
                Console.WriteLine($"PASS {test.Name}: {hits.Length} hits verified against CSV. Evidence: {evidence}");
            }
        }

        await ProbeSearchAsync(http, settings, parts, runId, cases[0].Query, cancellationToken);

        if (failedCases.Count != 0)
        {
            await ProbeMinimalRetrievalAsync(http, settings, cases[0].Query,
                $"What information is available about {parts[0].Name}?", runId, cancellationToken);
            throw new InvalidDataException(
                $"Retrieval tests failed: {string.Join(", ", failedCases)}. Successful authentication or empty results are not proof of retrieval readiness.");
        }

        Console.WriteLine($"All {cases.Length} live Retrieval API tests passed.");
    }

    private static async Task ProbeMinimalRetrievalAsync(HttpClient http, ConnectorSettings settings,
        string query, string topicQuery, string runId, CancellationToken cancellationToken)
    {
        var probes = new (string Name, string Version, string Query, bool Scoped)[]
        {
            ("minimal-v1.0", "v1.0", query, true),
            ("minimal-beta", "beta", query, true),
            ("topic-scoped", "v1.0", topicQuery, true),
            ("topic-unscoped", "v1.0", topicQuery, false)
        };
        foreach (var probe in probes)
        {
            var payload = new Dictionary<string, object>
            {
                ["queryString"] = probe.Query,
                ["dataSource"] = "externalItem"
            };
            if (probe.Scoped)
                payload["dataSourceConfiguration"] = new
                {
                    externalItem = new { connections = new[] { new { connectionId = settings.ConnectionId } } }
                };
            using var response = await http.PostAsJsonAsync($"https://graph.microsoft.com/{probe.Version}/copilot/retrieval",
                payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var evidence = Path.Combine(settings.StateDirectory, $"retrieval-{runId}-{probe.Name}.json");
            await File.WriteAllTextAsync(evidence, body, cancellationToken);
            var requestId = response.Headers.TryGetValues("request-id", out var requestIds)
                ? string.Join(",", requestIds) : null;

            await File.WriteAllTextAsync(Path.ChangeExtension(evidence, "trace.json"),
                JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    request = payload,
                    status = (int)response.StatusCode,
                    requestId
                }), cancellationToken);
            Console.WriteLine($"{probe.Name} retrieval: HTTP {(int)response.StatusCode}. Evidence: {evidence}");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Minimal retrieval {(int)response.StatusCode}: {body}", null, response.StatusCode);
            using var document = JsonDocument.Parse(body);
            Console.WriteLine($"{probe.Name} retrieval: {document.RootElement.GetProperty("retrievalHits").GetArrayLength()} hits.");
        }
    }

    private static async Task ProbeSearchAsync(HttpClient http, ConnectorSettings settings,
        IReadOnlyList<AppliancePart> parts, string runId, string paraphrasedQuery, CancellationToken cancellationToken)
    {
        foreach (var query in new[] { paraphrasedQuery, "*" })
        {
            var payload = new
            {
                requests = new[]
                {
                    new
                    {
                        entityTypes = new[] { "externalItem" },
                        contentSources = new[] { $"/external/connections/{settings.ConnectionId}" },
                        query = new { queryString = query },
                        fields = new[] { "partNumber", "name", "price", "inventory" },
                        from = 0,
                        size = 25
                    }
                }
            };
            using var response = await http.PostAsJsonAsync("https://graph.microsoft.com/v1.0/search/query",
                payload, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var suffix = query == "*" ? "all" : "paraphrase";
            var evidence = Path.Combine(settings.StateDirectory, $"search-{runId}-{suffix}.json");
            await File.WriteAllTextAsync(evidence, body, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Graph Search {(int)response.StatusCode}: {body}", null, response.StatusCode);
            using var document = JsonDocument.Parse(body);
            var count = 0;
            foreach (var result in document.RootElement.GetProperty("value").EnumerateArray())
            {
                foreach (var container in result.GetProperty("hitsContainers").EnumerateArray())
                {
                    if (!container.TryGetProperty("hits", out var hits))
                        continue;
                    foreach (var hit in hits.EnumerateArray())
                    {
                        var properties = hit.GetProperty("resource").GetProperty("properties");
                        var id = properties.GetProperty("partNumber").ToString();
                        var part = parts.SingleOrDefault(part => part.Id == id)
                            ?? throw new InvalidDataException($"Search returned unexpected part '{id}'.");
                        if (properties.GetProperty("name").GetString() != part.Name ||
                            properties.GetProperty("price").GetDouble() != part.Price ||
                            properties.GetProperty("inventory").GetInt64() != part.Inventory)
                            throw new InvalidDataException($"Search properties for part '{id}' do not match the CSV.");
                        Console.WriteLine($"Search '{query}': verified part {id}: {part.Name}.");
                        count++;
                    }
                }
            }
            Console.WriteLine($"Graph Search '{query}': {count} verified hits. Evidence: {evidence}");
        }
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private sealed record RetrievalCase(string Name, string Query, string? Filter, string[] ExpectedIds);
}
