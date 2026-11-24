using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PeopleAgent.Core;

/// <summary>Searches one configured Graph connector and converts hits into citation-ready documents.</summary>
public sealed class GraphKnowledgeSearch(
    HttpClient http, GraphOptions options, Telemetry telemetry) : IKnowledgeSearch
{
    /// <summary>Name used to identify this search provider in answer metadata.</summary>
    public string ProviderName => "Microsoft Graph Search (delegated)";
    /// <summary>Guidance that keeps model-generated search requests suitable for keyword Graph search.</summary>
    public string ToolDescription =>
        "Search the configured Graph connector's indexed content as the signed-in user. " +
        "This is keyword/KQL search, not semantic retrieval. Use concise English keywords or a KQL filter, " +
        "not a full natural-language question. Returns citation IDs, indexed properties and search summaries, not full documents. " +
        "Use * only when the user asks to list all items. No live inventory, employee records or write operations.";

    /// <summary>Searches Graph as the signed-in user and validates each returned hit.</summary>
    public async Task<IReadOnlyList<KnowledgeDocument>> SearchAsync(
        string query, string? userToken, CancellationToken cancellationToken)
    {
        using var span = Telemetry.Activities.StartActivity("knowledge_search", System.Diagnostics.ActivityKind.Client);
        span?.SetTag("gen_ai.tool.name", "knowledge_search");
        span?.SetTag("server.address", "graph.microsoft.com");
        span?.SetTag("graph.connection.id", options.ConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 1500)
            throw new ArgumentException("Graph queries must be at most 1,500 characters.", nameof(query));
        if (string.IsNullOrWhiteSpace(userToken))
            throw new AuthenticationRequiredException("A signed-in user is required to search Graph.");
        using var request = CreateRequest(query, userToken);
        using var response = await http.SendAsync(request, cancellationToken);
        var requestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
        {
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
            await telemetry.RecordAsync("graph.search.error", new
            {
                status = (int)response.StatusCode,
                requestId,
                connectionId = options.ConnectionId
            }, cancellationToken);
            throw new HttpRequestException(
                $"Graph Search returned HTTP {(int)response.StatusCode}; request-id: {requestId ?? "not supplied"}.",
                null, response.StatusCode);
        }
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var documents = ReadDocuments(body.RootElement);
        await telemetry.RecordAsync("graph.search.completed", new
        {
            connectionId = options.ConnectionId,
            requestId,
            resultCount = documents.Count,
            provider = ProviderName
        }, cancellationToken);
        span?.SetTag("graph.result.count", documents.Count);
        return documents;
    }

    private HttpRequestMessage CreateRequest(string query, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/search/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            requests = new[]
            {
                new
                {
                    entityTypes = new[] { "externalItem" },
                    contentSources = new[] { $"/external/connections/{options.ConnectionId}" },
                    query = new { queryString = query },
                    fields = options.Fields,
                    from = 0,
                    size = 10
                }
            }
        });
        return request;
    }

    private List<KnowledgeDocument> ReadDocuments(JsonElement root)
    {
        var documents = new List<KnowledgeDocument>();
        foreach (var result in root.GetProperty("value").EnumerateArray())
        {
            if (result.TryGetProperty("error", out _))
                throw new InvalidDataException("Graph Search returned a per-query error.");
            foreach (var container in result.GetProperty("hitsContainers").EnumerateArray())
            {
                if (!container.TryGetProperty("hits", out var hits))
                    continue;
                foreach (var hit in hits.EnumerateArray())
                    documents.Add(ReadDocument(hit, documents.Count + 1));
            }
        }
        return documents;
    }

    private KnowledgeDocument ReadDocument(JsonElement hit, int citationId)
    {
        var source = hit.GetProperty("contentSource").GetString();
        if (source != options.ConnectionId && source != $"/external/connections/{options.ConnectionId}")
            throw new InvalidDataException("Graph Search returned an item outside the configured connection.");
        var properties = hit.GetProperty("resource").GetProperty("properties");
        var title = ReadString(properties, "name") ?? ReadString(properties, "title")
            ?? throw new InvalidDataException("Graph results require a retrievable name or title.");
        var url = ReadString(properties, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) || sourceUri.Scheme != "https")
            throw new InvalidDataException("Graph results require a retrievable HTTPS source URL.");
        var summary = ReadString(hit, "summary") ?? "";
        var plainSummary = WebUtility.HtmlDecode(Regex.Replace(summary, "<[^>]*>", " ",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)));
        var content = "Indexed properties:\n" + properties.GetRawText()
            + "\nSearch summary (may be truncated):\n" + plainSummary;
        var numericFields = ReadNumericFields(properties);
        return new KnowledgeDocument(citationId, title, [], content, sourceUri.AbsoluteUri,
            $"{options.ConnectionId}/{hit.GetProperty("hitId").GetString()}",
            options.ContentIsSynthetic, numericFields, ReadString(properties, "currency"));
    }

    private static Dictionary<string, decimal> ReadNumericFields(JsonElement properties)
    {
        var numericFields = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var number))
                numericFields[property.Name] = number;
            else if (property.Value.ValueKind == JsonValueKind.String
                && decimal.TryParse(property.Value.GetString(),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite
                        | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out number))
                numericFields[property.Name] = number;
        }
        return numericFields;
    }

    /// <summary>Reads a JSON string property, returning null when it is absent or not a string.</summary>
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
