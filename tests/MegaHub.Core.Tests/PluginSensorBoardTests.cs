using MegaHub.Core.Plugins;
using CouchHud.Sdk;

namespace MegaHub.Core.Tests;

/// <summary>
/// The sensor board (0.7): one of Mega Hub's own plugins publishes fan speeds and temperatures, other plugins read them. Only checked numbers
/// pass, the source is named by the host, old values are not handed out, publishing is first-party only and both members are gated.
/// </summary>
public sealed class PluginSensorBoardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "megahub-sensors-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
    private bool _offline;

    public PluginSensorBoardTests() => Directory.CreateDirectory(_dir);

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

    private static PluginSensorReading Fan(string name, double rpm) => new(PluginSensorKind.Fan, name, rpm);

    private static PluginSensorReading Temp(string name, double c) => new(PluginSensorKind.Temperature, name, c);

    [Fact]
    public void Published_values_are_read_back_with_fans_first_and_the_host_naming_the_source()
    {
        var board = new PluginSensorBoard(() => _now);
        Assert.Equal(3, board.Publish("casehud", "CaseHUD", [Temp("CPU Package", 61.234), Fan("CPU Fan", 1180), Fan("Pump", 2400)]));

        var snap = board.Read();
        Assert.NotNull(snap);
        Assert.Equal("CaseHUD", snap.Source);
        Assert.Equal(_now, snap.Taken);
        Assert.Equal(["CPU Fan", "Pump", "CPU Package"], snap.Readings.Select(r => r.Name));
        Assert.Equal(61.2, snap.Readings[2].Value);
    }

    [Theory]
    [InlineData(PluginSensorKind.Fan, -1)]
    [InlineData(PluginSensorKind.Fan, 20001)]
    [InlineData(PluginSensorKind.Fan, double.NaN)]
    [InlineData(PluginSensorKind.Fan, double.PositiveInfinity)]
    [InlineData(PluginSensorKind.Temperature, -41)]
    [InlineData(PluginSensorKind.Temperature, 151)]
    [InlineData(PluginSensorKind.Temperature, double.NaN)]
    [InlineData((PluginSensorKind)7, 50)]
    public void Values_out_of_range_or_of_an_unknown_kind_are_dropped(PluginSensorKind kind, double value)
    {
        Assert.Empty(PluginSensorBoard.Clean([new PluginSensorReading(kind, "X", value)]));
    }

    [Fact]
    public void Names_are_stripped_capped_and_a_reading_without_a_name_is_dropped()
    {
        var clean = PluginSensorBoard.Clean([
            Fan("‮evil\u0007 \U0001F525Fan", 900),
            Fan(new string('a', 500), 900),
            Fan("​\u0001", 900),
            null!
        ]);
        Assert.Equal(2, clean.Count);
        Assert.Equal("evil Fan", clean[0].Name);
        Assert.Equal(PluginSensorReading.MaxName, clean[1].Name.Length);
    }

    [Fact]
    public void At_most_the_cap_is_kept()
    {
        var many = Enumerable.Range(0, 500).Select(i => Temp("T" + i, 40)).ToList();
        Assert.Equal(PluginSensorSnapshot.MaxReadings / 2, PluginSensorBoard.Clean(many).Count);
    }

    [Fact]
    public void Junk_fans_cannot_push_temperatures_out_and_the_other_way_round()
    {
        var junk = Enumerable.Range(0, 100).Select(i => Fan("F" + i, 900)).Append(Temp("CPU", 50)).ToList();
        var clean = PluginSensorBoard.Clean(junk);
        Assert.Contains(clean, r => r.Kind == PluginSensorKind.Temperature && r.Name == "CPU");
        Assert.Equal(PluginSensorSnapshot.MaxReadings / 2 + 1, clean.Count);
        Assert.Equal(PluginSensorKind.Fan, clean[0].Kind);
    }

    [Fact]
    public void The_source_name_is_cleaned_like_other_plugin_text()
    {
        var board = new PluginSensorBoard(() => _now);
        board.Publish("casehud", "Case‮HUD", [Fan("A", 1)]);
        Assert.Equal("CaseHUD", board.Read()!.Source);
    }

    [Fact]
    public void Retain_and_an_activity_check_keep_inactive_publishers_out()
    {
        var board = new PluginSensorBoard(() => _now);
        board.Publish("a", "A", [Fan("A", 1000)]);
        Assert.Null(board.Read(id => id != "a"));
        Assert.NotNull(board.Read(id => id == "a"));
        board.Retain(id => id != "a");
        Assert.Null(board.Read());
    }

    [Fact]
    public void With_several_publishers_the_newest_snapshot_wins_as_a_whole_not_merged()
    {
        // Documented assumption: one first-party publisher (CaseHUD). A second one would replace, not extend, the view.
        var board = new PluginSensorBoard(() => _now);
        board.Publish("a", "A", [Fan("A", 1000)]);
        _now += TimeSpan.FromSeconds(1);
        board.Publish("b", "B", [Temp("B", 30)]);
        var snap = board.Read()!;
        Assert.Equal("B", snap.Source);
        Assert.Single(snap.Readings);
    }

    private string _disabled = "";

    [Fact]
    public void Disabling_uninstalling_pausing_or_unloading_a_publisher_takes_its_values_off()
    {
        // Disabled: the folder is switched off in the host's enable check.
        var m = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, folder => folder != _disabled) { OfflineNow = () => _offline };
        var pub = Ctx(m, "pub", PluginOrigin.FirstParty, PluginCapabilities.SensorsPublish, PluginCapabilities.NetworkInternet);
        var reader = Ctx(m, "reader", PluginOrigin.FirstParty, PluginCapabilities.SensorsRead);

        pub.PublishSensors([Fan("A", 1000)]);
        Assert.NotNull(reader.ReadSensors());
        _disabled = "pub";
        Assert.Null(reader.ReadSensors()); // already at read time, before anyone purges
        m.RaiseTabsChanged();
        Assert.Null(m.SensorBoard.Read());
        _disabled = "";

        // Paused by Offline mode: gone at read time and after the flip.
        pub.PublishSensors([Fan("A", 1000)]);
        Assert.NotNull(reader.ReadSensors());
        _offline = true;
        Assert.Null(reader.ReadSensors());
        m.RaiseTabsChanged();
        _offline = false;
        Assert.Null(m.SensorBoard.Read());

        // Not loaded (unloaded or never granted): a stale entry is purged.
        m.SensorBoard.Publish("ghost", "Ghost", [Fan("G", 5)]);
        Assert.Null(reader.ReadSensors());
        m.PurgeInactiveSensors();
        Assert.Null(m.SensorBoard.Read());
    }

    [Fact]
    public void Old_values_and_values_from_the_future_are_not_handed_out()
    {
        var board = new PluginSensorBoard(() => _now);
        board.Publish("casehud", "CaseHUD", [Fan("A", 1000)]);
        _now += PluginSensorSnapshot.MaxAge + TimeSpan.FromSeconds(1);
        Assert.Null(board.Read());

        board.Publish("casehud", "CaseHUD", [Fan("A", 1000)]);
        _now -= TimeSpan.FromMinutes(5); // the clock jumped back
        Assert.Null(board.Read());
    }

    [Fact]
    public void Publishing_nothing_takes_the_values_off_and_the_newest_publisher_wins()
    {
        var board = new PluginSensorBoard(() => _now);
        board.Publish("a", "A", [Fan("A", 1000)]);
        _now += TimeSpan.FromSeconds(2);
        board.Publish("b", "B", [Fan("B", 2000)]);
        Assert.Equal("B", board.Read()!.Source);
        board.Publish("b", "B", null);
        Assert.Equal("A", board.Read()!.Source);
        Assert.Equal(0, board.Publish("a", "A", [Fan("bad", -5)]));
        Assert.Null(board.Read());
    }

    private PluginManager Manager() => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { OfflineNow = () => _offline };

    private static IPluginContext Ctx(PluginManager m, string id, PluginOrigin origin, params string[] caps) =>
        m.ContextFor(id, new PluginManager.PluginGrant(id, origin, new HashSet<string>(caps)), markLoaded: true);

    [Fact]
    public void Both_members_need_their_capability()
    {
        var ctx = Ctx(Manager(), "nothing", PluginOrigin.FirstParty, PluginCapabilities.Pages);
        Assert.Throws<PluginCapabilityException>(() => ctx.PublishSensors([Fan("A", 1)]));
        Assert.Throws<PluginCapabilityException>(() => ctx.ReadSensors());
        Assert.Equal(PluginCapabilities.SensorsPublish, PluginPolicy.ContextMembers["PublishSensors"]);
        Assert.Equal(PluginCapabilities.SensorsRead, PluginPolicy.ContextMembers["ReadSensors"]);
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void Only_MegaHubs_own_plugins_may_publish_but_anyone_may_read(PluginOrigin origin)
    {
        Assert.Equal("plugin.cap.firstPartyOnly", PluginPolicy.CheckDeclared(origin, [PluginCapabilities.SensorsPublish]));
        Assert.Null(PluginPolicy.CheckDeclared(origin, [PluginCapabilities.SensorsRead]));
        var both = new[] { PluginCapabilities.SensorsPublish, PluginCapabilities.SensorsRead };
        var granted = PluginPolicy.Granted(origin, both, 3, both);
        Assert.DoesNotContain(PluginCapabilities.SensorsPublish, granted);
        Assert.Contains(PluginCapabilities.SensorsRead, granted);
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, both));
    }

    [Fact]
    public void A_reader_sees_what_the_publisher_put_there_through_the_real_contexts()
    {
        var m = Manager();
        var casehud = Ctx(m, "casehud", PluginOrigin.FirstParty, PluginCapabilities.SensorsPublish);
        var fancontrol = Ctx(m, "fancontrol", PluginOrigin.ThirdParty, PluginCapabilities.SensorsRead);
        Assert.Null(fancontrol.ReadSensors());

        casehud.PublishSensors([Fan("Pump", 2400), Temp("Coolant", 31)]);
        var snap = fancontrol.ReadSensors();
        Assert.NotNull(snap);
        Assert.Equal("casehud", snap.Source);
        Assert.Equal(2, snap.Readings.Count);
    }

    [Fact]
    public void A_paused_publisher_takes_its_values_off_and_a_paused_reader_reads_nothing()
    {
        var m = Manager();
        var pub = Ctx(m, "pub", PluginOrigin.FirstParty, PluginCapabilities.SensorsPublish, PluginCapabilities.NetworkInternet);
        var reader = Ctx(m, "reader", PluginOrigin.FirstParty, PluginCapabilities.SensorsRead, PluginCapabilities.NetworkInternet);
        var plain = Ctx(m, "plain", PluginOrigin.FirstParty, PluginCapabilities.SensorsRead);
        pub.PublishSensors([Fan("A", 1000)]);
        Assert.NotNull(plain.ReadSensors());

        _offline = true;
        Assert.Null(reader.ReadSensors());
        pub.PublishSensors([Fan("A", 1100)]);
        Assert.Null(plain.ReadSensors());
    }
}
