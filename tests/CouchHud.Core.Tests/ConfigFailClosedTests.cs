using CouchHud.Core.Config;

namespace CouchHud.Core.Tests;

public class ConfigFailClosedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-config-").FullName;
    private string File_ => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);

    private const string LockedConfig = """{"couchLock":"code","passcodeHash":"hash-of-the-parents","integrationMode":"full","parental":{"enabled":true}}""";

    [Fact]
    public void A_broken_file_starts_locked_and_is_not_overwritten_with_defaults()
    {
        File.WriteAllText(File_, "{ \"couchLock\": \"code\", \"passcodeHash\": ");
        var damaged = File.ReadAllText(File_);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        Assert.Equal("windows", store.Current.CouchLock);
        // Changing something while it is broken keeps the file as it is.
        store.Update(c => c.Sounds = false);
        store.Update(c => c.Skin = "ps4");
        Assert.Equal(damaged, File.ReadAllText(File_));
        Assert.False(store.Current.Sounds);
        // A copy of the damaged file is kept next to it, as before.
        Assert.Single(Directory.GetFiles(_dir, "config.json.*.bad"));
    }

    [Fact]
    public void A_repaired_file_is_taken_up_again_with_its_lock_and_rules()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new ConfigStore(_dir, 1, Quick);
        Assert.Equal(ConfigFault.Invalid, store.Fault);

        File.WriteAllText(File_, LockedConfig);
        store.Update(c => c.Sounds = false);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
        Assert.True(store.Current.Parental.Enabled);
        Assert.False(store.Current.Sounds);
        // And now it saves again, with the rules still in it.
        Assert.Contains("hash-of-the-parents", File.ReadAllText(File_));
    }

    [Fact]
    public void A_file_that_was_deleted_while_broken_starts_over_with_defaults()
    {
        File.WriteAllText(File_, "{ broken");
        var store = new ConfigStore(_dir, 1, Quick);

        File.Delete(File_);
        store.Update(c => c.Sounds = false);

        Assert.Null(store.Fault);
        Assert.Equal("off", store.Current.CouchLock);
        Assert.True(File.Exists(File_));
    }

    [Fact]
    public void A_file_that_is_held_by_another_program_is_not_replaced_by_defaults()
    {
        File.WriteAllText(File_, LockedConfig);
        ConfigStore store;
        using (new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new ConfigStore(_dir, 3, Quick);
            Assert.Equal(ConfigFault.Unreadable, store.Fault);
            Assert.Equal("windows", store.Current.CouchLock);
            // Nothing is written while it is unreadable (it could not be anyway, but a swap would have worked).
            store.Update(c => c.Sounds = false);
        }

        Assert.Equal(LockedConfig, File.ReadAllText(File_));
        // Once the other program lets go, the real file comes back.
        store.Update(c => c.Sounds = false);
        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
    }

    [Fact]
    public async Task A_file_that_is_held_for_a_moment_is_read_on_a_later_try()
    {
        File.WriteAllText(File_, LockedConfig);
        var hold = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            hold.Dispose();
        });

        // Generous: a busy CI runner can start the release task late. The test only needs "a later try reads it".
        var store = new ConfigStore(_dir, 400, TimeSpan.FromMilliseconds(25));
        await release;

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
    }

    [Fact]
    public void No_file_is_a_normal_first_start()
    {
        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.Equal("off", store.Current.CouchLock);
        Assert.True(File.Exists(File_));
    }

    [Fact]
    public void Saving_goes_through_a_temp_file_of_its_own_per_process()
    {
        var store = new ConfigStore(_dir, 1, Quick);

        store.Update(c => c.Sounds = false);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.False(File.Exists(File_ + ".tmp"));
        Assert.Contains($".{Environment.ProcessId}.tmp", $"{File_}.{Environment.ProcessId}.tmp");
        Assert.False(new ConfigStore(_dir, 1, Quick).Current.Sounds);
    }

    [Fact]
    public void A_valid_file_loads_unchanged()
    {
        File.WriteAllText(File_, LockedConfig);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("full", store.Current.IntegrationMode);
    }
}
