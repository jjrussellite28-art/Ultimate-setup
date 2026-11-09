using System.Globalization;
using System.Text;

namespace MegaHub.Core.Updates;

/// <summary>
/// A small screen for the output of a pseudo console (ConPTY). There winget draws its progress bars, but conhost sends the screen as
/// VT: it moves the cursor, repaints only what changed, writes runs of blanks as "cursor forward" and erases with escape sequences.
/// Stripping the escapes would glue pieces of different rows together, so this keeps a grid of <see cref="Width"/> x
/// <see cref="Height"/> cells, applies the usual sequences (cursor moves, erase, insert/delete, scroll) and after each piece hands out
/// the rows that changed, top to bottom, as plain lines for <see cref="WingetProgressParser"/>. Unknown sequences are skipped.
/// <para>
/// OSC 9;4 (the taskbar progress winget sends to a terminal: state 1 or 2 with a percent) becomes a line "n %", which the parser reads
/// as a bare percent. The text is untrusted: the grid is fixed in size, sequence parameters are capped, numbers range checked;
/// nothing is executed. Single threaded: feed it from one reader.
/// </para>
/// </summary>
public sealed class ConsoleScreen
{
    public const int MaxWidth = 1024;
    public const int MaxHeight = 200;
    /// <summary>Parameters and OSC text longer than this are dropped (the sequence still ends normally).</summary>
    private const int MaxSequence = 256;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, Text, TextEscape }

    private readonly char[][] _rows;
    private readonly bool[] _dirty;
    private readonly StringBuilder _sequence = new();
    // What goes out with the next Feed result, in the order it happened: rows that scrolled away and taskbar percents (with the
    // rows changed before them, so a percent never lands before the package line it belongs to).
    private readonly List<string> _out = [];
    private State _state;
    private int _row, _col;
    private bool _wrapPending;

    public int Width { get; }
    public int Height { get; }

    public ConsoleScreen(int width = 320, int height = 50)
    {
        Width = Math.Clamp(width, 20, MaxWidth);
        Height = Math.Clamp(height, 2, MaxHeight);
        _rows = new char[Height][];
        _dirty = new bool[Height];
        for (var i = 0; i < Height; i++) _rows[i] = Blank();
    }

    /// <summary>Applies the next piece of output; the rows it changed (plus OSC 9;4 percent lines), each as a line without trailing blanks.</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<char> chunk)
    {
        foreach (var c in chunk) Step(c);
        TakeDirty();
        var lines = _out.ToList();
        _out.Clear();
        return lines;
    }

    /// <summary>The changed rows, top to bottom, into the output (capped: a flood of lines must not grow without end).</summary>
    private void TakeDirty()
    {
        for (var i = 0; i < Height; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            var text = Text(_rows[i]);
            if (text.Length > 0 && _out.Count < MaxPending) _out.Add(text);
        }
    }

    private const int MaxPending = 4 * MaxHeight;

    /// <summary>The row as it is now (tests).</summary>
    public string Row(int index) => Text(_rows[Math.Clamp(index, 0, Height - 1)]);

    private char[] Blank()
    {
        var row = new char[Width];
        Array.Fill(row, ' ');
        return row;
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                return;
            case State.Escape:
                Escape(c);
                return;
            case State.EscapeIntermediate:
                // ESC ( B and the like: intermediates, then one final character.
                if (c is < ' ' or > '/') _state = State.Ground;
                return;
            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    Csi(c);
                    _state = State.Ground;
                }
                else if (c is >= ' ' and <= '?')
                {
                    if (_sequence.Length < MaxSequence) _sequence.Append(c);
                }
                else if (c == '\x1B')
                {
                    _state = State.Escape;
                }
                else if (c is '\x18' or '\x1A')
                {
                    _state = State.Ground; // CAN, SUB cancel the sequence
                }
                return;
            case State.Osc:
            case State.Text:
                if (c == '\x07')
                {
                    EndString();
                }
                else if (c == '\x1B')
                {
                    _state = _state == State.Osc ? State.OscEscape : State.TextEscape;
                }
                else if (c is '\x18' or '\x1A')
                {
                    _state = State.Ground;
                }
                else if (_sequence.Length < MaxSequence)
                {
                    _sequence.Append(c);
                }
                return;
            case State.OscEscape:
            case State.TextEscape:
                // ESC \ is the string terminator; anything else after ESC starts a new sequence.
                if (c == '\\')
                {
                    _state = _state == State.OscEscape ? State.Osc : State.Text;
                    EndString();
                }
                else
                {
                    _state = State.Escape;
                    Escape(c);
                }
                return;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1B':
                _state = State.Escape;
                return;
            case '\r':
                _col = 0;
                _wrapPending = false;
                return;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;
            case '\b':
                if (_wrapPending) _wrapPending = false;
                else if (_col > 0) _col--;
                return;
            case '\t':
                _col = Math.Min(Width - 1, (_col / 8 + 1) * 8);
                return;
        }
        if (c < ' ' || c == '\x7F' || c is >= '\x80' and <= '\x9F') return;
        if (_wrapPending)
        {
            _col = 0;
            LineFeed();
        }
        // A wide character (CJK, full-width forms) takes two cells like in the console, the second one holds a filler that is
        // left out of the text; a surrogate pair (most emoji) takes its two cells with its two halves. Neither is split over two rows.
        var wide = IsWide(c);
        if ((wide || char.IsHighSurrogate(c)) && _col == Width - 1)
        {
            Put(_col, ' ');
            _col = 0;
            LineFeed();
        }
        Put(_col, c);
        _dirty[_row] = true;
        if (wide)
        {
            Put(_col + 1, Filler);
            if (_col + 2 >= Width)
            {
                _col = Width - 1;
                _wrapPending = true;
            }
            else
            {
                _col += 2;
            }
            return;
        }
        if (_col == Width - 1) _wrapPending = true;
        else _col++;
    }

    /// <summary>The second cell of a wide character; never comes from the input (control characters are dropped there).</summary>
    private const char Filler = '\0';

    /// <summary>Writes one cell; a wide character that loses one of its two cells to it loses the other one too.</summary>
    private void Put(int col, char c)
    {
        var row = _rows[_row];
        if (row[col] == Filler && col > 0 && IsWide(row[col - 1])) row[col - 1] = ' ';
        if (IsWide(row[col]) && col + 1 < Width && row[col + 1] == Filler) row[col + 1] = ' ';
        row[col] = c;
    }

    /// <summary>East Asian wide and full-width characters of the basic plane (the ranges a console draws two cells wide).</summary>
    internal static bool IsWide(char c) => c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '〾' or >= 'ぁ' and <= '㏿'
        or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= 'ꀀ' and <= '꓏' or >= '가' and <= '힣'
        or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦';

    /// <summary>The text of a row, without the fillers of wide characters.</summary>
    private static string Text(char[] row) => new string(row).Replace("\0", "", StringComparison.Ordinal).TrimEnd();

    private void Escape(char c)
    {
        _sequence.Clear();
        switch (c)
        {
            case '[':
                _state = State.Csi;
                return;
            case ']':
                _state = State.Osc;
                return;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = State.Text;
                return;
            case 'E': // NEL
                _col = 0;
                LineFeed();
                break;
            case 'D': // IND
                LineFeed();
                break;
            case 'M': // RI
                if (_row > 0) _row--;
                else ScrollDown(1);
                _wrapPending = false;
                break;
            case 'c': // RIS
                for (var i = 0; i < Height; i++) Clear(i, 0, Width);
                _row = _col = 0;
                _wrapPending = false;
                break;
            case >= ' ' and <= '/':
                _state = State.EscapeIntermediate;
                return;
        }
        _state = State.Ground;
    }

    private void EndString()
    {
        if (_state == State.Osc) Osc(_sequence.ToString());
        _sequence.Clear();
        _state = State.Ground;
    }

    /// <summary>OSC 9;4;state;percent: 1 normal, 2 error, 4 paused carry a percent; 0 (clear) and 3 (indeterminate) do not.</summary>
    private void Osc(string text)
    {
        var parts = text.Split(';');
        if (parts.Length < 4 || parts[0] != "9" || parts[1] != "4") return;
        if (parts[2] is not ("1" or "2" or "4")) return;
        if (parts[3].Length is 0 or > 3 || !parts[3].All(char.IsAsciiDigit)) return;
        var percent = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (percent > 100) return;
        TakeDirty();
        if (_out.Count < MaxPending) _out.Add($"{percent} %");
    }

    private void Csi(char final)
    {
        var raw = _sequence.ToString();
        _sequence.Clear();
        // Private sequences (cursor visibility, modes: "?25l") and ones with intermediates change nothing on the grid.
        if (raw.Length > 0 && raw[0] is '?' or '<' or '=' or '>') return;
        if (raw.Any(ch => ch is >= ' ' and <= '/')) return;
        var p = Parameters(raw);
        int P(int i, int fallback) => i < p.Count && p[i] > 0 ? p[i] : fallback;
        _wrapPending = false;
        switch (final)
        {
            case 'A': _row = Math.Max(0, _row - P(0, 1)); break;
            case 'B': case 'e': _row = Math.Min(Height - 1, _row + P(0, 1)); break;
            case 'C': case 'a': _col = Math.Min(Width - 1, _col + P(0, 1)); break;
            case 'D': _col = Math.Max(0, _col - P(0, 1)); break;
            case 'E': _row = Math.Min(Height - 1, _row + P(0, 1)); _col = 0; break;
            case 'F': _row = Math.Max(0, _row - P(0, 1)); _col = 0; break;
            case 'G': case '`': _col = Math.Clamp(P(0, 1) - 1, 0, Width - 1); break;
            case 'd': _row = Math.Clamp(P(0, 1) - 1, 0, Height - 1); break;
            case 'H':
            case 'f':
                _row = Math.Clamp(P(0, 1) - 1, 0, Height - 1);
                _col = Math.Clamp(P(1, 1) - 1, 0, Width - 1);
                break;
            case 'K':
                switch (p.Count > 0 ? p[0] : 0)
                {
                    case 0: Clear(_row, _col, Width); break;
                    case 1: Clear(_row, 0, _col + 1); break;
                    case 2: Clear(_row, 0, Width); break;
                }
                break;
            case 'J':
                switch (p.Count > 0 ? p[0] : 0)
                {
                    case 0:
                        Clear(_row, _col, Width);
                        for (var i = _row + 1; i < Height; i++) Clear(i, 0, Width);
                        break;
                    case 1:
                        for (var i = 0; i < _row; i++) Clear(i, 0, Width);
                        Clear(_row, 0, _col + 1);
                        break;
                    case 2:
                    case 3:
                        for (var i = 0; i < Height; i++) Clear(i, 0, Width);
                        break;
                }
                break;
            case 'X': Clear(_row, _col, Math.Min(Width, _col + P(0, 1))); break;
            case '@':
            {
                var n = Math.Min(P(0, 1), Width - _col);
                Array.Copy(_rows[_row], _col, _rows[_row], _col + n, Width - _col - n);
                Clear(_row, _col, _col + n);
                break;
            }
            case 'P':
            {
                var n = Math.Min(P(0, 1), Width - _col);
                Array.Copy(_rows[_row], _col + n, _rows[_row], _col, Width - _col - n);
                Clear(_row, Width - n, Width);
                break;
            }
            case 'L': InsertLines(P(0, 1)); break;
            case 'M': DeleteLines(P(0, 1)); break;
            case 'S': ScrollUp(P(0, 1)); break;
            case 'T': ScrollDown(P(0, 1)); break;
        }
    }

    /// <summary>"5;12" as numbers; an empty or oversized field is 0. At most 16 fields.</summary>
    private static List<int> Parameters(string raw)
    {
        var result = new List<int>();
        if (raw.Length == 0) return result;
        foreach (var field in raw.Split(';', 17))
        {
            if (result.Count == 16) break;
            var digits = field.Split(':')[0];
            result.Add(digits.Length is > 0 and <= 5 && digits.All(char.IsAsciiDigit) ? int.Parse(digits, CultureInfo.InvariantCulture) : 0);
        }
        return result;
    }

    private void Clear(int row, int from, int to)
    {
        from = Math.Clamp(from, 0, Width);
        to = Math.Clamp(to, 0, Width);
        if (to <= from) return;
        Array.Fill(_rows[row], ' ', from, to - from);
        _dirty[row] = true;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_row < Height - 1) _row++;
        else ScrollUp(1);
    }

    private void ScrollUp(int n) => Shift(0, Math.Min(n, Height), up: true);

    private void ScrollDown(int n) => Shift(0, Math.Min(n, Height), up: false);

    private void InsertLines(int n) => Shift(_row, Math.Min(n, Height - _row), up: false);

    private void DeleteLines(int n) => Shift(_row, Math.Min(n, Height - _row), up: true);

    /// <summary>A changed row that leaves the grid is still handed out; the changed rows above it went before it.</summary>
    private void Remember(int row)
    {
        if (!_dirty[row]) return;
        for (var i = 0; i <= row && i < Height; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            var text = Text(_rows[i]);
            if (text.Length > 0 && _out.Count < MaxPending) _out.Add(text);
        }
    }

    /// <summary>Moves rows <paramref name="top"/>..end by <paramref name="n"/> up (blank rows come in at the bottom) or down (at <paramref name="top"/>).</summary>
    private void Shift(int top, int n, bool up)
    {
        if (n <= 0) return;
        for (var k = 0; k < n; k++)
        {
            if (up)
            {
                var gone = _rows[top];
                Remember(top);
                for (var i = top; i < Height - 1; i++)
                {
                    _rows[i] = _rows[i + 1];
                    _dirty[i] = _dirty[i + 1];
                }
                Array.Fill(gone, ' ');
                _rows[Height - 1] = gone;
                _dirty[Height - 1] = false;
            }
            else
            {
                var gone = _rows[Height - 1];
                Remember(Height - 1);
                for (var i = Height - 1; i > top; i--)
                {
                    _rows[i] = _rows[i - 1];
                    _dirty[i] = _dirty[i - 1];
                }
                Array.Fill(gone, ' ');
                _rows[top] = gone;
                _dirty[top] = false;
            }
        }
    }
}
