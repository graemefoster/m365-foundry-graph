using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PeopleAgent.Core;

/// <summary>The Agent Framework agent that answers Teams / Microsoft 365 Copilot (Activity protocol) turns.</summary>
public sealed record ConversationAgent(AIAgent Agent);

/// <summary>
/// Defines the People Agent for the Microsoft Agent Framework. The framework owns the model call, the tool-calling
/// loop and conversation history; this class only supplies the instructions and the two tools.
/// </summary>
public static class PeopleAgentFactory
{
    public const string Name = "people-agent";
    public const int MaxKnowledgeSearches = 2;

    private const string ChartInstructions = """

        When present_chart is available, use it for an explicit chart request or a useful comparison of indexed
        numeric values. Only select numericFields from knowledge_search in THIS turn. Never extract chart values
        from summaries or prior answers. Never invent units, currency, quantities, freshness, complete-inventory
        coverage or trends. Select up to 10 relevant records; describe any selection honestly. A chart shows only
        those retrieved records, not necessarily every matching item. Mixed currency metadata cannot be compared.
        For a follow-up chart request, search again. If chart selection fails, correct it or explain the limitation.
        Always write a self-contained cited text answer, even when a chart is queued. Do not claim a chart has
        already been displayed: the host sends it only after the answer completes successfully.
        """;

    /// <summary>Settings for one agent; the Teams agent and the evaluation agent differ only in these.</summary>
    /// <param name="Instructions">System instructions (see <see cref="LoadInstructions"/>).</param>
    /// <param name="Knowledge">Graph connector search for signed-in users, or the bundled sample documents.</param>
    /// <param name="AllowCharts">Offer the present_chart tool (only the Activity front end can render charts).</param>
    /// <param name="MaxOutputTokens">Output-token cap per model call.</param>
    public sealed record Options(string Instructions, IKnowledgeSearch Knowledge, bool AllowCharts,
        int MaxOutputTokens = 1600);

    /// <summary>Reads Data/instructions.graph.txt (Graph connector) or Data/instructions.txt (sample documents).</summary>
    public static string LoadInstructions(string contentRoot, bool graph) =>
        File.ReadAllText(Path.Combine(contentRoot, "Data", graph ? "instructions.graph.txt" : "instructions.txt"));

    /// <summary>
    /// Agent options (instructions, tools) for <c>AIProjectClient.AsAIAgent</c>; set
    /// <see cref="ChatOptions.ModelId"/> to the model deployment.
    /// </summary>
    public static ChatClientAgentOptions CreateOptions(Options options, string? model = null)
    {
        List<AITool> tools = [KnowledgeSearchTool(options.Knowledge)];
        if (options.AllowCharts)
            tools.Add(ChartTool());
        return new ChatClientAgentOptions
        {
            Name = Name,
            Description = "Answers HR policy questions from the organisation's knowledge, with citations.",
            ChatOptions = new ChatOptions
            {
                ModelId = model,
                Instructions = options.Instructions + (options.AllowCharts ? ChartInstructions : ""),
                Tools = tools,
                MaxOutputTokens = options.MaxOutputTokens,
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
            }
        };
    }

    /// <summary>Creates the agent over any chat client (used by tests with a scripted model).</summary>
    public static AIAgent Create(IChatClient model, Options options, ILoggerFactory? loggerFactory = null) =>
        new ChatClientAgent(model, CreateOptions(options), loggerFactory);

    private static AIFunction KnowledgeSearchTool(IKnowledgeSearch knowledge) => AIFunctionFactory.Create(
        ([Description("An English HR policy search query")] string query, CancellationToken cancellationToken) =>
            SearchAsync(knowledge, query, cancellationToken),
        new AIFunctionFactoryOptions { Name = "knowledge_search", Description = knowledge.ToolDescription });

    /// <summary>
    /// Runs one knowledge search and returns the documents numbered for citation. A search failure (for example
    /// Graph sign-in required) ends the run and is rethrown by the caller, rather than being reported to the model.
    /// </summary>
    private static async Task<string> SearchAsync(IKnowledgeSearch knowledge, string query, CancellationToken cancellationToken)
    {
        var scope = AnswerRun.Current;
        if (scope.Searches >= MaxKnowledgeSearches)
            return JsonSerializer.Serialize(new { error = "Search limit reached. Answer from the results already retrieved." });
        scope.Searches++;

        IReadOnlyList<KnowledgeDocument> documents;
        try
        {
            documents = await knowledge.SearchAsync(query, scope.User, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            scope.Failure = exception;
            if (FunctionInvokingChatClient.CurrentContext is { } context)
                context.Terminate = true;
            return JsonSerializer.Serialize(new { error = "Search failed." });
        }

        var citationIds = scope.AddRetrieved(documents);
        return JsonSerializer.Serialize(new
        {
            provider = knowledge.ProviderName,
            documents = documents.Select((doc, index) => new
            {
                citationId = citationIds[index],
                doc.Title,
                doc.Content,
                doc.IsSynthetic,
                numericFields = doc.NumericFields,
                currency = doc.Currency
            })
        });
    }

    private static AIFunction ChartTool() => AIFunctionFactory.Create(
        ([Description("A numericFields key from this turn's search results")] string field,
         [Description("2-10 distinct citationIds from this turn's search results")] int[] citationIds) =>
            QueueChart(field, citationIds),
        new AIFunctionFactoryOptions
        {
            Name = "present_chart",
            Description = "Queue one optional numeric comparison chart for this answer. Use when explicitly requested or when comparing " +
                "several indexed records is clearer visually. Select a numericFields key and 2-10 distinct citationIds " +
                "from this turn's search results. The host resolves all labels, values and links; never supply values or card JSON. " +
                "A successful call queues a card for after the complete answer; it does not send anything yet."
        });

    /// <summary>
    /// Resolves the model's chart selection against retrieved records (the model never supplies values).
    /// An invalid selection is reported back to the model so it can correct it or explain the limitation.
    /// </summary>
    private static string QueueChart(string field, int[] citationIds)
    {
        var scope = AnswerRun.Current;
        try
        {
            if (scope.Chart is not null)
                throw new InvalidDataException("Only one chart may be queued per answer.");
            scope.Chart = AnswerChart.FromToolCall(
                JsonSerializer.SerializeToElement(new { field, citationIds }), scope.Retrieved);
            return JsonSerializer.Serialize(new
            {
                status = "queued",
                field,
                citationIds = scope.Chart.Points.Select(point => point.Source.CitationId),
                message = "The host will send this card only after the final answer succeeds. Write a self-contained cited answer."
            });
        }
        catch (InvalidDataException exception)
        {
            return JsonSerializer.Serialize(new { error = exception.Message });
        }
    }
}
