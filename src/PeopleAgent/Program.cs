// People Agent host: a Foundry hosted agent (Azure.AI.AgentServer) serving
//   POST /activity/messages  Teams / Microsoft 365 Copilot (Activity protocol)
//   POST /responses          Foundry evaluations and other Responses clients (sample documents, no user identity)
// Both answer with the same Microsoft Agent Framework agent (PeopleAgentFactory), created with
// AIProjectClient.AsAIAgent as in the Foundry hosted-agent samples.
using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using OpenTelemetry.Metrics;
using PeopleAgent.ActivityProtocol;

var projectEndpoint = new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set."));
var deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");
var project = new AIProjectClient(projectEndpoint, new DefaultAzureCredential());

// Content root is the app folder so Data/ files load regardless of the working directory.
var contentRoot = AppContext.BaseDirectory;
var builder = AgentHost.CreateBuilder([.. args, "--contentRoot", contentRoot]);
var graph = new GraphOptions(builder.Configuration);

AddConversationServices(builder.Services);
AddKnowledgeSearch(builder.Services);

// Teams / Microsoft 365 Copilot: Graph connector search as the signed-in user (or the sample documents) and charts.
builder.Services.AddSingleton(provider => new ConversationAgent(project.AsAIAgent(
    PeopleAgentFactory.CreateOptions(new PeopleAgentFactory.Options(
        PeopleAgentFactory.LoadInstructions(contentRoot, graph.Enabled),
        provider.GetRequiredService<IKnowledgeSearch>(), AllowCharts: true), deployment))));
builder.AddPeopleAgentActivity(graph);

// Foundry evaluations: no signed-in user, so this agent searches the bundled sample documents.
AIAgent evaluationAgent = project.AsAIAgent(PeopleAgentFactory.CreateOptions(new PeopleAgentFactory.Options(
    PeopleAgentFactory.LoadInstructions(contentRoot, graph: false), new KnowledgeBase(contentRoot),
    AllowCharts: false), deployment));
builder.WebApplicationBuilder.AddFoundryResponses(evaluationAgent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

builder.ConfigureTracing(tracing => tracing.AddSource(Telemetry.SourceName));
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(Telemetry.SourceName));

var app = builder.Build();
app.Run();

/// <summary>Topic policy, conversation engine, feedback and telemetry for the Activity (Teams) front end.</summary>
static void AddConversationServices(IServiceCollection services)
{
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<TopicPolicy>();
    services.AddSingleton<Telemetry>();
    services.AddSingleton<FeedbackService>();
    services.AddSingleton<ConversationEngine>();
}

/// <summary>
/// Knowledge search: the Microsoft Graph connector when Graph is enabled, otherwise the bundled sample documents.
/// Graph searches as the signed-in user, whose token comes from Teams SSO (channel OAuth).
/// </summary>
static void AddKnowledgeSearch(IServiceCollection services)
{
    services.AddSingleton<KnowledgeBase>();
    services.AddSingleton<GraphOptions>();
    services.AddHttpClient<GraphKnowledgeSearch>(http => http.Timeout = TimeSpan.FromSeconds(45))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    services.AddSingleton<IKnowledgeSearch>(provider => provider.GetRequiredService<GraphOptions>().Enabled
        ? provider.GetRequiredService<GraphKnowledgeSearch>()
        : provider.GetRequiredService<KnowledgeBase>());
}
