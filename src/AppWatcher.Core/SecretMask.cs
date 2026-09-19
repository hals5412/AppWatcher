using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AppWatcher.Core;

public static class SecretMask
{
    private static readonly Regex Arguments = new(
        """(?ix)(--?(?:password|passwd|token|api[_-]?key|secret)(?:\s*=\s*|\s+))("[^"]*"|'[^']*'|[^\s]+)""",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static string Text(string text) => Arguments.Replace(text, "$1********");

    public static string Json(string json)
    {
        try { return Visit(JsonNode.Parse(json))?.ToJsonString(JsonDefaults.Options) ?? "null"; }
        catch (System.Text.Json.JsonException) { return Text(json); }
    }

    private static JsonNode? Visit(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray()) obj[key] = Visit(obj[key]?.DeepClone());
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) array[i] = Visit(array[i]?.DeepClone());
        else if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(Text(text));
        return node;
    }

    public static EventRecord Record(EventRecord value) => value with
    {
        ApplicationName = value.ApplicationName is null ? null : Text(value.ApplicationName),
        EventType = Text(value.EventType),
        ReasonCode = Text(value.ReasonCode),
        DetailsJson = Json(value.DetailsJson)
    };
}
