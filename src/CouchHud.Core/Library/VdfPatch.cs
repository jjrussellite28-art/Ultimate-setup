using System.Text;

namespace CouchHud.Core.Library;

public enum VdfLookupKind
{
    /// <summary>The key does not exist (all parents exist as blocks up to <see cref="VdfLookup.ExistingDepth"/>).</summary>
    Missing,
    /// <summary>The key is a string value.</summary>
    Value,
    /// <summary>The key is a block, not a value.</summary>
    Block,
    /// <summary>A parent segment exists but is a plain value, so the path cannot exist.</summary>
    NotABlock
}

/// <summary>
/// Result of <see cref="VdfPatch.Lookup"/>. Raw is the value token exactly as written in the file
/// (quotes and escapes included); ExistingDepth is how many leading path segments exist.
/// </summary>
public sealed record VdfLookup(VdfLookupKind Kind, string? Value, string? Raw, int ExistingDepth);

/// <summary>
/// Targeted editor for Valve KeyValues text files (config.vdf, localconfig.vdf).
/// Reads, sets and deletes a single string value under a key path and leaves every
/// other byte (comments, order, whitespace, line endings, escapes) untouched.
/// Keys compare case-insensitively like Steam does; with duplicate keys the first wins.
/// </summary>
public static class VdfPatch
{
    public static string[] SplitPath(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? Get(string text, string path) => Get(text, SplitPath(path));

    public static string Set(string text, string path, string value) => Set(text, SplitPath(path), value);

    public static string Delete(string text, string path, int? pruneFrom = null) =>
        Delete(text, SplitPath(path), pruneFrom);

    public static string? Get(string text, IReadOnlyList<string> path)
    {
        var l = Lookup(text, path);
        return l.Kind == VdfLookupKind.Value ? l.Value : null;
    }

    /// <summary>Tells a missing key apart from a block, a value, or a path blocked by a plain value.</summary>
    public static VdfLookup Lookup(string text, string path) => Lookup(text, SplitPath(path));

    public static VdfLookup Lookup(string text, IReadOnlyList<string> path)
    {
        if (path.Count == 0) throw new ArgumentException("empty path", nameof(path));
        var block = Parse(text);
        for (var i = 0; i < path.Count; i++)
        {
            var entry = block.Find(path[i]);
            if (entry is null) return new VdfLookup(VdfLookupKind.Missing, null, null, i);
            if (i == path.Count - 1)
            {
                return entry.Child is not null
                    ? new VdfLookup(VdfLookupKind.Block, null, null, i)
                    : new VdfLookup(VdfLookupKind.Value, entry.Value, text[entry.ValueStart..entry.ValueEnd], i);
            }
            if (entry.Child is null) return new VdfLookup(VdfLookupKind.NotABlock, null, null, i);
            block = entry.Child;
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Sets the string value, creating the key and any missing parent blocks.</summary>
    public static string Set(string text, IReadOnlyList<string> path, string value) =>
        SetRaw(text, path, Quote(value));

    /// <summary>Like <see cref="Set(string, IReadOnlyList{string}, string)"/> but writes the value token verbatim.</summary>
    public static string SetRaw(string text, string path, string rawToken) =>
        SetRaw(text, SplitPath(path), rawToken);

    public static string SetRaw(string text, IReadOnlyList<string> path, string rawToken)
    {
        if (path.Count == 0) throw new ArgumentException("empty path", nameof(path));
        var root = Parse(text);
        var eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";

        // Walk as deep as existing blocks allow.
        var block = root;
        var depth = 0;
        while (depth < path.Count - 1)
        {
            var entry = block.Find(path[depth]);
            if (entry is null) break;
            if (entry.Child is null)
                throw new InvalidOperationException($"VDF: '{path[depth]}' is a value, not a block");
            block = entry.Child;
            depth++;
        }

        if (depth == path.Count - 1)
        {
            var existing = block.Find(path[depth]);
            if (existing is { Child: not null })
                throw new InvalidOperationException($"VDF: '{path[depth]}' is a block, not a value");
            if (existing is not null)
                return string.Concat(text.AsSpan(0, existing.ValueStart), rawToken, text.AsSpan(existing.ValueEnd));
        }

        var inline = !block.IsRoot && !IsBlank(text, LineStart(text, block.CloseStart), block.CloseStart);
        var sb = new StringBuilder();
        if (inline)
        {
            // Brace shares its line: add the new entries on that line so a later Delete can undo it exactly.
            var p = block.CloseStart;
            if (p > 0 && !char.IsWhiteSpace(text[p - 1]) && text[p - 1] != '{') sb.Append(' ');
            for (var i = depth; i < path.Count - 1; i++) sb.Append(Quote(path[i])).Append(" { ");
            sb.Append(Quote(path[^1])).Append("\t\t").Append(rawToken).Append(' ');
            for (var i = depth; i < path.Count - 1; i++) sb.Append("} ");
            return text.Insert(p, sb.ToString());
        }

        // Build the missing part: nested blocks down to the leaf, indented from the insertion depth.
        var d = block.Depth;
        for (var i = depth; i < path.Count - 1; i++, d++)
        {
            sb.Append(Tabs(d)).Append(Quote(path[i])).Append(eol).Append(Tabs(d)).Append('{').Append(eol);
        }
        sb.Append(Tabs(d)).Append(Quote(path[^1])).Append("\t\t").Append(rawToken).Append(eol);
        for (d--; d >= block.Depth; d--) sb.Append(Tabs(d)).Append('}').Append(eol);

        if (block.IsRoot)
        {
            var prefix = text.Length > 0 && text[^1] != '\n' ? eol : "";
            return text + prefix + sb;
        }
        return text.Insert(LineStart(text, block.CloseStart), sb.ToString());
    }

    /// <summary>
    /// Removes the key (value or whole block); returns the text unchanged if absent. Parent blocks
    /// at path index &gt;= <paramref name="pruneFrom"/> are removed too if this leaves them empty
    /// (to undo a Set that created them); null prunes nothing.
    /// </summary>
    public static string Delete(string text, IReadOnlyList<string> path, int? pruneFrom = null)
    {
        var chain = Chain(Parse(text), path);
        if (chain is null) return text;
        var i = chain.Count - 1;
        while (pruneFrom is { } from && i > 0 && i - 1 >= from && chain[i].Block.Entries.Count == 1) i--;
        var entry = chain[i].Entry;

        var start = entry.KeyStart;
        var end = entry.ValueEnd;
        var ls = LineStart(text, start);
        var j = end;
        while (j < text.Length && text[j] is ' ' or '\t') j++;
        // Take the whole line if nothing else shares it.
        if (IsBlank(text, ls, start) && (j >= text.Length || text[j] is '\r' or '\n'))
        {
            if (j < text.Length && text[j] == '\r') j++;
            if (j < text.Length && text[j] == '\n') j++;
            return string.Concat(text.AsSpan(0, ls), text.AsSpan(j));
        }
        // Inline entry: also drop the gap after it, unless a comment follows.
        var comment = j + 1 < text.Length && text[j] == '/' && text[j + 1] == '/';
        if (j < text.Length && text[j] is not ('\r' or '\n') && !comment) end = j;
        return string.Concat(text.AsSpan(0, start), text.AsSpan(end));
    }

    // ---- helpers ---------------------------------------------------------------------

    private static int LineStart(string text, int pos) => pos == 0 ? 0 : text.LastIndexOf('\n', pos - 1) + 1;

    private static bool IsBlank(string text, int from, int to)
    {
        for (var i = from; i < to; i++) if (text[i] is not (' ' or '\t')) return false;
        return true;
    }

    private static string Tabs(int n) => new('\t', Math.Max(n, 0));

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\t' => "\\t", _ => c.ToString() });
        }
        return sb.Append('"').ToString();
    }

    // ---- parsing with positions ------------------------------------------------------

    private sealed class Entry
    {
        public required string Key { get; init; }
        public int KeyStart { get; init; }
        public int ValueStart { get; set; }
        public int ValueEnd { get; set; }
        public string? Value { get; set; }
        public Block? Child { get; set; }
    }

    private sealed class Block
    {
        public List<Entry> Entries { get; } = [];
        public int CloseStart { get; set; }
        public int Depth { get; init; }
        public bool IsRoot => Depth == 0;

        public Entry? Find(string key) =>
            Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static List<(Block Block, Entry Entry)>? Chain(Block root, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return null;
        var chain = new List<(Block, Entry)>();
        var block = root;
        for (var i = 0; i < path.Count; i++)
        {
            var entry = block.Find(path[i]);
            if (entry is null) return null;
            chain.Add((block, entry));
            if (i < path.Count - 1)
            {
                block = entry.Child;
                if (block is null) return null;
            }
        }
        return chain;
    }

    private enum Kind { String, Open, Close }

    private readonly record struct Token(Kind Kind, int Start, int End, string Text);

    private static Block Parse(string text)
    {
        var pos = 0;
        var root = new Block { Depth = 0, CloseStart = text.Length };
        ReadBlock(text, ref pos, root, expectClose: false);
        return root;
    }

    private static void ReadBlock(string text, ref int pos, Block block, bool expectClose)
    {
        while (true)
        {
            var key = Next(text, ref pos);
            if (key is null)
            {
                if (expectClose) throw new FormatException("VDF: unexpected end of input");
                return;
            }
            if (key.Value.Kind == Kind.Close)
            {
                if (!expectClose) throw new FormatException("VDF: unexpected }");
                block.CloseStart = key.Value.Start;
                return;
            }
            if (key.Value.Kind != Kind.String) throw new FormatException("VDF: expected key");

            var value = Next(text, ref pos) ?? throw new FormatException($"VDF: missing value for {key.Value.Text}");
            var entry = new Entry { Key = key.Value.Text, KeyStart = key.Value.Start, ValueStart = value.Start };
            switch (value.Kind)
            {
                case Kind.Open:
                    var child = new Block { Depth = block.Depth + 1 };
                    ReadBlock(text, ref pos, child, expectClose: true);
                    entry.Child = child;
                    entry.ValueEnd = pos;
                    break;
                case Kind.String:
                    entry.Value = value.Text;
                    entry.ValueEnd = value.End;
                    break;
                default:
                    throw new FormatException($"VDF: unexpected }} after {key.Value.Text}");
            }
            block.Entries.Add(entry);
        }
    }

    private static Token? Next(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) i++;
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else break;
        }
        if (i >= text.Length) return null;

        var start = i;
        var c = text[i];
        if (c == '{') { i++; return new Token(Kind.Open, start, i, "{"); }
        if (c == '}') { i++; return new Token(Kind.Close, start, i, "}"); }

        var sb = new StringBuilder();
        if (c == '"')
        {
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    var n = text[i + 1];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                    i += 2;
                }
                else sb.Append(text[i++]);
            }
            if (i >= text.Length) throw new FormatException("VDF: unterminated string");
            i++; // closing quote
            return new Token(Kind.String, start, i, sb.ToString());
        }

        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) sb.Append(text[i++]);
        return new Token(Kind.String, start, i, sb.ToString());
    }
}
