using System.Text;

namespace CouchHud.Core.Saves;

/// <summary>One path of a manifest entry with the conditions under which it applies.</summary>
public sealed record ManifestPath(string Path, IReadOnlyList<string> Tags, IReadOnlyList<ManifestCondition> When);

/// <summary>One "when" condition; null fields mean "any".</summary>
public sealed record ManifestCondition(string? Os, string? Store);

/// <summary>A game in the Ludusavi manifest, reduced to what a Windows save backup needs.</summary>
public sealed record ManifestEntry(string Name)
{
    public long? SteamId { get; init; }
    public long? GogId { get; init; }
    public IReadOnlyList<long> ExtraSteamIds { get; init; } = [];
    public string? InstallDir { get; init; }
    public string? Alias { get; init; }
    public IReadOnlyList<ManifestPath> Files { get; init; } = [];
    public IReadOnlyList<ManifestPath> Registry { get; init; } = [];
}

/// <summary>
/// Streaming reader for the Ludusavi manifest. The file is several MB of regular block YAML, so instead
/// of a general YAML library each top-level game is cut out line by line and parsed on its own with a
/// small subset parser (maps, lists, scalars, quoted strings, {} and [] flow forms). Memory stays flat.
/// </summary>
public static class LudusaviManifestReader
{
    public static IEnumerable<ManifestEntry> Read(TextReader reader)
    {
        string? name = null;
        var block = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal) || line.TrimStart().StartsWith('#')) continue;
            if (line[0] != ' ')
            {
                if (name is not null) yield return Build(name, block);
                block.Clear();
                name = YamlLite.SplitKey(line.TrimEnd()).Key;
            }
            else if (name is not null)
            {
                block.Add(line);
            }
        }
        if (name is not null) yield return Build(name, block);
    }

    private static ManifestEntry Build(string name, List<string> lines)
    {
        var root = YamlLite.Parse(lines) as Dictionary<string, object?> ?? new();

        long? steam = null;
        if (root.TryGetValue("steam", out var s) && s is Dictionary<string, object?> sd && sd.TryGetValue("id", out var sid))
        {
            steam = ToLong(sid);
        }
        long? gog = null;
        if (root.TryGetValue("gog", out var g) && g is Dictionary<string, object?> gd && gd.TryGetValue("id", out var gid))
        {
            gog = ToLong(gid);
        }
        var extra = new List<long>();
        if (root.TryGetValue("id", out var ids) && ids is Dictionary<string, object?> idd
            && idd.TryGetValue("steamExtra", out var se) && se is List<object?> sel)
        {
            extra.AddRange(sel.Select(ToLong).Where(v => v is not null).Select(v => v!.Value));
        }

        string? installDir = null;
        if (root.TryGetValue("installDir", out var inst) && inst is Dictionary<string, object?> instd)
        {
            installDir = instd.Keys.FirstOrDefault();
        }

        var alias = root.TryGetValue("alias", out var al) ? al as string : null;

        return new ManifestEntry(name)
        {
            SteamId = steam,
            GogId = gog,
            ExtraSteamIds = extra,
            InstallDir = installDir,
            Alias = alias,
            Files = Paths(root, "files"),
            Registry = Paths(root, "registry")
        };
    }

    private static List<ManifestPath> Paths(Dictionary<string, object?> root, string key)
    {
        var result = new List<ManifestPath>();
        if (!root.TryGetValue(key, out var v) || v is not Dictionary<string, object?> map) return result;
        foreach (var (path, details) in map)
        {
            var tags = new List<string>();
            var when = new List<ManifestCondition>();
            if (details is Dictionary<string, object?> d)
            {
                if (d.TryGetValue("tags", out var t) && t is List<object?> tl) tags.AddRange(tl.OfType<string>());
                if (d.TryGetValue("when", out var w) && w is List<object?> wl)
                {
                    foreach (var c in wl.OfType<Dictionary<string, object?>>())
                    {
                        when.Add(new ManifestCondition(
                            c.TryGetValue("os", out var os) ? os as string : null,
                            c.TryGetValue("store", out var st) ? st as string : null));
                    }
                }
            }
            result.Add(new ManifestPath(path, tags, when));
        }
        return result;
    }

    private static long? ToLong(object? v) => v is string s && long.TryParse(s, out var n) ? n : null;
}

