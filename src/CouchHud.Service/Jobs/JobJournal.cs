using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CouchHud.Core.Updates;

namespace CouchHud.Service.Jobs;

/// <summary>
/// A job that must outlive this process: a CouchHUD setup stops (and disables) the service that started it, so the instance the setup
/// starts afterwards has to answer GetJob for it. Written before the setup starts, removed when this process sees the setup end.
/// </summary>
/// <param name="Version">The version the setup installs (from the signed checksum file).</param>
/// <param name="StartedAtMs">Wall clock (Unix ms) at the start: only to drop records that are too old to matter.</param>
public sealed record JournaledJob(string Id, string OwnerSid, string Kind, string Version, long StartedAtMs, string? LogFile);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(JournaledJob))]
internal sealed partial class JobJournalJson : JsonSerializerContext
{
}

/// <summary>
/// The records live in <c>{app}\service\data\jobs</c>, below the checked data folder (only SYSTEM and Administrators can write there),
/// one file per job named by its id. Read back strictly: a file that is not exactly a record of ours is deleted and ignored.
/// </summary>
public sealed class JobJournal(string folder, Func<long>? wallClockMs = null)
{
    /// <summary>Records older than this are dropped on load (the user has long seen the outcome some other way).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>The folder below the service's data folder.</summary>
    public const string FolderName = "jobs";

    public const int MaxRecords = 16;
    private const int MaxFileBytes = 4096;

    private static readonly Regex Id = new(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Sid = new(@"^S-1-(5-21|12-1)(-[0-9]{1,10}){1,14}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly Func<long> _now = wallClockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public string Folder { get; } = Path.GetFullPath(folder);

    public long Now => _now();

    public void Write(JournaledJob job)
    {
        if (!Id.IsMatch(job.Id)) throw new ArgumentException("not a job id", nameof(job));
        Directory.CreateDirectory(Folder);
        PathSafety.RequireNoReparsePoints(Folder);
        var path = Path.Combine(Folder, job.Id + ".json");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(job, JobJournalJson.Default.JournaledJob));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        if (!Id.IsMatch(id)) return;
        try
        {
            File.Delete(Path.Combine(Folder, id + ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The records that are still young enough, valid ones only; every file is removed (the caller now holds the jobs in memory).</summary>
    public IReadOnlyList<JournaledJob> TakeAll()
    {
        var found = new List<JournaledJob>();
        if (!Directory.Exists(Folder)) return found;
        try
        {
            PathSafety.RequireNoReparsePoints(Folder);
        }
        catch (Core.Plugins.CatalogException)
        {
            return found;
        }
        foreach (var file in Directory.EnumerateFiles(Folder).Take(256).ToList())
        {
            try
            {
                if (found.Count < MaxRecords && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length <= MaxFileBytes
                    && JsonSerializer.Deserialize(File.ReadAllBytes(file), JobJournalJson.Default.JournaledJob) is { } job
                    && Id.IsMatch(job.Id) && Path.GetFileNameWithoutExtension(file) == job.Id && job.OwnerSid is not null && Sid.IsMatch(job.OwnerSid)
                    && job.Kind is not null && job.Version is not null && Core.Plugins.SemVer.TryParse(job.Version, out _)
                    && _now() - job.StartedAtMs is >= 0 and var age && age <= (long)MaxAge.TotalMilliseconds)
                {
                    found.Add(job);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }
}
