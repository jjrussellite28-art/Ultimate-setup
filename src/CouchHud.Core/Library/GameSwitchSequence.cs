using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What a console-style switch does on the machine (the shell's real steps, fakes in the tests).</summary>
public interface IGameSwitchSteps
{
    /// <summary>Every check of the new start, before anything is closed. Throws when the new game would not start.</summary>
    void RequireLaunchable();

    /// <summary>Unfreezes the old game so it can save and close.</summary>
    void Thaw();

    /// <summary>
    /// Asks the old game to close and ends it after the grace time. <paramref name="callOff"/> is for the last look right before anything is
    /// ended by force. Throws <see cref="InvalidOperationException"/> when there is nothing of it left to close.
    /// </summary>
    Task CloseOldAsync(Action<string> callOff, CancellationToken cancel);

    /// <summary>The reason to call the switch off (the new start would be refused now, another game took over), or null. May throw: that counts as a reason.</summary>
    Task<string?> CheckAsync();

    /// <summary>Looks at the machine again: does the tracker still see the old game?</summary>
    Task<bool> OldStillRunningAsync();

    /// <summary>The start of the new game itself (checks included), not the question of the switch.</summary>
    LibraryItem StartNew();
}

/// <summary>The switch was called off. <see cref="OldClosed"/>: the old game was already gone by then.</summary>
public sealed class GameSwitchCalledOffException(string reason, bool oldClosed) : InvalidOperationException(reason)
{
    public bool OldClosed { get; } = oldClosed;
}

/// <summary>The old game was still seen running after the settle time: the new one is not started.</summary>
public sealed class GameSwitchStillRunningException : InvalidOperationException;

/// <summary>
/// The order of a console-style switch: check the new start, close the old game (watched, so it is not ended by force for a start that
/// would be refused), wait until the tracker no longer sees it, start the new one. The final start is <see cref="IGameSwitchSteps.StartNew"/>
/// and never goes through the question of <see cref="GameSwitch.Decide"/> again (the switch itself is what that question lets through).
/// </summary>
public static class GameSwitchSequence
{
    public static async Task<LibraryItem> RunAsync(IGameSwitchSteps steps, TimeSpan watchPoll, TimeSpan settle, TimeSpan settlePoll)
    {
        steps.RequireLaunchable();
        steps.Thaw();
        using var calledOff = new CancellationTokenSource();
        string? reason = null;
        void CallOff(string r)
        {
            if (Interlocked.CompareExchange(ref reason, r, null) != null) return;
            try { calledOff.Cancel(); } catch (ObjectDisposedException) { }
        }
        var watch = WatchAsync(steps, watchPoll, calledOff.Token, CallOff);
        try
        {
            await steps.CloseOldAsync(CallOff, calledOff.Token);
        }
        catch (InvalidOperationException)
        {
            // Nothing of it left to close (ended meanwhile, or never seen as a process): the tracker below decides.
        }
        finally
        {
            try { calledOff.Cancel(); } catch (ObjectDisposedException) { }
            try { await watch; } catch { /* the watcher never replaces what is being thrown */ }
        }
        if (Volatile.Read(ref reason) is { } off) throw new GameSwitchCalledOffException(off, !await steps.OldStillRunningAsync());
        var deadline = DateTimeOffset.UtcNow + settle;
        while (await steps.OldStillRunningAsync())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new GameSwitchStillRunningException();
            await Task.Delay(settlePoll);
        }
        // Between the watcher's stop and the start another game or an outside start may have taken over: look once more, right before.
        string? last;
        try { last = await steps.CheckAsync(); }
        catch (Exception ex) { last = ex.Message; }
        if (last is not null) throw new GameSwitchCalledOffException(last, true);
        try
        {
            return steps.StartNew();
        }
        catch (Exception ex)
        {
            // The old game is gone by now (the one-time permission used up, the lock came back ...): say so with the reason.
            throw new GameSwitchCalledOffException(ex.Message, true);
        }
    }

    private static async Task WatchAsync(IGameSwitchSteps steps, TimeSpan poll, CancellationToken token, Action<string> callOff)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(poll, token); }
            catch (OperationCanceledException) { return; }
            string? why;
            // Whatever goes wrong in the look itself, the old game is not ended for a start nobody could vouch for.
            try { why = await steps.CheckAsync(); }
            catch (Exception ex) { why = ex.Message; }
            if (why is null) continue;
            callOff(why);
            return;
        }
    }
}
