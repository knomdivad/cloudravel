namespace CloudRavel.Tests.Auth;

internal static class PersonaFieldLeakAssertions
{
    public static string Evaluate(string personaName, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "empty";

        var propertyNames = JsonPropertyNameWalker.CollectPropertyNames(json);
        if (propertyNames.Count == 0 && json.TrimStart().StartsWith('{'))
            return "invalid-json";

        var allowed = PersonaPropertyAllowLists.AllowedFor(personaName);
        foreach (var name in propertyNames)
        {
            if (allowed.Contains(name))
                continue;

            if (personaName == "bot-only" && PersonaPropertyAllowLists.IsCloudProperty(name))
                return $"cloud-leak:{name}";

            if (personaName == "cloud-only" && PersonaPropertyAllowLists.IsBotProperty(name))
                return $"bot-leak:{name}";

            return $"unknown-leak:{name}";
        }

        return "ok";
    }

    internal static void AssertNoLeak(string personaName, string routeTemplate, string method, string json)
    {
        var verdict = Evaluate(personaName, json);
        if (verdict is "ok" or "empty")
            return;

        throw new Xunit.Sdk.XunitException(
            $"{personaName} {method} {routeTemplate}: {verdict} in body: {Truncate(json)}");
    }

    private static string Truncate(string json) =>
        json.Length <= 240 ? json : json[..240] + "...";
}
