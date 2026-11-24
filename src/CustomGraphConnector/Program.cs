using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Identity;
using CustomGraphConnector;

try
{
    if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--config") ||
        args[0] is not ("validate" or "provision" or "sync" or "verify" or "run" or "retrieve" or "repair-copilot"))
    {
        Console.Error.WriteLine("Usage: dotnet run -- <validate|provision|sync|verify|run|retrieve|repair-copilot> [--config path]");
        return 2;
    }

    var command = args[0];
    var settings = ConnectorSettings.Load(args.Length == 3 ? args[2] : "connector.settings.json",
        authenticate: command != "validate");
    var parts = AppliancePart.Load(settings.CsvPath);
    Console.WriteLine($"Validated {parts.Count} unique appliance parts.");

    if (command == "validate")
    {
        Console.WriteLine(JsonSerializer.Serialize(parts.Select(part => part.ToExternalItem(settings.SourceUrl)),
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    if (command == "retrieve")
    {
        await RetrievalTest.RunAsync(settings, parts, cancellation.Token);
        return 0;
    }

    using var certificate = X509Certificate2.CreateFromPemFile(settings.CertificatePath, settings.PrivateKeyPath);
    if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        throw new InvalidDataException("The local certificate requires a private key and must not be expired.");
    var credential = new ClientCertificateCredential(settings.TenantId, settings.ClientId, certificate);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    var connector = new GraphConnector(http, credential, settings);

    if (command == "repair-copilot")
        await connector.RepairCopilotSchemaAsync(cancellation.Token);
    if (command is "run" or "provision")
        await connector.ProvisionAsync(cancellation.Token);
    if (command is "run" or "sync" or "repair-copilot")
        await connector.SyncAsync(parts, cancellation.Token);
    if (command is "run" or "verify" or "repair-copilot")
        await connector.VerifyAsync(parts, cancellation.Token);

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.ToString());
    return 1;
}
