using System.Text.Json;

namespace PeopleAgent.Core;

/// <summary>Values submitted by a feedback card.</summary>
public sealed record FeedbackRequest(string? Action, string? Rating, string? AnswerId, string? Comment);

/// <summary>Validates and records feedback for answers in the active conversation.</summary>
public sealed class FeedbackService(Telemetry telemetry, TopicPolicy policy, TimeProvider clock)
{
    /// <summary>Checks the shape and allowed values of a feedback submission.</summary>
    public static bool IsValid(FeedbackRequest request) =>
        request.Action == "feedback.submit"
        && request.Rating is "helpful" or "notHelpful"
        && !string.IsNullOrWhiteSpace(request.AnswerId)
        && (request.Comment?.Length ?? 0) <= 500;

    /// <summary>Records valid feedback once and returns the user-facing result.</summary>
    public async Task<string> SubmitAsync(ConversationSession session, FeedbackRequest request,
        CancellationToken cancellationToken)
    {
        if (session.LastSeenUtc is { } lastSeen && clock.GetUtcNow() - lastSeen >= policy.SessionTimeout)
        {
            session.Reset();
            await telemetry.RecordAsync("feedback.rejected", new { reason = "session-expired" }, cancellationToken);
            return "This conversation has ended or was reset, so your feedback couldn't be saved. Start a new conversation to continue.";
        }
        if (!IsValid(request) || !session.Answers.TryGetValue(request.AnswerId!, out var answer))
        {
            await telemetry.RecordAsync("feedback.rejected", new { reason = "invalid-or-unknown-answer" }, cancellationToken);
            return "Sorry, I couldn't record that feedback.";
        }
        if (answer.Submitted)
        {
            await telemetry.RecordAsync("feedback.duplicate", new { request.AnswerId }, cancellationToken);
            return "You've already given feedback on this answer.";
        }

        // The local sample persists comments only on explicit feedback submission, not conversation text.
        await telemetry.AppendAsync("feedback.jsonl", JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            request.AnswerId,
            request.Rating,
            request.Comment
        }), cancellationToken);
        answer.Submitted = true;
        session.LastSeenUtc = clock.GetUtcNow();
        await telemetry.RecordAsync("feedback.submitted",
            new { request.AnswerId, request.Rating }, cancellationToken);
        return "Thanks for your feedback.";
    }
}
