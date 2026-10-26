using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>What the catalog sources delivered; <see cref="FromCache"/> when at least one source was offline. ErrorCode (notFound, unreachable) is set when every error is of that kind, so the UI can translate it.</summary>
public sealed record CatalogResult(IReadOnlyList<CatalogEntry> Entries, bool FromCache, long? FetchedAt, string? Error, string? ErrorCode = null);

/// <summary>What an install did; plugins always need a restart because loaded DLLs cannot be replaced or unloaded.</summary>
public sealed record InstallOutcome(string Message, bool RestartNeeded);

/// <summary>
/// Loads catalog indexes (with timeout, size limit and an offline cache), downloads packages with a
/// mandatory SHA-256 check and installs them through <see cref="PackageInstaller"/>.
/// Trust: the default source must carry a valid signature (index.json.sig, see <see cref="CatalogSignature"/>);
/// sources the user added may be unsigned and are marked as such. Only https is accepted; local sources exist
/// for development behind <paramref name="allowLocal"/>.
/// </summary>
public sealed class CatalogService(HttpClient http, string dataDir, Func<IReadOnlyList<string>> sources, Func<bool> allowLocal, string language = "en", string? publicKeyPem = null, Func<string>? languageNow = null)
{
    private string Language => languageNow?.Invoke() ?? language;

    public static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(10);
    /// <summary>A download may take as long as it likes, as long as data keeps coming.</summary>
    public static TimeSpan DownloadInactivity { get; internal set; } = TimeSpan.FromSeconds(60);
    public const long MaxIndexBytes = 1024 * 1024;
    public const long MaxSignatureBytes = 4096;
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    private readonly object _cacheLock = new();

    private string CacheFile => Path.Combine(dataDir, "catalog-cache.json");

    private sealed record CacheEntry(long FetchedAt, string Data, string? Signature);

