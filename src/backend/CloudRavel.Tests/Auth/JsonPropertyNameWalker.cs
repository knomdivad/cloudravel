using System.Text.Json;

namespace CloudRavel.Tests.Auth;

internal static class JsonPropertyNameWalker
{
    public static IReadOnlyList<string> CollectPropertyNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            WalkElement(doc.RootElement, names);
            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void WalkElement(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    names.Add(prop.Name);
                    WalkElement(prop.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkElement(item, names);
                break;
        }
    }
}
