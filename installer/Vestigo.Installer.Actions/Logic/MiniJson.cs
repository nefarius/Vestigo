#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>Ordered JSON object that keeps member order and unknown members across a read/write round trip.</summary>
    public sealed class JsonObject
    {
        private readonly List<KeyValuePair<string, object?>> _members = new List<KeyValuePair<string, object?>>();

        public IReadOnlyList<KeyValuePair<string, object?>> Members => _members;

        public bool TryGet(string name, out object? value)
        {
            foreach (KeyValuePair<string, object?> member in _members)
            {
                if (string.Equals(member.Key, name, StringComparison.Ordinal))
                {
                    value = member.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>Case-insensitive lookup, matching ASP.NET Core configuration key semantics.</summary>
        public bool TryGetIgnoreCase(string name, out object? value)
        {
            foreach (KeyValuePair<string, object?> member in _members)
            {
                if (string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = member.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>Replaces the value of a case-insensitively matching member, keeping its key spelling, or appends it.</summary>
        public void SetIgnoreCase(string name, object? value)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (string.Equals(_members[i].Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    _members[i] = new KeyValuePair<string, object?>(_members[i].Key, value);
                    return;
                }
            }

            _members.Add(new KeyValuePair<string, object?>(name, value));
        }

        public void Set(string name, object? value)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (string.Equals(_members[i].Key, name, StringComparison.Ordinal))
                {
                    _members[i] = new KeyValuePair<string, object?>(name, value);
                    return;
                }
            }

            _members.Add(new KeyValuePair<string, object?>(name, value));
        }
    }

    /// <summary>A JSON number kept as its original text so merging never changes unrelated values.</summary>
    public sealed class JsonNumber
    {
        public JsonNumber(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    /// <summary>
    ///     Minimal JSON reader/writer used for the installer-owned settings keys. Values are string, <see cref="JsonNumber" />,
    ///     bool, null, <see cref="JsonObject" />, or <c>List&lt;object?&gt;</c>. Comments and trailing commas are accepted
    ///     because ASP.NET Core configuration accepts them.
    /// </summary>
    public static class MiniJson
    {
        public static object? Parse(string text)
        {
            Reader reader = new Reader(text);
            reader.SkipTrivia();
            object? value = reader.ReadValue(0);
            reader.SkipTrivia();
            if (!reader.AtEnd)
            {
                throw reader.Error("Unexpected content after the JSON value");
            }

            return value;
        }

        public static string Write(object? value)
        {
            StringBuilder builder = new StringBuilder();
            WriteValue(builder, value, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object? value, int depth)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case string text:
                    WriteString(builder, text);
                    break;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    break;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonNumber raw:
                    builder.Append(raw.Text);
                    break;
                case JsonObject obj:
                    if (obj.Members.Count == 0)
                    {
                        builder.Append("{}");
                        break;
                    }

                    builder.Append("{\n");
                    for (int i = 0; i < obj.Members.Count; i++)
                    {
                        Indent(builder, depth + 1);
                        WriteString(builder, obj.Members[i].Key);
                        builder.Append(": ");
                        WriteValue(builder, obj.Members[i].Value, depth + 1);
                        builder.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                    }

                    Indent(builder, depth);
                    builder.Append('}');
                    break;
                case List<object?> list:
                    if (list.Count == 0)
                    {
                        builder.Append("[]");
                        break;
                    }

                    builder.Append("[\n");
                    for (int i = 0; i < list.Count; i++)
                    {
                        Indent(builder, depth + 1);
                        WriteValue(builder, list[i], depth + 1);
                        builder.Append(i + 1 < list.Count ? ",\n" : "\n");
                    }

                    Indent(builder, depth);
                    builder.Append(']');
                    break;
                default:
                    throw new InvalidOperationException("Unsupported JSON value type " + value.GetType().Name);
            }
        }

        private static void Indent(StringBuilder builder, int depth)
        {
            builder.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private sealed class Reader
        {
            private const int MaxDepth = 64;
            private readonly string _text;
            private int _pos;

            public Reader(string text)
            {
                _text = text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }

            public bool AtEnd => _pos >= _text.Length;

            public FormatException Error(string message)
            {
                return new FormatException(message + " at offset " + _pos.ToString(CultureInfo.InvariantCulture) + ".");
            }

            public void SkipTrivia()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (char.IsWhiteSpace(c))
                    {
                        _pos++;
                    }
                    else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                    {
                        while (_pos < _text.Length && _text[_pos] != '\n')
                        {
                            _pos++;
                        }
                    }
                    else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
                    {
                        int end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            throw Error("Unterminated comment");
                        }

                        _pos = end + 2;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public object? ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("JSON is nested too deeply");
                }

                if (AtEnd)
                {
                    throw Error("Unexpected end of JSON");
                }

                char c = _text[_pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': ExpectWord("true"); return true;
                    case 'f': ExpectWord("false"); return false;
                    case 'n': ExpectWord("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw Error("Unexpected character '" + c + "'");
                }
            }

            private JsonObject ReadObject(int depth)
            {
                JsonObject obj = new JsonObject();
                _pos++;
                while (true)
                {
                    SkipTrivia();
                    if (AtEnd)
                    {
                        throw Error("Unterminated object");
                    }

                    if (_text[_pos] == '}')
                    {
                        _pos++;
                        return obj;
                    }

                    if (_text[_pos] != '"')
                    {
                        throw Error("Expected a property name");
                    }

                    string name = ReadString();
                    SkipTrivia();
                    if (AtEnd || _text[_pos] != ':')
                    {
                        throw Error("Expected ':'");
                    }

                    _pos++;
                    SkipTrivia();
                    obj.Set(name, ReadValue(depth + 1));
                    SkipTrivia();
                    if (!AtEnd && _text[_pos] == ',')
                    {
                        _pos++;
                    }
                    else if (AtEnd || _text[_pos] != '}')
                    {
                        throw Error("Expected ',' or '}'");
                    }
                }
            }

            private List<object?> ReadArray(int depth)
            {
                List<object?> list = new List<object?>();
                _pos++;
                while (true)
                {
                    SkipTrivia();
                    if (AtEnd)
                    {
                        throw Error("Unterminated array");
                    }

                    if (_text[_pos] == ']')
                    {
                        _pos++;
                        return list;
                    }

                    list.Add(ReadValue(depth + 1));
                    SkipTrivia();
                    if (!AtEnd && _text[_pos] == ',')
                    {
                        _pos++;
                    }
                    else if (AtEnd || _text[_pos] != ']')
                    {
                        throw Error("Expected ',' or ']'");
                    }
                }
            }

            private string ReadString()
            {
                StringBuilder builder = new StringBuilder();
                _pos++;
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("Unterminated string");
                    }

                    char c = _text[_pos++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("Unterminated escape");
                    }

                    char escape = _text[_pos++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length ||
                                !int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw Error("Invalid \\u escape");
                            }

                            builder.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw Error("Invalid escape '\\" + escape + "'");
                    }
                }
            }

            private JsonNumber ReadNumber()
            {
                int start = _pos;
                while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0)
                {
                    _pos++;
                }

                string raw = _text.Substring(start, _pos - start);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw Error("Invalid number");
                }

                return new JsonNumber(raw);
            }

            private void ExpectWord(string word)
            {
                if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                {
                    throw Error("Unexpected token");
                }

                _pos += word.Length;
            }
        }
    }
}
