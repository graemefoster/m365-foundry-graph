using System.Text.Json;

namespace PeopleAgent.Core;

/// <summary>One chart value tied to the source document it came from.</summary>
public sealed record ChartPoint(AnswerSource Source, decimal Value);

/// <summary>Validated numeric values that can be presented as a chart.</summary>
public sealed record AnswerChart(string Field, string? Currency, IReadOnlyList<ChartPoint> Points)
{
    /// <summary>Builds a chart only from distinct, retrieved citations with comparable numeric values.</summary>
    public static AnswerChart FromToolCall(JsonElement args, IReadOnlyDictionary<int, KnowledgeDocument> retrieved)
    {
        var field = ReadField(args);
        var ids = ReadCitationIds(args);
        var points = ReadPoints(ids, field, retrieved);
        var currency = ReadCurrency(points);

        return new AnswerChart(field, currency,
            points.OrderByDescending(point => point.Value)
                .ThenBy(point => point.Source.Document.Title, StringComparer.Ordinal).ToArray());
    }

    private static string ReadField(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Any(property => property.Name is not ("field" or "citationIds"))
            || !args.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(fieldElement.GetString()))
            throw new InvalidDataException("present_chart requires a field and 2-10 distinct current-turn citationIds, not values or card JSON.");
        return fieldElement.GetString()!;
    }

    private static JsonElement ReadCitationIds(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || args.EnumerateObject().Any(property => property.Name is not ("field" or "citationIds"))
            || !args.TryGetProperty("citationIds", out var ids)
            || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 2 or > 10)
            throw new InvalidDataException("present_chart requires a field and 2-10 distinct current-turn citationIds, not values or card JSON.");
        return ids;
    }

    private static List<ChartPoint> ReadPoints(JsonElement ids, string field,
        IReadOnlyDictionary<int, KnowledgeDocument> retrieved)
    {
        var points = new List<ChartPoint>();
        var seen = new HashSet<int>();
        foreach (var item in ids.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var id)
                || !seen.Add(id) || !retrieved.TryGetValue(id, out var document)
                || document.NumericFields is null || !document.NumericFields.TryGetValue(field, out var value)
                || string.IsNullOrWhiteSpace(document.Title)
                || !Uri.TryCreate(document.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Every chart point must reference a distinct retrieved record with that numeric field and an HTTPS source URL.");
            points.Add(new ChartPoint(new AnswerSource(id, document), value));
        }
        return points;
    }

    private static string? ReadCurrency(IReadOnlyList<ChartPoint> points)
    {
        var currencies = points.Select(point => point.Source.Document.Currency?.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (currencies.Length != 1 || currencies[0] is { } currency
            && (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')))
            throw new InvalidDataException("Selected records have mixed, partially missing or invalid currency metadata; do not compare them in one chart.");
        return currencies[0];
    }
}