/// <summary>Just enough YAML for the manifest. Values come back as string, List&lt;object?&gt; or Dictionary.</summary>
internal static class YamlLite
{
    private sealed record Line(int Indent, string Text);

    public static object? Parse(IReadOnlyList<string> raw)
    {
        var lines = new List<Line>();
        foreach (var l in raw)
        {
            if (string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith('#')) continue;
            var indent = l.Length - l.TrimStart(' ').Length;
            lines.Add(new Line(indent, l.TrimEnd()[indent..]));
        }
        var i = 0;
        return lines.Count == 0 ? null : ParseBlock(lines, ref i, lines[0].Indent);
    }

    private static bool IsItem(string text) => text.StartsWith("- ", StringComparison.Ordinal) || text == "-";

    private static object? ParseBlock(List<Line> lines, ref int i, int indent)
    {
        if (IsItem(lines[i].Text))
        {
            var list = new List<object?>();
            while (i < lines.Count && lines[i].Indent == indent && IsItem(lines[i].Text))
            {
                var after = lines[i].Text.Length > 2 ? lines[i].Text[2..] : "";
                var content = after.TrimStart();
                var contentIndent = indent + 2 + (after.Length - content.Length);
                if (content.Length == 0)
                {
                    i++;
                    list.Add(i < lines.Count && lines[i].Indent > indent ? ParseBlock(lines, ref i, lines[i].Indent) : null);
                }
                else if (!IsFlow(content) && SplitKey(content).Key is not null)
                {
                    // "- key: value" starts a map whose further keys are indented to the first key.
                    lines[i] = new Line(contentIndent, content);
                    list.Add(ParseBlock(lines, ref i, contentIndent));
                }
                else
                {
                    list.Add(Scalar(content));
                    i++;
                }
            }
            return list;
        }

        var map = new Dictionary<string, object?>();
        while (i < lines.Count && lines[i].Indent == indent && !IsItem(lines[i].Text))
        {
            var (key, rest) = SplitKey(lines[i].Text);
            i++;
            if (key is null) continue;
            if (rest.Length > 0)
            {
                map[key] = Scalar(rest);
            }
            else if (i < lines.Count && (lines[i].Indent > indent || (lines[i].Indent == indent && IsItem(lines[i].Text))))
            {
                map[key] = ParseBlock(lines, ref i, lines[i].Indent);
            }
            else
            {
                map[key] = null;
            }
        }
        // Lines we couldn't place: skip so the caller can't loop.
        while (i < lines.Count && lines[i].Indent > indent) i++;
        return map;
    }

    private static bool IsFlow(string s) => s.StartsWith('{') || s.StartsWith('[');

    private static object? Scalar(string s)
    {
        s = s.Trim();
        if (s == "{}") return new Dictionary<string, object?>();
        if (s == "[]") return new List<object?>();
        if (s.StartsWith('[') && s.EndsWith(']'))
        {
            return s[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => (object?)Unquote(x)).ToList();
        }
        if (s is "~" or "null") return null;
        return Unquote(s);
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var k = 1; k < s.Length - 1; k++)
            {
                if (s[k] == '\\' && k + 1 < s.Length - 1)
                {
                    k++;
                    sb.Append(s[k] switch { 'n' => '\n', 't' => '\t', _ => s[k] });
                }
                else sb.Append(s[k]);
            }
            return sb.ToString();
        }
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        return s;
    }

    /// <summary>Splits "key: rest" (key may be quoted). Key is null if the line is no map entry.</summary>
    public static (string? Key, string Value) SplitKey(string text)
    {
        if (text.Length == 0) return (null, "");
        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            for (var k = 1; k < text.Length; k++)
            {
                if (quote == '"' && text[k] == '\\') { k++; continue; }
                if (text[k] != quote) continue;
                if (quote == '\'' && k + 1 < text.Length && text[k + 1] == '\'') { k++; continue; }
                var after = text[(k + 1)..];
                if (after.StartsWith(':') && (after.Length == 1 || after[1] == ' '))
                {
                    return (Unquote(text[..(k + 1)]), after[1..].Trim());
                }
                return (null, "");
            }
            return (null, "");
        }
        // Plain key: ends at the first ": " or a trailing ":".
        var idx = text.IndexOf(": ", StringComparison.Ordinal);
        if (idx < 0 && text.EndsWith(':')) idx = text.Length - 1;
        if (idx <= 0) return (null, "");
        return (text[..idx], text[(idx + 1)..].Trim());
    }
}
