using CouchHud.Core.Config;

namespace CouchHud.Core.Tests;

/// <summary>The first-run setup assistant keeps one marker in the config, next to the accepted terms.</summary>
public class SetupWizardConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-wizard-").FullName;
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

    [Fact]
    public void A_new_install_has_not_seen_the_assistant()
    {
        var store = new ConfigStore(_dir, 1, TimeSpan.FromMilliseconds(5));

        Assert.Equal(0, store.Current.SetupWizardDone);
        Assert.Equal(0, store.Current.AcceptedTerms);
    }

    [Fact]
    public void An_install_from_before_the_assistant_keeps_its_terms_and_reads_as_not_seen()
    {
        // Existing installs: terms accepted, no marker yet. The UI only offers the assistant then, it does not force it.
        File.WriteAllText(File_, """{"acceptedTerms":1,"skin":"ps4","couchLock":"code","integrationMode":"full"}""");

        var store = new ConfigStore(_dir, 1, TimeSpan.FromMilliseconds(5));

        Assert.Null(store.Fault);
        Assert.Equal(1, store.Current.AcceptedTerms);
        Assert.Equal(0, store.Current.SetupWizardDone);
        Assert.Equal("code", store.Current.CouchLock);
    }

    [Fact]
    public void The_marker_is_saved_and_read_back()
    {
        var store = new ConfigStore(_dir, 1, TimeSpan.FromMilliseconds(5));
        store.UpdateOrThrow(c =>
        {
            c.AcceptedTerms = 1;
            c.SetupWizardDone = 1;
        });

        Assert.Contains("\"setupWizardDone\": 1", File.ReadAllText(File_));
        var again = new ConfigStore(_dir, 1, TimeSpan.FromMilliseconds(5));
        Assert.Equal(1, again.Current.SetupWizardDone);
        Assert.Equal(1, again.Current.AcceptedTerms);
    }

    [Fact]
    public void A_broken_file_does_not_pretend_the_assistant_was_done_or_open_the_couch()
    {
        File.WriteAllText(File_, "{ \"setupWizardDone\": 1, \"couchLock\": ");

        var store = new ConfigStore(_dir, 1, TimeSpan.FromMilliseconds(5));

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        // Fail-closed: locked, so the UI does not start the assistant (it needs the same release as the settings).
        Assert.Equal("windows", store.Current.CouchLock);
    }
}
