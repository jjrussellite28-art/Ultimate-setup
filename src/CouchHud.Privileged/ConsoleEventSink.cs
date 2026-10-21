namespace CouchHud.Privileged;

/// <summary>Reports to the console: what the shell's helpers did so far (stdout for what happened, stderr for what did not).</summary>
public sealed class ConsoleEventSink : IEventSink
{
    public static readonly ConsoleEventSink Instance = new();

    public void Info(string message) => Console.WriteLine(message);

    public void Error(string message) => Console.Error.WriteLine(message);
}
