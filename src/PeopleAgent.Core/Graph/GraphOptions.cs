namespace PeopleAgent.Core;

/// <summary>Provides validated settings for delegated Microsoft Graph search.</summary>
public sealed class GraphOptions
{
    /// <summary>OAuth scope required to read external connector items.</summary>
    public const string Scope = "https://graph.microsoft.com/ExternalItem.Read.All";
    /// <summary>Whether Graph search is enabled.</summary>
    public bool Enabled { get; }
    /// <summary>Channel OAuth connection name, when channel sign-in is used.</summary>
    public string OAuthConnectionName { get; }
    /// <summary>Whether the configured channel OAuth connection is active.</summary>
    public bool UseChannelOAuth => OAuthConnectionName.Length > 0;
    /// <summary>Microsoft Entra tenant identifier.</summary>
    public string TenantId { get; }
    /// <summary>Application client identifier.</summary>
    public string ClientId { get; }
    /// <summary>External Graph connection searched for knowledge.</summary>
    public string ConnectionId { get; }
    /// <summary>Properties requested from the connector index.</summary>
    public string[] Fields { get; }
    /// <summary>Whether retrieved source content is synthetic demonstration data.</summary>
    public bool ContentIsSynthetic { get; }

    /// <summary>Loads Graph settings and validates them when Graph is enabled.</summary>
    public GraphOptions(IConfiguration configuration)
    {
        Enabled = configuration.GetValue("Graph:Enabled", false);
        OAuthConnectionName = configuration["Graph:OAuthConnectionName"] ?? "";
        TenantId = configuration["Graph:TenantId"] ?? "";
        ClientId = configuration["Graph:ClientId"] ?? "";
        ConnectionId = configuration["Graph:ConnectionId"] ?? "";
        Fields = configuration.GetSection("Graph:Fields").Get<string[]>()
            ?? ["partNumber", "name", "description", "price", "inventory", "appliances", "url"];
        ContentIsSynthetic = configuration.GetValue("Graph:ContentIsSynthetic", false);
        if (Enabled)
            ValidateEnabledSettings();
    }

    /// <summary>Rejects incomplete or conflicting settings before Graph search can be used.</summary>
    private void ValidateEnabledSettings()
    {
        if (!HasValidApplicationIds() || !HasValidConnectionAndFields()
            || !HasValidOAuthConnectionName() || !UseChannelOAuth)
            throw new InvalidOperationException("Graph mode requires tenant/client GUIDs, an alphanumeric connection ID and fields, and a channel OAuth connection (Graph:OAuthConnectionName) for Teams SSO.");
    }

    private bool HasValidApplicationIds() => Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _);

    private bool HasValidConnectionAndFields() => ConnectionId.Length is >= 1 and <= 128
        && ConnectionId.All(char.IsAsciiLetterOrDigit)
        && Fields.Length > 0 && Fields.All(field => !string.IsNullOrWhiteSpace(field));

    private bool HasValidOAuthConnectionName() => OAuthConnectionName.Length <= 100
        && OAuthConnectionName.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
