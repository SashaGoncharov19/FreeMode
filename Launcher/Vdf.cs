namespace GTANetwork.Launcher;

/// <summary>Minimal parser for Valve's KeyValues ("VDF") text format used by Steam config files.</summary>
internal static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokenize(text);
        var index = 0;
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (index < tokens.Count)
        {
            var key = tokens[index++];
            if (key == "}" || key == "{") continue;
            if (index >= tokens.Count) break;

            if (tokens[index] == "{")
            {
                index++;
                root[key] = ParseObject(tokens, ref index);
            }
            else
            {
                root[key] = tokens[index++];
            }
        }

        return root;
    }

    private static Dictionary<string, object> ParseObject(List<string> tokens, ref int index)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (index < tokens.Count)
        {
            var key = tokens[index++];
            if (key == "}") return result;
            if (index >= tokens.Count) break;

            if (tokens[index] == "{")
            {
                index++;
                result[key] = ParseObject(tokens, ref index);
            }
            else
            {
                result[key] = tokens[index++];
            }
        }

        return result;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                var start = i;
                var sb = new System.Text.StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => text[i] });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }
                    i++;
                }
                i++; // closing quote
                tokens.Add(sb.ToString());
                continue;
            }

            var wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
            tokens.Add(text.Substring(wordStart, i - wordStart));
        }

        return tokens;
    }

    /// <summary>Walks a path of keys (case-insensitive) and returns the node, or null.</summary>
    public static object? Get(Dictionary<string, object>? node, params string[] path)
    {
        object? current = node;
        foreach (var key in path)
        {
            if (current is not Dictionary<string, object> dict || !dict.TryGetValue(key, out var next)) return null;
            current = next;
        }
        return current;
    }
}
