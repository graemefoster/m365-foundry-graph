using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;

namespace PeopleAgent.ActivityProtocol;

/// <summary>Builds and sends the Adaptive Card that presents selected chart records.</summary>
public static class ActivityChartCard
{
    private const int MaximumCardBytes = 28_000;
    private const int MaximumChartTitleLength = 60;
    private const int TruncatedChartTitleLength = 57;

    /// <summary>Creates a chart card with exact values and links for each selected source.</summary>
    public static object Content(AnswerChart chart)
    {
        var title = $"Indexed {chart.Field} by record - {chart.Points.Count} selected records";
        var rows = BuildRows(chart);

        var currency = chart.Currency is null ? "Currency not specified." : $"Record currency: {chart.Currency}.";
        var synthetic = chart.Points.Any(point => point.Source.Document.IsSynthetic) ? " Includes synthetic sample data." : "";

        var card = CreateCard(chart, title, currency, synthetic, rows);
        if (JsonSerializer.SerializeToUtf8Bytes(card).Length > MaximumCardBytes)
            throw new InvalidDataException("The chart card exceeds the supported message size.");
        return card;
    }

    /// <summary>Assembles the card's summary, chart, fallback text, and exact source rows.</summary>
    private static object CreateCard(AnswerChart chart, string title, string currency, string synthetic, object[] rows) =>
        new
        {
            type = "AdaptiveCard",
            version = "1.5",
            fallbackText = title + ". Source date not provided. " + currency
                + string.Concat(chart.Points.Select(point =>
                    $"\n{point.Source.Document.Title}: {point.Value.ToString(CultureInfo.InvariantCulture)} - {point.Source.Document.Url}")),
            speak = title + ". Source date not provided. " + currency,
            body = new object[]
            {
                new { type = "TextBlock", text = title, weight = "Bolder", wrap = true, style = "heading" },
                new { type = "TextBlock", text = "Selected indexed connector records. Source date not provided; not live data.",
                    isSubtle = true, size = "Small", wrap = true, spacing = "None" },
                new { type = "TextBlock", text = currency + synthetic, isSubtle = true, size = "Small", wrap = true },
                new
                {
                    type = "Chart.HorizontalBar", title = chart.Field,
                    data = chart.Points.Select(point => new
                    {
                        x = point.Source.Document.Title.Length > MaximumChartTitleLength ? point.Source.Document.Title[..TruncatedChartTitleLength] + "..."
                            : point.Source.Document.Title,
                        y = point.Value
                    }).ToArray(),
                    fallback = new { type = "TextBlock", text = "Chart unavailable; exact values and sources are below.",
                        isSubtle = true, wrap = true }
                },
                new { type = "Container", separator = true, items = rows }
            }
        };

    /// <summary>Creates source rows showing a linked title beside its exact numeric value.</summary>
    private static object[] BuildRows(AnswerChart chart) => chart.Points.Select(point =>
    {
        var document = point.Source.Document;
        var url = new Uri(document.Url!).AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
        return new
        {
            type = "ColumnSet",
            spacing = "Small",
            columns = new object[]
            {
                new
                {
                    type = "Column", width = "stretch",
                    items = new[] { new { type = "TextBlock", wrap = true,
                        text = $"[{point.Source.CitationId}] [{Escape(document.Title)}]({url})" } }
                },
                new
                {
                    type = "Column", width = "auto",
                    items = new[] { new { type = "TextBlock", wrap = true,
                        text = point.Value.ToString(CultureInfo.InvariantCulture), horizontalAlignment = "Right" } }
                }
            }
        };
    }).ToArray();

    /// <summary>Ends the text stream before sending a chart attachment when the answer includes one.</summary>
    public static async Task CompleteAsync(TurnResult result,
        Func<CancellationToken, Task<StreamingResponseResult>> finishText,
        Func<IActivity, CancellationToken, Task> sendActivity, CancellationToken cancellationToken)
    {
        var delivery = await finishText(cancellationToken);
        if (delivery != StreamingResponseResult.Success)
            throw new IOException($"Activity stream delivery failed: {delivery}.");

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Error is null && result.Chart is { } chart)
        {
            var activity = MessageFactory.Attachment(new Attachment
            {
                ContentType = "application/vnd.microsoft.card.adaptive",
                Content = Content(chart)
            });
            activity.Text = $"Chart of indexed {chart.Field} for {chart.Points.Count} selected records.";
            await sendActivity(activity, cancellationToken);
        }
    }

    /// <summary>Escapes Markdown punctuation so document titles remain readable links.</summary>
    private static string Escape(string value) =>
        string.Concat(value.Select(character => "\\[]()*_`<>".Contains(character) ? "\\" + character : character.ToString()));
}
