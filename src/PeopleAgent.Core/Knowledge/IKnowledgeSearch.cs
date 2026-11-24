namespace PeopleAgent.Core;

/// <summary>Searches a configured knowledge source for documents relevant to a question.</summary>
public interface IKnowledgeSearch
{
    /// <summary>Human-readable name of this search provider.</summary>
    string ProviderName { get; }
    /// <summary>Instructions that explain how the answer model should use this search tool.</summary>
    string ToolDescription { get; }
    /// <summary>Searches for documents, as the signed-in user when a delegated token is supplied.</summary>
    Task<IReadOnlyList<KnowledgeDocument>> SearchAsync(
        string query, string? userToken, CancellationToken cancellationToken);
}
