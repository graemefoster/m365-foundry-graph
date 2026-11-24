using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PeopleAgent.Tests;

/// <summary>
/// An offline model for the Agent Framework agent: each call returns the reply chosen by <c>reply</c> from the
/// messages the agent sent, and every request is recorded. The real agent, tool loop and history run on top.
/// </summary>
internal sealed class FakeModel(Func<IReadOnlyList<ChatMessage>, ChatResponse> reply) : IChatClient
{
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];
    public Exception? Failure { get; set; }

    /// <summary>Returns the given replies in order, one per model call.</summary>
    public static FakeModel Script(params ChatResponse[] replies)
    {
        var next = 0;
        return new(_ => next < replies.Length ? replies[next++] : throw new InvalidOperationException("No scripted reply left."));
    }

    /// <summary>Searches once per question, then answers with the given text.</summary>
    public static FakeModel SearchThenAnswer(string answer) => new(messages => messages[^1].Role == ChatRole.Tool
        ? Text(answer)
        : Tool("knowledge_search", """{"query":"policy"}"""));

    public static ChatResponse Tool(string name, string argumentsJson) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-" + Guid.NewGuid().ToString("N"), name,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson))]));

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text) { MessageId = "msg-" + Guid.NewGuid().ToString("N") })
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
        };

    /// <summary>The tool results the agent sent in a request.</summary>
    public static IEnumerable<string> ToolResults(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? "");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sent = messages.ToArray();
        Requests.Add((sent, options));
        if (Failure is not null)
            throw Failure;
        return Task.FromResult(reply(sent));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