    public static bool IsDefaultSource(string source) => source.Equals(CatalogDefaults.IndexUrl, StringComparison.OrdinalIgnoreCase);

    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        var entries = new List<CatalogEntry>();
        var errors = new List<string>();
        var codes = new List<string?>();
        var fromCache = false;
        long? fetched = null;
        var local = allowLocal();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CatalogParser.IsAllowedUrl(source, local))
            {
                errors.Add(Strings.Get("catalog.sourceHttps", source));
                codes.Add(null);
                continue;
            }
            IReadOnlyList<CatalogEntry> parsed;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IndexTimeout);
                var data = await ReadBytesAsync(source, MaxIndexBytes, timeout.Token);
                string? signature = null;
                try
                {
                    signature = System.Text.Encoding.ASCII.GetString(await ReadBytesAsync(CatalogSignature.SigUrl(source), MaxSignatureBytes, timeout.Token));
                }
                catch (Exception ex) when (ex is HttpRequestException or CatalogException or IOException)
                {
                    // No signature file: fine for user sources, fatal for the default one (checked below).
                }
                parsed = Accept(data, signature, source, local);
                WriteCache(source, data, signature);
                fetched = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex) when (ex is HttpRequestException or CatalogException or OperationCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                var cached = ReadCache(source);
                try
                {
                    if (cached is null) throw;
                    parsed = Accept(Convert.FromBase64String(cached.Data), cached.Signature, source, local);
                    fromCache = true;
                    fetched = Math.Min(fetched ?? long.MaxValue, cached.FetchedAt);
                    errors.Add(ex is CatalogException ? Strings.Get("catalog.sourceCached", Short(source), ex.Message) : Strings.Get("catalog.sourceOffline", Short(source)));
                    codes.Add(ex is CatalogException ? null : CodeOf(ex));
                }
                catch (Exception inner) when (inner is CatalogException or FormatException || ReferenceEquals(inner, ex))
                {
                    // A broken cache is as good as none.
                    errors.Add(Strings.Get("catalog.sourceError", Short(source), ex is OperationCanceledException ? Strings.Get("net.noAnswer") : ex.Message));
                    codes.Add(ex is CatalogException ? null : CodeOf(ex));
                    continue;
                }
            }

            foreach (var entry in parsed)
            {
                var idKey = $"{entry.Kind}:{entry.Id}";
                var folderKey = $"{entry.Kind}:{entry.Folder}";
                if (ids.TryGetValue(idKey, out var knownFolder))
                {
                    // Same id again: only interesting when it points somewhere else (a takeover attempt or a clash).
                    if (!knownFolder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(entry with { Problem = Strings.Get("catalog.collision"), DownloadUrl = null });
                    }
                    continue;
                }
                if (folders.ContainsKey(folderKey)) continue;   // first source wins
                ids[idKey] = entry.Folder;
                folders[folderKey] = entry.Id;
                entries.Add(entry);
            }
        }

        return new CatalogResult(entries, fromCache, fetched, errors.Count == 0 ? null : string.Join(" · ", errors),
            codes.Count > 0 && codes.All(c => c is not null && c == codes[0]) ? codes[0] : null);
    }

    /// <summary>notFound for a 404 (the repository or file does not exist), unreachable for everything else on the network.</summary>
    private static string CodeOf(Exception ex) => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? "notFound" : "unreachable";

    /// <summary>Verifies the signature (mandatory for the default source) and parses the index.</summary>
    private IReadOnlyList<CatalogEntry> Accept(byte[] data, string? signature, string source, bool local)
    {
        var verified = CatalogSignature.Verify(data, signature, publicKeyPem);
        if (IsDefaultSource(source) && !verified) throw new CatalogException(Strings.Get("catalog.untrusted"));
        var parsed = CatalogParser.Parse(System.Text.Encoding.UTF8.GetString(data), source, Language, local);
        return verified ? parsed : parsed.Select(e => e with { Unsigned = true }).ToList();
    }

    private static string Short(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.Host) : url;

    private async Task<byte[]> ReadBytesAsync(string url, long limit, CancellationToken ct)
    {
        var (stream0, length) = await OpenAsync(url, ct);
        await using var stream = stream0;
        if (length > limit) throw new CatalogException(Strings.Get("catalog.tooLarge"));
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, limit, Strings.Get("catalog.tooLarge"), ct);
        return buffer.ToArray();
    }

    private async Task<(Stream Stream, long? Length)> OpenAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.IsFile)
        {
            var file = File.OpenRead(uri.LocalPath);
            return (file, file.Length);
        }
        var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return (new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response), response.Content.Headers.ContentLength);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes. With <paramref name="idle"/> the token is cancelled when no data arrives for <see cref="DownloadInactivity"/>.</summary>
    private static async Task CopyLimitedAsync(Stream from, Stream to, long limit, string message, CancellationToken ct, CancellationTokenSource? idle = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            idle?.CancelAfter(DownloadInactivity);
            var read = await from.ReadAsync(buffer, ct);
            if (read <= 0) break;
            total += read;
            if (total > limit) throw new CatalogException(message);
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Disposes the HTTP response together with its stream.</summary>
    private sealed class ResponseStream(Stream inner, IDisposable response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // -----------------------------------------------------------------------
    // Cache

    private void WriteCache(string source, byte[] data, string? signature)
    {
        lock (_cacheLock)
        {
            try
            {
                var all = ReadAllCache();
                all[source] = new CacheEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Convert.ToBase64String(data), signature);
                Directory.CreateDirectory(dataDir);
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, CacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[catalog] cache not written: {ex.Message}");
            }
        }
    }

    private CacheEntry? ReadCache(string source)
    {
        lock (_cacheLock) return ReadAllCache().GetValueOrDefault(source);
    }

    private Dictionary<string, CacheEntry> ReadAllCache()
    {
        try
        {
            return File.Exists(CacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    // -----------------------------------------------------------------------
    // Install

    /// <summary>
    /// Downloads, verifies and installs. Checks (in this order, nothing is written before all pass):
    /// compatibility, origin (updates only from the source the package came from), https, hash present,
    /// download size, hash equal. Skins are usable at once; plugins are installed (new) or staged
    /// (update of a plugin that may be loaded) and need a restart.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(CatalogEntry entry, SemVer app, IEnumerable<PluginInfo>? plugins = null, CancellationToken ct = default)
    {
        var reason = CatalogRules.Reason(entry, app);
        if (reason is not null) throw new CatalogException(reason);
        var (installed, _, _, origin) = new CatalogRegistry(dataDir).Lookup(entry, plugins ?? []);
        var originProblem = CatalogRules.OriginProblem(entry, installed, origin);
        if (originProblem is not null) throw new CatalogException(originProblem);
        var local = allowLocal();
        if (entry.DownloadUrl is null || !CatalogParser.IsAllowedUrl(entry.DownloadUrl, local)) throw new CatalogException(Strings.Get("catalog.httpsOnly"));
        if (entry.Sha256 is null) throw new CatalogException(Strings.Get("catalog.noChecksumInstall"));

        var temp = Path.Combine(dataDir, "catalog-temp");
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, $"{Guid.NewGuid():N}.zip");
        try
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    idle.CancelAfter(DownloadInactivity);
                    var (source, length) = await OpenAsync(entry.DownloadUrl, idle.Token);
                    await using var stream = source;
                    if (length > MaxPackageBytes) throw new CatalogException(Strings.Get("catalog.packageTooLarge"));
                    await using var file = File.Create(zip);
                    await CopyLimitedAsync(stream, file, MaxPackageBytes, Strings.Get("catalog.packageTooLarge"), idle.Token, idle);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    throw new CatalogException(ex is OperationCanceledException ? Strings.Get("net.stalled") : Strings.Get("net.downloadFailed", ex.Message));
                }
            }

            CatalogRules.VerifySha256(zip, entry.Sha256);
            try
            {
                return Install(entry, zip);
            }
            catch (InvalidDataException ex)
            {
                throw new CatalogException(Strings.Get("catalog.archiveBad", ex.Message));
            }
        }
        finally
        {
            try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Installs an already verified package.</summary>
    internal InstallOutcome Install(CatalogEntry entry, string verifiedZip)
    {
        var registry = new CatalogRegistry(dataDir);
        if (entry.Kind == CatalogKind.Skin)
        {
            PackageInstaller.Install(verifiedZip, Path.Combine(dataDir, "skins"), PackageInstaller.Kind.Skin, entry.Folder);
            registry.Set(entry, pending: false);
            return new InstallOutcome(Strings.Get("catalog.skinInstalled", entry.Name, entry.Version), false);
        }

        var pluginRoot = Path.Combine(dataDir, "plugins");
        if (!Directory.Exists(Path.Combine(pluginRoot, entry.Folder)))
        {
            var target = PackageInstaller.Install(verifiedZip, pluginRoot, PackageInstaller.Kind.Plugin, entry.Folder);
            PendingPackages.WarnIfManifestDiffers(target, entry.Version);
            // Installed on purpose, from a verified package: these files are the trusted ones.
            new PluginTrust(dataDir).Trust(entry.Folder, target);
            registry.Set(entry, pending: false);
            return new InstallOutcome(Strings.Get("catalog.pluginInstalled", entry.Name, entry.Version), true);
        }

        // An older copy may be loaded (and its DLLs locked): replace it before the next start loads anything.
        PendingPackages.Stage(dataDir, entry, verifiedZip);
        registry.Set(entry, pending: true);
        return new InstallOutcome(Strings.Get("catalog.updateReady", entry.Name, entry.Version), true);
    }
}

/// <summary>Which catalog packages were installed through CouchHud: version, folder and the source they came from.</summary>
public sealed class CatalogRegistry(string dataDir)
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <param name="Sha256">Hash of the staged package (a pending update): checked again before it is put in place.</param>
    public sealed record Record(string Version, string Folder, bool Pending, string? Source = null, string? Sha256 = null);

    private string StoreFile => Path.Combine(dataDir, "catalog-installed.json");

    public static string Key(CatalogKind kind, string id) => $"{kind.ToString().ToLowerInvariant()}:{id}".ToLowerInvariant();

    public Dictionary<string, Record> Read()
    {
        lock (Lock)
        {
            try
            {
                return File.Exists(StoreFile)
                    ? new Dictionary<string, Record>(JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(StoreFile), Options) ?? [], StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Set(CatalogEntry entry, bool pending) => Set(Key(entry.Kind, entry.Id), new Record(entry.Version, entry.Folder, pending, entry.Source, pending ? entry.Sha256 : null));

    internal void Remove(string key)
    {
        lock (Lock)
        {
            var all = Read();
            if (!all.Remove(key)) return;
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(all));
        }
    }

    internal void Set(string key, Record record)
    {
        lock (Lock)
        {
            var all = Read();
            all[key] = record;
            Directory.CreateDirectory(dataDir);
            var tmp = StoreFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, StoreFile, overwrite: true);
        }
    }

    /// <summary>
    /// Is the entry installed, with which version, is an update waiting for a restart, and which source did it
    /// come from (null: bundled or installed by hand)? A plugin counts by its folder (user or bundled) or plugin
    /// id; a skin by its folder. For packages installed through the catalog the recorded version wins over
    /// what plugin.json says.
    /// </summary>
    public (bool Installed, string? Version, bool Pending, string? Source) Lookup(CatalogEntry entry, IEnumerable<PluginInfo> plugins)
    {
        var record = Read().GetValueOrDefault(Key(entry.Kind, entry.Id));
        if (entry.Kind == CatalogKind.Skin)
        {
            return Directory.Exists(Path.Combine(dataDir, "skins", entry.Folder)) ? (true, record?.Version, false, record?.Source) : (false, null, false, null);
        }

        var info = plugins.FirstOrDefault(p => p.Folder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        var onDisk = Directory.Exists(Path.Combine(dataDir, "plugins", entry.Folder));
        if (info is null && !onDisk) return (false, null, false, null);
        return (true, record?.Version ?? info?.Version, record is { Pending: true }, record?.Source);
    }
}

/// <summary>Plugin updates that wait for the next start, because the old files are in use while CouchHud runs.</summary>
public static class PendingPackages
{
    public static string Folder(string dataDir) => Path.Combine(dataDir, "pending");

    internal static void Stage(string dataDir, CatalogEntry entry, string verifiedZip)
    {
        Directory.CreateDirectory(Folder(dataDir));
        File.Copy(verifiedZip, ZipFor(dataDir, CatalogRegistry.Key(entry.Kind, entry.Id)), overwrite: true);
    }

    private static string ZipFor(string dataDir, string key) => Path.Combine(Folder(dataDir), key.Replace(':', '-') + ".zip");

    internal static void WarnIfManifestDiffers(string pluginDir, string catalogVersion)
    {
        var declared = PluginManager.ReadManifest(pluginDir)?.Version;
        if (declared is not null && declared != catalogVersion)
        {
            Console.Error.WriteLine($"[catalog] {Path.GetFileName(pluginDir)}: plugin.json says {declared}, catalog says {catalogVersion} (catalog version counts)");
        }
    }

    /// <summary>
    /// Call at startup before the plugins load. Never throws: a failure is logged and the staged update dropped.
    /// The entry is marked as done before the attempt, so even a crash in the middle cannot repeat at every boot.
    /// </summary>
    public static IReadOnlyList<string> ApplyAll(string dataDir, Action<string>? log = null)
    {
        var applied = new List<string>();
        try
        {
            var registry = new CatalogRegistry(dataDir);
            foreach (var (key, record) in registry.Read().Where(r => r.Value.Pending).ToList())
            {
                var zip = ZipFor(dataDir, key);
                try
                {
                    registry.Set(key, record with { Pending = false });
                    if (!File.Exists(zip)) throw new FileNotFoundException("staged package missing");
                    // The folder for staged packages is writable by every program of the user: it is the package that was
                    // verified when it was downloaded that goes in, not whatever lies there now.
                    CatalogRules.VerifySha256(zip, record.Sha256);
                    var target = PackageInstaller.Install(zip, Path.Combine(dataDir, "plugins"), PackageInstaller.Kind.Plugin, record.Folder);
                    WarnIfManifestDiffers(target, record.Version);
                    new PluginTrust(dataDir).Trust(record.Folder, target);
                    applied.Add(key);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[catalog] pending update {key} failed: {ex.Message}");
                    try { registry.Remove(key); } catch (Exception inner) { log?.Invoke($"[catalog] {inner.Message}"); }
                }
                finally
                {
                    try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[catalog] applying pending updates failed: {ex.Message}");
        }
        return applied;
    }
}
