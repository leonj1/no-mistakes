using System.Globalization;
using System.Text;

namespace NoMistakes.Cli;

/// <summary>A single key/value pair in an ordered TOON object.</summary>
public readonly record struct ToonField(string Key, object? Value);

/// <summary>
/// An ordered TOON object. Field encounter order is preserved so the encoder
/// emits fields deterministically, mirroring Go's toon.Object/toon.NewObject.
/// </summary>
public sealed class ToonObject
{
    public ToonObject(params ToonField[] fields) : this((IEnumerable<ToonField>)fields) { }

    public ToonObject(IEnumerable<ToonField> fields)
    {
        Fields = fields.ToList();
    }

    public IReadOnlyList<ToonField> Fields { get; }

    public bool IsEmpty => Fields.Count == 0;
}

/// <summary>Raised when a value cannot be represented in a TOON document.</summary>
public sealed class ToonEncodingException : Exception
{
    public ToonEncodingException(string message) : base(message) { }
}

/// <summary>
/// Encoder for the subset of Token-Oriented Object Notation (TOON) the axi
/// output layer emits, ported line-for-line from toon-go's encoder with the
/// Core Profile defaults (2-space indent, comma delimiter, no length markers).
/// Supported values: null, bool, string, integer numbers, ToonObject, and
/// sequences that are either all-primitive (inline arrays) or uniform
/// primitive-field objects (tabular arrays). Shapes the axi layer never
/// produces (mixed or nested lists) throw ToonEncodingException.
/// </summary>
public static class Toon
{
    private const int IndentSize = 2;
    private const char Delimiter = ',';

    // Integers beyond IEEE 754 exact range encode as strings, like toon-go.
    private const long MaxSafeInteger = 9007199254740991;

    /// <summary>A pre-formatted numeric literal in the normalized data model.</summary>
    private sealed record NumberLiteral(string Literal);

    public static string MarshalString(object? value)
    {
        var normalized = Normalize(value);
        var lines = new List<string>();
        EncodeRoot(normalized, lines);
        return string.Join("\n", lines);
    }

