using System.Globalization;
using System.Text.Json;

namespace SemanticDesktop.Adapters.RobloxStudio;

public enum RobloxPropertyKind
{
    String,
    Bool,
    Number,
    Color3,
    Vector3,
    UDim2
}

public static class RobloxPropertyContract
{
    public const string Color3Type = "Color3";
    public const string Vector3Type = "Vector3";
    public const string UDim2Type = "UDim2";

    public static readonly IReadOnlyDictionary<string, RobloxPropertyKind> AllowedProperties =
        new Dictionary<string, RobloxPropertyKind>(StringComparer.Ordinal)
        {
            ["Name"] = RobloxPropertyKind.String,
            ["Anchored"] = RobloxPropertyKind.Bool,
            ["CanCollide"] = RobloxPropertyKind.Bool,
            ["CanQuery"] = RobloxPropertyKind.Bool,
            ["CanTouch"] = RobloxPropertyKind.Bool,
            ["Locked"] = RobloxPropertyKind.Bool,
            ["CastShadow"] = RobloxPropertyKind.Bool,
            ["Massless"] = RobloxPropertyKind.Bool,
            ["Transparency"] = RobloxPropertyKind.Number,
            ["Reflectance"] = RobloxPropertyKind.Number,
            ["Visible"] = RobloxPropertyKind.Bool,
            ["Enabled"] = RobloxPropertyKind.Bool,
            ["Value"] = RobloxPropertyKind.Number,
            ["Text"] = RobloxPropertyKind.String,
            ["PlaceholderText"] = RobloxPropertyKind.String,
            ["Font"] = RobloxPropertyKind.String,
            ["TextSize"] = RobloxPropertyKind.Number,
            ["BackgroundTransparency"] = RobloxPropertyKind.Number,
            ["BorderSizePixel"] = RobloxPropertyKind.Number,
            ["ZIndex"] = RobloxPropertyKind.Number,
            ["LayoutOrder"] = RobloxPropertyKind.Number,
            ["Rotation"] = RobloxPropertyKind.Number,
            ["Material"] = RobloxPropertyKind.String,
            ["BrickColor"] = RobloxPropertyKind.String,
            ["Shape"] = RobloxPropertyKind.String,
            ["Color"] = RobloxPropertyKind.Color3,
            ["TextColor3"] = RobloxPropertyKind.Color3,
            ["BackgroundColor3"] = RobloxPropertyKind.Color3,
            // Size/Position accept Vector3 (parts) or UDim2 (GUI) via tagged JSON.
            ["Size"] = RobloxPropertyKind.Vector3,
            ["Position"] = RobloxPropertyKind.Vector3,
            ["Orientation"] = RobloxPropertyKind.Vector3
        };

    public static readonly HashSet<string> BlockedProperties = new(StringComparer.Ordinal)
    {
        "Parent",
        "Source",
        "Archivable"
    };

    public static bool TryValidateSetPropertyValue(
        string property,
        object? rawValue,
        out object? normalizedValue,
        out string? error)
    {
        normalizedValue = null;
        error = null;

        if (BlockedProperties.Contains(property))
        {
            error = $"Property '{property}' is not allowed.";
            return false;
        }

        if (!AllowedProperties.TryGetValue(property, out var kind))
        {
            error = $"Property '{property}' is not in the allowlist.";
            return false;
        }

        // Size/Position may be Vector3 or UDim2 tagged objects.
        if ((property is "Size" or "Position") && rawValue is JsonElement sizeEl && sizeEl.ValueKind == JsonValueKind.Object)
        {
            if (sizeEl.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                string.Equals(typeEl.GetString(), UDim2Type, StringComparison.Ordinal))
            {
                return TryNormalizeUDim2(rawValue, out normalizedValue, out error);
            }
        }

        return kind switch
        {
            RobloxPropertyKind.String => TryNormalizeString(rawValue, out normalizedValue, out error),
            RobloxPropertyKind.Bool => TryNormalizeBool(rawValue, out normalizedValue, out error),
            RobloxPropertyKind.Number => TryNormalizeNumber(rawValue, property, out normalizedValue, out error),
            RobloxPropertyKind.Color3 => TryNormalizeColor3(rawValue, out normalizedValue, out error),
            RobloxPropertyKind.Vector3 => TryNormalizeVector3(rawValue, out normalizedValue, out error),
            RobloxPropertyKind.UDim2 => TryNormalizeUDim2(rawValue, out normalizedValue, out error),
            _ => Fail("Unsupported property kind.", out normalizedValue, out error)
        };
    }

