using System.Text.Json;
using HexTail.Domain;

namespace HexTail.Elastic;

public static class ElasticDocumentMapper
{
    public static Line Map(
        JsonElement source,
        JsonElement fields,
        IReadOnlyList<string> outputFields
    )
    {
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(source, null, flattened);
        FlattenFields(fields, flattened);
        var raw = string.Join(
            ' ',
            outputFields
                .Select(field => flattened.GetValueOrDefault(field))
                .Where(value => !string.IsNullOrWhiteSpace(value))
        );
        return new Line(raw, flattened);
    }

    private static void Flatten(
        JsonElement element,
        string? prefix,
        IDictionary<string, string> result
    )
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (prefix is not null)
                result[prefix] = Compact(element);
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
                Flatten(property.Value, key, result);
            else if (
                property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            )
                result[key] = Compact(property.Value);
        }
    }

    private static void FlattenFields(JsonElement fields, IDictionary<string, string> result)
    {
        if (fields.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in fields.EnumerateObject())
        {
            if (
                property.Value.ValueKind == JsonValueKind.Array
                && property.Value.GetArrayLength() > 0
            )
                result[property.Name] = Compact(property.Value[0]);
            else if (
                property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            )
                result[property.Name] = Compact(property.Value);
        }
    }

    private static string Compact(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
}