    // --- normalization (toon-go normalize.go subset) ---

    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return b;
            case string s:
                return s;
            case sbyte or short or int or long:
            {
                var i = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                var literal = i.ToString(CultureInfo.InvariantCulture);
                if (i > MaxSafeInteger || i < -MaxSafeInteger)
                {
                    return literal;
                }
                return new NumberLiteral(literal);
            }
            case byte or ushort or uint or ulong:
            {
                var u = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                var literal = u.ToString(CultureInfo.InvariantCulture);
                if (u > MaxSafeInteger)
                {
                    return literal;
                }
                return new NumberLiteral(literal);
            }
            case ToonObject obj:
                return new ToonObject(obj.Fields.Select(f => new ToonField(f.Key, Normalize(f.Value))));
            case System.Collections.IEnumerable seq:
            {
                var items = new List<object?>();
                foreach (var item in seq)
                {
                    items.Add(Normalize(item));
                }
                return items;
            }
            default:
                throw new ToonEncodingException($"toon: unsupported value of type {value.GetType()}");
        }
    }

    private static bool IsPrimitive(object? value) =>
        value is null or bool or string or NumberLiteral;

    // --- encoding (toon-go encoder.go subset) ---

    private static void EncodeRoot(object? value, List<string> lines)
    {
        switch (value)
        {
            case null or bool or string or NumberLiteral:
                lines.Add(FormatPrimitive(value, inArray: false));
                break;
            case ToonObject obj:
                EncodeObject(obj, 0, lines);
                break;
            case List<object?> list:
                EncodeArray("", list, 0, lines);
                break;
            default:
                throw new ToonEncodingException($"toon: unsupported root value {value.GetType()}");
        }
    }

    private static void EncodeObject(ToonObject obj, int depth, List<string> lines)
    {
        if (depth == 0 && obj.IsEmpty)
        {
            return;
        }
        var indent = Indent(depth);
        foreach (var field in obj.Fields)
        {
            switch (field.Value)
            {
                case null or bool or string or NumberLiteral:
                    lines.Add(indent + EncodeKey(field.Key) + ": " + FormatPrimitive(field.Value, inArray: false));
                    break;
                case ToonObject nested:
                    lines.Add(indent + EncodeKey(field.Key) + ":");
                    EncodeObject(nested, depth + 1, lines);
                    break;
                case List<object?> list:
                    EncodeArray(field.Key, list, depth, lines);
                    break;
                default:
                    throw new ToonEncodingException(
                        $"toon: unsupported object field {field.Key} of type {field.Value.GetType()}");
            }
        }
    }

    private static void EncodeArray(string key, List<object?> values, int depth, List<string> lines)
    {
        var indent = Indent(depth);
        var keyLiteral = key.Length > 0 ? EncodeKey(key) : "";

        if (values.All(IsPrimitive))
        {
            var line = indent + RenderHeader(keyLiteral, values.Count, null);
            if (values.Count > 0)
            {
                line += " " + string.Join(Delimiter, values.Select(v => FormatPrimitive(v, inArray: true)));
            }
            lines.Add(line);
            return;
        }

        if (DetectTabular(values) is { } fields)
        {
            lines.Add(indent + RenderHeader(keyLiteral, values.Count, fields));
            var rowIndent = Indent(depth + 1);
            foreach (var row in values)
            {
                var obj = (ToonObject)row!;
                lines.Add(rowIndent + string.Join(
                    Delimiter,
                    fields.Select(f => FormatPrimitive(FieldValue(obj, f), inArray: true))));
            }
            return;
        }

        throw new ToonEncodingException("toon: unsupported non-tabular array shape");
    }

    /// <summary>
    /// Returns the shared column names when every element is an object with the
    /// same set of primitive-valued fields, otherwise null. Ported from
    /// toon-go's detectTabular.
    /// </summary>
    private static List<string>? DetectTabular(List<object?> values)
    {
        if (values.Count == 0 || values[0] is not ToonObject first || first.IsEmpty)
        {
            return null;
        }
        var fields = new List<string>(first.Fields.Count);
        var fieldSet = new HashSet<string>();
        foreach (var field in first.Fields)
        {
            if (!IsPrimitive(field.Value))
            {
                return null;
            }
            fields.Add(field.Key);
            fieldSet.Add(field.Key);
        }
        foreach (var value in values.Skip(1))
        {
            if (value is not ToonObject obj || obj.Fields.Count != fields.Count)
            {
                return null;
            }
            var seen = new HashSet<string>();
            foreach (var field in obj.Fields)
            {
                if (!fieldSet.Contains(field.Key) || !IsPrimitive(field.Value))
                {
                    return null;
                }
                seen.Add(field.Key);
            }
            if (seen.Count != fields.Count)
            {
                return null;
            }
        }
        return fields;
    }

    private static object? FieldValue(ToonObject obj, string key)
    {
        foreach (var field in obj.Fields)
        {
            if (field.Key == key)
            {
                return field.Value;
            }
        }
        return null;
    }

    private static string RenderHeader(string keyLiteral, int length, List<string>? fields)
    {
        var b = new StringBuilder();
        b.Append(keyLiteral);
        b.Append('[').Append(length.ToString(CultureInfo.InvariantCulture)).Append(']');
        if (fields is { Count: > 0 })
        {
            b.Append('{');
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                {
                    b.Append(Delimiter);
                }
                b.Append(EncodeKey(fields[i]));
            }
            b.Append('}');
        }
        b.Append(':');
        return b.ToString();
    }

    private static string Indent(int depth) =>
        depth <= 0 ? "" : new string(' ', depth * IndentSize);

    // --- primitive formatting and quoting (toon-go format.go) ---

    private static string FormatPrimitive(object? value, bool inArray)
    {
        switch (value)
        {
            case null:
                return "null";
            case bool b:
                return b ? "true" : "false";
            case NumberLiteral n:
                return n.Literal;
            case string s:
                ValidateCharacters(s);
                return NeedsQuoting(s) ? QuoteString(s) : s;
            default:
                throw new ToonEncodingException($"toon: unsupported primitive {value.GetType()}");
        }
    }

    // The document and array delimiters are both comma in the Core Profile
    // defaults the axi layer uses, so a comma forces quoting in every context
    // and the inArray flag does not change the decision.
    private static bool NeedsQuoting(string s)
    {
        if (s.Length == 0 || s.Trim() != s)
        {
            return true;
        }
        if (s is "true" or "false" or "null")
        {
            return true;
        }
        if (LooksNumeric(s) || HasLeadingZeroDecimal(s))
        {
            return true;
        }
        if (s.IndexOfAny([':', '\\', '"', '[', ']', '{', '}', '\n', '\r', '\t']) >= 0)
        {
            return true;
        }
        if (s.StartsWith('-'))
        {
            return true;
        }
        return s.Contains(Delimiter);
    }

    private static string QuoteString(string s)
    {
        var b = new StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (var r in s.EnumerateRunes())
        {
            switch (r.Value)
            {
                case '\\':
                    b.Append("\\\\");
                    break;
                case '"':
                    b.Append("\\\"");
                    break;
                case '\n':
                    b.Append("\\n");
                    break;
                case '\r':
                    b.Append("\\r");
                    break;
                case '\t':
                    b.Append("\\t");
                    break;
                default:
                    b.Append(r.ToString());
                    break;
            }
        }
        b.Append('"');
        return b.ToString();
    }

    private static void ValidateCharacters(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value < 0x20 && r.Value != '\n' && r.Value != '\r' && r.Value != '\t')
            {
                throw new ToonEncodingException(
                    $"toon: unsupported control character U+{r.Value:X4} in string");
            }
        }
    }

    private static bool LooksNumeric(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }
        var i = 0;
        if (s[0] == '-')
        {
            i++;
            if (i == s.Length)
            {
                return false;
            }
        }
        var digits = 0;
        while (i < s.Length && IsDigit(s[i]))
        {
            i++;
            digits++;
        }
        if (digits == 0)
        {
            return false;
        }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            if (i == s.Length || !IsDigit(s[i]))
            {
                return false;
            }
            while (i < s.Length && IsDigit(s[i]))
            {
                i++;
            }
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                i++;
            }
            if (i == s.Length || !IsDigit(s[i]))
            {
                return false;
            }
            while (i < s.Length && IsDigit(s[i]))
            {
                i++;
            }
        }
        return i == s.Length;
    }

    private static bool HasLeadingZeroDecimal(string s) =>
        s.Length >= 2 && s[0] == '0' && s[1] >= '0' && s[1] <= '9';

    private static string EncodeKey(string key)
    {
        if (key.Length == 0 || !IsValidUnquotedKey(key))
        {
            return QuoteString(key);
        }
        return key;
    }

    private static bool IsValidUnquotedKey(string key)
    {
        var first = true;
        foreach (var r in key.EnumerateRunes())
        {
            if (first)
            {
                first = false;
                if (r.Value != '_' && !System.Text.Rune.IsLetter(r))
                {
                    return false;
                }
                continue;
            }
            if (!System.Text.Rune.IsLetter(r) && !System.Text.Rune.IsDigit(r)
                && r.Value != '_' && r.Value != '.')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
