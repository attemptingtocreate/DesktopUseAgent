using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Execution.References;

public static partial class StepReferenceResolver
{
    [GeneratedRegex(@"^\$steps\.(?<step>[A-Za-z0-9_\-]+)(?<path>(?:\.[A-Za-z0-9_]+|\[\d+\])*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RefRegex();

    public static JsonElement ResolveArgs(
        Dictionary<string, JsonElement>? args,
        IReadOnlyDictionary<string, JsonElement> outputs)
    {
        if (args is null || args.Count == 0)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        // Support targetFrom shorthand → elementId from prior ui.find
        if (args.TryGetValue("targetFrom", out var targetFrom) &&
            targetFrom.ValueKind == JsonValueKind.String)
        {
            var stepId = targetFrom.GetString()!;
            if (!outputs.TryGetValue(stepId, out var stepOutput))
            {
                throw new InvalidOperationException($"{ErrorCodes.ReferenceError}: unknown step '{stepId}'.");
            }

            var elementId = ExtractFirstElementId(stepOutput)
                            ?? throw new InvalidOperationException(
                                $"{ErrorCodes.ReferenceError}: step '{stepId}' has no element id.");
            args = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
            args.Remove("targetFrom");
            args["elementId"] = JsonSerializer.SerializeToElement(elementId);
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return ResolveElement(doc.RootElement.Clone(), outputs);
    }

    public static JsonElement ResolveElement(JsonElement element, IReadOnlyDictionary<string, JsonElement> outputs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = element.GetString() ?? string.Empty;
                if (!text.StartsWith("$steps.", StringComparison.Ordinal))
                {
                    return element;
                }

                return ResolvePath(text, outputs);
            }
            case JsonValueKind.Object:
            {
                var obj = new Dictionary<string, JsonElement>();
                foreach (var prop in element.EnumerateObject())
                {
                    obj[prop.Name] = ResolveElement(prop.Value, outputs);
                }

                return JsonSerializer.SerializeToElement(obj);
            }
            case JsonValueKind.Array:
            {
                var list = new List<JsonElement>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ResolveElement(item, outputs));
                }

                return JsonSerializer.SerializeToElement(list);
            }
            default:
                return element;
        }
    }

    public static JsonElement ResolvePath(string reference, IReadOnlyDictionary<string, JsonElement> outputs)
    {
        var match = RefRegex().Match(reference);
        if (!match.Success)
        {
            throw new InvalidOperationException($"{ErrorCodes.ReferenceError}: invalid reference '{reference}'.");
        }

        var stepId = match.Groups["step"].Value;
        if (!outputs.TryGetValue(stepId, out var current))
        {
            throw new InvalidOperationException($"{ErrorCodes.ReferenceError}: unknown step '{stepId}'.");
        }

        var path = match.Groups["path"].Value;
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                var start = i;
                while (i < path.Length && path[i] is not '.' and not '[')
                {
                    i++;
                }

                var name = path[start..i];
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    throw new InvalidOperationException(
                        $"{ErrorCodes.ReferenceError}: path '{reference}' not found.");
                }
            }
            else if (path[i] == '[')
            {
                i++;
                var start = i;
                while (i < path.Length && path[i] != ']')
                {
                    i++;
                }

                if (i >= path.Length || !int.TryParse(path[start..i], out var index))
                {
                    throw new InvalidOperationException(
                        $"{ErrorCodes.ReferenceError}: invalid index in '{reference}'.");
                }

                i++; // ]
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                {
                    throw new InvalidOperationException(
                        $"{ErrorCodes.ReferenceError}: index out of range in '{reference}'.");
                }

                current = current[index];
            }
            else
            {
                throw new InvalidOperationException($"{ErrorCodes.ReferenceError}: invalid reference '{reference}'.");
            }
        }

        return current.Clone();
    }

    private static string? ExtractFirstElementId(JsonElement stepOutput)
    {
        if (stepOutput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!stepOutput.TryGetProperty("data", out var data))
        {
            return null;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("elements", out var elements) &&
            elements.ValueKind == JsonValueKind.Array &&
            elements.GetArrayLength() > 0)
        {
            var first = elements[0];
            if (first.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("id", out var directId) &&
            directId.ValueKind == JsonValueKind.String)
        {
            return directId.GetString();
        }

        return null;
    }
}
