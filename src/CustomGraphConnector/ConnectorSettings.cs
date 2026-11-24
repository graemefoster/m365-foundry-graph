using System.Text.Json;

namespace CustomGraphConnector;

public sealed record ConnectorSettings
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string CertificatePath { get; init; }
    public required string PrivateKeyPath { get; init; }
    public required string ConnectionId { get; init; }
    public required string ConnectionName { get; init; }
    public required string CsvPath { get; init; }
    public required string SourceUrl { get; init; }
    public string StateDirectory { get; init; } = ".local";
    public string? RetrievalClientId { get; init; }
    public string? RetrievalCasesPath { get; init; }
    public string? ConnectionDescription { get; init; }
    public string ContentCategory { get; init; } = "uncategorized";

    public static ConnectorSettings Load(string path, bool authenticate)
    {
        var settings = JsonSerializer.Deserialize<ConnectorSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Connector settings are empty.");

        if (settings.ConnectionId is not { Length: >= 3 and <= 32 } ||
            !settings.ConnectionId.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("Connection ID must contain 3-32 ASCII letters or digits.");
        if (string.IsNullOrWhiteSpace(settings.ConnectionName))
            throw new InvalidDataException("Connection name is required.");

        if (!Uri.TryCreate(settings.SourceUrl.Replace("{id}", "1", StringComparison.Ordinal), UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(source.Fragment))
            throw new InvalidDataException("Source URL must be an absolute HTTPS URL without a fragment.");

        if (settings.ContentCategory is not ("uncategorized" or "knowledgeBase"))
            throw new InvalidDataException("This connector supports uncategorized or knowledgeBase content.");

        if (authenticate && (!Guid.TryParse(settings.TenantId, out _) || !Guid.TryParse(settings.ClientId, out _)))
            throw new InvalidDataException("Valid tenant and client IDs are required. Run scripts/setup.ps1 first.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return settings with
        {
            CsvPath = Path.GetFullPath(settings.CsvPath, directory),
            RetrievalCasesPath = settings.RetrievalCasesPath is null
                ? null : Path.GetFullPath(settings.RetrievalCasesPath, directory),
            StateDirectory = Path.GetFullPath(settings.StateDirectory, directory),
            CertificatePath = Path.GetFullPath(settings.CertificatePath, directory),
            PrivateKeyPath = Path.GetFullPath(settings.PrivateKeyPath, directory)
        };
    }
}
