using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoRim.Bridge
{
    public enum JsonType
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Minimal JSON tree, parser and writer.
    ///
    /// Hand-rolled on purpose: the mod loads into the game's AppDomain alongside every other
    /// mod, and shipping a copy of a common library (Newtonsoft et al.) is a well-known source
    /// of assembly-identity conflicts. Nothing but AutoRim.dll goes into Assemblies/.
    ///
    /// All number formatting and parsing is invariant-culture. RimWorld runs under the user's
    /// locale, and a comma decimal separator would silently produce unparseable output.
    /// </summary>
    public sealed class JsonValue
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly JsonType _type;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<JsonValue> _array;
        private readonly Dictionary<string, JsonValue> _object;

        public JsonType Type => _type;

        public bool IsNull => _type == JsonType.Null;

        private JsonValue(JsonType type)
        {
            _type = type;
            if (type == JsonType.Array) _array = new List<JsonValue>();
            if (type == JsonType.Object) _object = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        }

        private JsonValue(bool value) : this(JsonType.Bool) { _bool = value; }
        private JsonValue(double value) : this(JsonType.Number) { _number = value; }
        private JsonValue(string value) : this(JsonType.String) { _string = value; }

        // ---- construction ----------------------------------------------------------------

        public static readonly JsonValue Null = new JsonValue(JsonType.Null);

        public static JsonValue NewObject() => new JsonValue(JsonType.Object);
        public static JsonValue NewArray() => new JsonValue(JsonType.Array);
        public static JsonValue New(bool value) => new JsonValue(value);
        public static JsonValue New(double value) => new JsonValue(value);
        public static JsonValue New(int value) => new JsonValue((double)value);
        public static JsonValue New(string value) => value == null ? Null : new JsonValue(value);

        public static implicit operator JsonValue(bool value) => New(value);
        public static implicit operator JsonValue(int value) => New(value);
        public static implicit operator JsonValue(float value) => New(value);
        public static implicit operator JsonValue(double value) => New(value);
        public static implicit operator JsonValue(string value) => New(value);

        // ---- object / array access -------------------------------------------------------

        public IEnumerable<KeyValuePair<string, JsonValue>> Fields =>
            _object ?? (IEnumerable<KeyValuePair<string, JsonValue>>)Array.Empty<KeyValuePair<string, JsonValue>>();

        public IReadOnlyList<JsonValue> Items => _array ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

        public int Count => _array?.Count ?? _object?.Count ?? 0;

        /// <summary>Object member access. Reading a missing key yields Null rather than throwing.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_object != null && _object.TryGetValue(key, out var v)) return v;
                return Null;
            }
            set
            {
                if (_object == null) throw new InvalidOperationException("Not a JSON object.");
                _object[key] = value ?? Null;
            }
        }

        public JsonValue this[int index] =>
            _array != null && index >= 0 && index < _array.Count ? _array[index] : Null;

        public bool Has(string key) => _object != null && _object.ContainsKey(key);

        public JsonValue Add(JsonValue value)
        {
            if (_array == null) throw new InvalidOperationException("Not a JSON array.");
            _array.Add(value ?? Null);
            return this;
        }

        /// <summary>Fluent object member set, for building responses inline.</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            this[key] = value;
            return this;
        }

        // ---- typed reads -----------------------------------------------------------------

        public bool AsBool(bool fallback = false) => _type == JsonType.Bool ? _bool : fallback;

        public double AsDouble(double fallback = 0d) => _type == JsonType.Number ? _number : fallback;

        public int AsInt(int fallback = 0) => _type == JsonType.Number ? (int)Math.Round(_number) : fallback;

        public string AsString(string fallback = null)
        {
            switch (_type)
            {
                case JsonType.String: return _string;
                case JsonType.Number: return _number.ToString("R", Inv);
                case JsonType.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        // ---- writing ---------------------------------------------------------------------

        public override string ToString()
        {
            var sb = new StringBuilder(256);
            Write(sb);
            return sb.ToString();
        }

        public void Write(StringBuilder sb)
        {
            switch (_type)
            {
                case JsonType.Null:
                    sb.Append("null");
                    break;
                case JsonType.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonType.Number:
                    if (double.IsNaN(_number) || double.IsInfinity(_number)) sb.Append("null");
                    else if (_number == Math.Floor(_number) && Math.Abs(_number) < 1e15)
                        sb.Append(((long)_number).ToString(Inv));
                    else sb.Append(_number.ToString("R", Inv));
                    break;
                case JsonType.String:
                    WriteString(sb, _string);
                    break;
                case JsonType.Array:
                {
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                }
                case JsonType.Object:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in _object)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        kv.Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
                }
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters must be escaped. U+2028 and U+2029 are legal
                        // JSON but terminate a JavaScript string literal, so escape them too.
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- parsing ---------------------------------------------------------------------

        public static JsonValue Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new JsonParseException("Empty input.");
            int pos = 0;
            var value = ParseValue(text, ref pos, 0);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new JsonParseException($"Trailing content at offset {pos}.");
            return value;
        }

        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (JsonParseException)
            {
                value = Null;
                return false;
            }
        }

        private const int MaxDepth = 64;

        private static JsonValue ParseValue(string s, ref int pos, int depth)
        {
            if (depth > MaxDepth) throw new JsonParseException("Nesting too deep.");
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new JsonParseException("Unexpected end of input.");

            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depth);
                case '[': return ParseArray(s, ref pos, depth);
                case '"': return New(ParseString(s, ref pos));
                case 't': Expect(s, ref pos, "true"); return New(true);
                case 'f': Expect(s, ref pos, "false"); return New(false);
                case 'n': Expect(s, ref pos, "null"); return Null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static JsonValue ParseObject(string s, ref int pos, int depth)
        {
            var result = NewObject();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new JsonParseException($"Expected object key at offset {pos}.");
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new JsonParseException($"Expected ':' at offset {pos}.");
                pos++;
                result[key] = ParseValue(s, ref pos, depth + 1);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonParseException("Unterminated object.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return result; }
                throw new JsonParseException($"Expected ',' or '}}' at offset {pos}.");
            }
        }

        private static JsonValue ParseArray(string s, ref int pos, int depth)
        {
            var result = NewArray();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref pos, depth + 1));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new JsonParseException("Unterminated array.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return result; }
                throw new JsonParseException($"Expected ',' or ']' at offset {pos}.");
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new JsonParseException("Unterminated string.");
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) throw new JsonParseException("Unterminated escape.");
                char e = s[pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) throw new JsonParseException("Truncated \\u escape.");
                        if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, Inv, out int code))
                            throw new JsonParseException("Invalid \\u escape.");
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default: throw new JsonParseException($"Invalid escape '\\{e}'.");
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length)
            {
                char c = s[pos];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') pos++;
                else break;
            }
            string raw = s.Substring(start, pos - start);
            if (!double.TryParse(raw, NumberStyles.Float, Inv, out double value))
                throw new JsonParseException($"Invalid number '{raw}' at offset {start}.");
            return New(value);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
                throw new JsonParseException($"Expected '{literal}' at offset {pos}.");
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }
    }

    public class JsonParseException : Exception
    {
        public JsonParseException(string message) : base(message) { }
    }
}
