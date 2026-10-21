namespace CouchHud.Shell.Services;

/// <summary>
/// The real platform parts of <see cref="CouchHud.Privileged"/> for the shell: the administrator code lives in that library (the
/// service uses it too); here it is put together once, with the console as the place to report to.
/// </summary>
public static class PrivilegedServices
{
    public static IEventSink Events { get; } = ConsoleEventSink.Instance;

    public static IProcessLauncher Processes { get; } = new ProcessLauncher();

    public static ITaskScheduler Scheduler { get; } = new SchtasksScheduler(Processes, Events);

    public static IScm Scm { get; } = new WindowsScm();

    public static ILsaSecrets Lsa { get; } = new LsaSecrets(Events);

    public static ILogonVerifier Logon { get; } = new WindowsLogonVerifier();

    public static IFirewall Firewall { get; } = new WindowsFirewall(Processes, Events);

    public static IWindowsUpdate WindowsUpdate { get; } = new WindowsUpdateAgent();

    public static AutoLogonSetup AutoLogon { get; } = new(Lsa);

    /// <summary>The policy files and tasks next to the program (CouchHud.exe's folder).</summary>
    public static PolicyInstaller Policies { get; } = new(Scheduler, Events, Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);

    public static TaskSetup TaskSetup { get; } = new(Scheduler, Scm, Policies, Events);
}
