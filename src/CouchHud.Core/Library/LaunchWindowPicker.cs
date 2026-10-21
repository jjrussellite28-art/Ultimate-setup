namespace CouchHud.Core.Library;

/// <summary>A top-level window as the picker sees it. ZOrder 0 is the topmost window.</summary>
public sealed record WindowInfo(long Handle, int Pid, string ExeName, string Title, bool Minimized, int ZOrder, long Area);

public enum WindowMatchKind { None, New, Reused }

public sealed record WindowMatch(WindowInfo? Window, WindowMatchKind Kind);

/// <summary>What to do with the chosen window: whose process may take the foreground, and whether it fills the screen.</summary>
public sealed record ActivationPlan(int AllowPid, bool Maximize);

/// <summary>Pure decisions for URL/protocol starts: which program handles the scheme, which window to bring forward.</summary>
public static class LaunchWindowPicker
{
    /// <summary>The scheme of a launch target ("https", "steam"), or null for paths and plain text.</summary>
    public static string? SchemeOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var colon = target.IndexOf(':');
        // "C:\game.exe" is a path, not a scheme: schemes have at least two characters.
        if (colon < 2) return null;
        var scheme = target[..colon];
        if (!char.IsAsciiLetter(scheme[0])) return null;
        foreach (var c in scheme)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return null;
        return scheme.ToLowerInvariant();
    }

    /// <summary>"C:\...\msedge.exe" to "msedge.exe"; null when nothing usable.</summary>
    public static string? ExeNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// CouchHUD is in front when a web entry starts, so it holds the right to hand the foreground over: right before
    /// the activation the window's own process is allowed to take it. Web entries (http/https) open maximized on the TV
    /// when the option is on; programs and games keep their own window size.
    /// </summary>
    public static ActivationPlan PlanActivation(WindowInfo window, string? scheme, bool maximizeWebWindows) =>
        new(window.Pid, maximizeWebWindows && scheme is "http" or "https");

    /// <summary>
    /// Picks the window to bring forward. A window that was not there before wins (largest first). Otherwise a window of the
    /// handler program: one whose title changed (the new tab became active) or, when allowed, simply the topmost one.
    /// </summary>
    public static WindowMatch Choose(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string? handlerExe, bool allowUnchanged)
    {
        var known = before.Select(w => w.Handle).ToHashSet();
        var fresh = after.Where(w => !known.Contains(w.Handle) && !w.Minimized).OrderByDescending(w => w.Area).FirstOrDefault();
        if (fresh is not null) return new WindowMatch(fresh, WindowMatchKind.New);

        if (handlerExe is null) return new WindowMatch(null, WindowMatchKind.None);
        var mine = after.Where(w => string.Equals(w.ExeName, handlerExe, StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.ZOrder).ToList();

        var oldTitles = before.ToDictionary(w => w.Handle, w => w.Title);
        var changed = mine.FirstOrDefault(w => oldTitles.TryGetValue(w.Handle, out var t) && t != w.Title);
        if (changed is not null) return new WindowMatch(changed, WindowMatchKind.Reused);

        if (allowUnchanged && mine.Count > 0) return new WindowMatch(mine[0], WindowMatchKind.Reused);
        return new WindowMatch(null, WindowMatchKind.None);
    }
}