    public static bool IsFiniteNumber(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool TryNormalizeString(object? rawValue, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (rawValue is JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.String)
            {
                error = "String property requires a string value.";
                return false;
            }

            normalized = el.GetString();
            return true;
        }

        normalized = rawValue?.ToString();
        return normalized is not null;
    }

    private static bool TryNormalizeBool(object? rawValue, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (rawValue is bool b)
        {
            normalized = b;
            return true;
        }

        if (rawValue is JsonElement el && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            normalized = el.GetBoolean();
            return true;
        }

        error = "Boolean property requires true/false.";
        return false;
    }

    private static bool TryNormalizeNumber(object? rawValue, string property, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (!TryReadNumber(rawValue, out var number))
        {
            error = "Numeric property requires a finite number.";
            return false;
        }

        if (!IsFiniteNumber(number))
        {
            error = "Numeric property rejects NaN/Infinity.";
            return false;
        }

        if (property is "Transparency" or "Reflectance" or "BackgroundTransparency")
        {
            if (number is < 0 or > 1)
            {
                error = $"{property} must be between 0 and 1.";
                return false;
            }
        }

        normalized = number;
        return true;
    }

    private static bool TryNormalizeColor3(object? rawValue, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (rawValue is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            error = "Color3 property requires { type: 'Color3', r, g, b }.";
            return false;
        }

        if (!TryGetType(el, Color3Type))
        {
            error = "Color3 property requires type 'Color3'.";
            return false;
        }

        if (!TryReadComponent(el, "r", out var r) ||
            !TryReadComponent(el, "g", out var g) ||
            !TryReadComponent(el, "b", out var b))
        {
            error = "Color3 requires finite r/g/b in [0,1].";
            return false;
        }

        normalized = new Dictionary<string, object?>
        {
            ["type"] = Color3Type,
            ["r"] = r,
            ["g"] = g,
            ["b"] = b
        };
        return true;
    }

    private static bool TryNormalizeVector3(object? rawValue, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (rawValue is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            error = "Vector3 property requires { type: 'Vector3', x, y, z }.";
            return false;
        }

        if (!TryGetType(el, Vector3Type))
        {
            error = "Vector3 property requires type 'Vector3'.";
            return false;
        }

        if (!TryReadComponent(el, "x", out var x, allowNegative: true) ||
            !TryReadComponent(el, "y", out var y, allowNegative: true) ||
            !TryReadComponent(el, "z", out var z, allowNegative: true))
        {
            error = "Vector3 requires finite x/y/z.";
            return false;
        }

        normalized = new Dictionary<string, object?>
        {
            ["type"] = Vector3Type,
            ["x"] = x,
            ["y"] = y,
            ["z"] = z
        };
        return true;
    }

    private static bool TryNormalizeUDim2(object? rawValue, out object? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (rawValue is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            error = "UDim2 property requires { type: 'UDim2', xScale, xOffset, yScale, yOffset }.";
            return false;
        }

        if (!TryGetType(el, UDim2Type))
        {
            error = "UDim2 property requires type 'UDim2'.";
            return false;
        }

        if (!TryReadComponent(el, "xScale", out var xScale, allowNegative: true) ||
            !TryReadComponent(el, "xOffset", out var xOffset, allowNegative: true) ||
            !TryReadComponent(el, "yScale", out var yScale, allowNegative: true) ||
            !TryReadComponent(el, "yOffset", out var yOffset, allowNegative: true))
        {
            error = "UDim2 requires finite xScale/xOffset/yScale/yOffset.";
            return false;
        }

        normalized = new Dictionary<string, object?>
        {
            ["type"] = UDim2Type,
            ["xScale"] = xScale,
            ["xOffset"] = xOffset,
            ["yScale"] = yScale,
            ["yOffset"] = yOffset
        };
        return true;
    }

    private static bool TryGetType(JsonElement el, string expected)
    {
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return string.Equals(typeEl.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool TryReadComponent(JsonElement el, string name, out double value, bool allowNegative = false)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var prop) || !TryReadNumber(prop, out value))
        {
            return false;
        }

        if (!IsFiniteNumber(value))
        {
            return false;
        }

        if (!allowNegative && value is < 0 or > 1)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadNumber(object? rawValue, out double number)
    {
        number = 0;
        switch (rawValue)
        {
            case byte b:
                number = b;
                return true;
            case short s:
                number = s;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case float f:
                number = f;
                return true;
            case double d:
                number = d;
                return true;
            case JsonElement el when el.ValueKind == JsonValueKind.Number:
                return el.TryGetDouble(out number);
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number):
                return true;
            default:
                return false;
        }
    }

    private static bool Fail(string message, out object? normalized, out string? error)
    {
        normalized = null;
        error = message;
        return false;
    }
}
