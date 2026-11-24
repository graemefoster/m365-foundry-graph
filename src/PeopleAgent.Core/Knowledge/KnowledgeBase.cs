using System.Text.Json;
using System.Text.RegularExpressions;

namespace PeopleAgent.Core;

/// <summary>Loads and searches the bundled synthetic HR policy corpus.</summary>
public sealed class KnowledgeBase : IKnowledgeSearch
{
    /// <summary>Human-readable name of this search provider.</summary>
    public string ProviderName => "Synthetic HR sample";
    /// <summary>Instructions for using the local synthetic knowledge search.</summary>
    public string ToolDescription => "Search the synthetic Contoso HR documents. Use an English search query. Returns citation IDs, titles and policy passages. This is not a real employee system.";
    /// <summary>Validated documents loaded from the bundled corpus.</summary>
    public IReadOnlyList<KnowledgeDocument> Documents { get; }

    /// <summary>Loads the knowledge corpus and ensures it is non-empty with unique IDs.</summary>
    public KnowledgeBase(IHostEnvironment environment) : this(environment.ContentRootPath) { }

    /// <summary>Loads Data/knowledge.json under the given content root.</summary>
    public KnowledgeBase(string contentRoot)
    {
        Documents = JsonSerializer.Deserialize<List<KnowledgeDocument>>(
            File.ReadAllText(Path.Combine(contentRoot, "Data", "knowledge.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The synthetic knowledge corpus is missing.");

        if (Documents.Count == 0 || Documents.Select(d => d.Id).Distinct().Count() != Documents.Count)
            throw new InvalidDataException("Knowledge documents must have unique IDs and cannot be empty.");
    }

    /// <summary>Returns up to three documents ranked by keyword overlap.</summary>
    public IReadOnlyList<KnowledgeDocument> Search(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var words = Regex.Matches(TopicPolicy.Normalize(query), @"[\p{L}\p{N}]+",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Value).Where(w => w.Length > 2).ToHashSet(StringComparer.Ordinal);
        return Documents.Select(doc => (Doc: doc, Score: doc.Keywords.Count(words.Contains)))
            .Where(item => item.Score > 0).OrderByDescending(item => item.Score)
            .ThenBy(item => item.Doc.Id).Take(3).Select(item => item.Doc).ToArray();
    }

    /// <summary>Adapts local synchronous search to the shared asynchronous search contract.</summary>
    public Task<IReadOnlyList<KnowledgeDocument>> SearchAsync(
        string query, string? userToken, CancellationToken cancellationToken) => Task.FromResult(Search(query));
}
