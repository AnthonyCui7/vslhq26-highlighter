using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Dict/JSON plumbing for the port. The Python pipeline passes
/// dict[str, Any] everywhere; the C# port mirrors that with JsonObject (which
/// preserves insertion order) plus these helpers for the handful of Python
/// idioms that need translating: float(x) coercion, {**a, **b} merging, and
/// json.dumps formatting.</summary>
public static class JsonUtil
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>json.dumps(node)</summary>
    public static string Dumps(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    /// <summary>json.dumps(node, indent=2)</summary>
    public static string DumpsIndented(JsonNode? node) => node?.ToJsonString(Indented) ?? "null";

    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    public static JsonObject ParseObject(string text) =>
        JsonNode.Parse(text) as JsonObject
        ?? throw new PipelineError("Expected a JSON object");

    /// <summary>Clone a node so it can be attached to a new parent (JsonNode
    /// instances are single-parent). Python dict references need no such copy,
    /// so call sites use this wherever Python re-uses a value.</summary>
    public static JsonNode? C(JsonNode? node) => node?.DeepClone();

    public static JsonObject CloneObj(JsonObject obj) => (JsonObject)obj.DeepClone();

    /// <summary>{**source, overrides...} — a new object with overrides applied.</summary>
    public static JsonObject With(JsonObject source, params (string Key, JsonNode? Value)[] overrides)
    {
        var result = CloneObj(source);
        foreach (var (key, value) in overrides) result[key] = value;
        return result;
    }

    /// <summary>{**a, **b}</summary>
    public static JsonObject Merged(JsonObject a, JsonObject b)
    {
        var result = CloneObj(a);
        foreach (var pair in b) result[pair.Key] = C(pair.Value);
        return result;
    }

    /// <summary>float(x) — numbers, bools, and numeric strings coerce; anything
    /// else fails (mirrors Python's TypeError/ValueError as a false return).</summary>
    public static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<double>(out var asDouble)) { value = asDouble; return true; }
        if (jsonValue.TryGetValue<long>(out var asLong)) { value = asLong; return true; }
        if (jsonValue.TryGetValue<int>(out var asInt)) { value = asInt; return true; }
        if (jsonValue.TryGetValue<decimal>(out var asDecimal)) { value = (double)asDecimal; return true; }
        if (jsonValue.TryGetValue<bool>(out var asBool)) { value = asBool ? 1 : 0; return true; }
        if (jsonValue.TryGetValue<string>(out var asString)
            && double.TryParse(asString, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    /// <summary>float(x) where the value is known to be numeric.</summary>
    public static double Double(JsonNode? node) =>
        TryDouble(node, out var value)
            ? value
            : throw new PipelineError($"Expected a number, got: {Dumps(node)}");

    /// <summary>float(x or fallback) for absent/unusable values.</summary>
    public static double DoubleOr(JsonNode? node, double fallback) =>
        TryDouble(node, out var value) ? value : fallback;

    public static int Int(JsonNode? node) => (int)Double(node);

    /// <summary>The value as a C# string when it is a JSON string; null otherwise.</summary>
    public static string? StrOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>str(x) — JSON strings verbatim, everything else as its JSON text
    /// (numbers/bools/null render close enough to Python's str() for log lines).</summary>
    public static string Str(JsonNode? node) => StrOrNull(node) ?? Dumps(node);

    /// <summary>Python str() for prompt interpolation: None/True/False spellings
    /// match Python so prompt bytes line up; numbers keep their JSON notation.</summary>
    public static string PyStr(JsonNode? node)
    {
        if (IsNull(node)) return "None";
        if (node is JsonValue value && value.TryGetValue<bool>(out var b)) return b ? "True" : "False";
        return Str(node);
    }

    /// <summary>Python truthiness over JSON values: null/false/0/""/[]/{} are falsy.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value when value.TryGetValue<bool>(out var b) => b,
        JsonValue value when TryDouble(value, out var d) => d != 0,
        JsonValue value when value.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };

    /// <summary>bool(x is not None and x is not falsy JSON null literal).</summary>
    public static bool IsNull(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);

    /// <summary>The items of a JSON array that are objects (Python's
    /// `if isinstance(item, dict)` filters).</summary>
    public static List<JsonObject> Objects(JsonNode? node)
    {
        var result = new List<JsonObject>();
        if (node is not JsonArray array) return result;
        foreach (var item in array)
            if (item is JsonObject obj)
                result.Add(obj);
        return result;
    }

    /// <summary>A JsonArray from nodes, cloning each so parented nodes are safe.</summary>
    public static JsonArray Arr(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(C(item));
        return array;
    }

    public static JsonArray Arr(params JsonNode?[] items) => Arr((IEnumerable<JsonNode?>)items);
}

/// <summary>Python string-formatting equivalents, invariant-culture always.</summary>
public static class Py
{
    /// <summary>f"{value:.Nf}"</summary>
    public static string F(double value, int decimals) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>f"{value:g}" — 6 significant digits, trailing zeros stripped,
    /// lowercase exponent.</summary>
    public static string G(double value)
    {
        var text = value.ToString("G6", CultureInfo.InvariantCulture);
        return text.Contains('E') ? text.Replace("E", "e") : text;
    }

    /// <summary>round(value, digits) — Python rounds half to even.</summary>
    public static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.ToEven);

    /// <summary>str(int)</summary>
    public static string S(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>str(float)/f-string default rendering of a double (shortest
    /// round-trip; Python prints "10.0" for integral floats — a cosmetic
    /// difference in log lines only).</summary>
    public static string S(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Python repr() of a float: integral values render as "N.0",
    /// everything else as the shortest round-trip. Used where a Python float
    /// lands verbatim in prompt text so the prompt bytes match.</summary>
    public static string Repr(double value)
    {
        if (double.IsFinite(value) && Math.Floor(value) == value && Math.Abs(value) < 1e16)
            return ((long)value).ToString(CultureInfo.InvariantCulture) + ".0";
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
