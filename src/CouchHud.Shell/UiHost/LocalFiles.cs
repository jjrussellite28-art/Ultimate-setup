using CouchHud.Core.Library;
using Microsoft.Web.WebView2.Core;

namespace CouchHud.Shell.UiHost;

/// <summary>
/// The UI runs on https://app.couchhud, so file:// URLs (Steam's local artwork
/// cache, custom covers, user skins) are served through a virtual host instead.
/// The path keeps its folder structure so relative URLs in skin CSS still work;
/// UNC paths become /UNC/server/share/....
/// </summary>
public static class LocalFiles
{
    public const string Host = "local.couchhud";
    private const string UncPrefix = "UNC";

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".css"] = "text/css",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm"
    };

    public static string Rewrite(string url)
    {
        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return url;
        var path = new Uri(url).LocalPath;
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal);
        var segments = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return $"https://{Host}/{(unc ? UncPrefix + "/" : "")}{string.Join('/', segments)}";
    }

    /// <summary>
    /// For the artwork of library entries: like <see cref="Rewrite"/>, and online images go through the disk cache
    /// (the address of the cached copy; the UI never loads artwork from the internet itself).
    /// </summary>
    public static string RewriteArt(string url) =>
        ArtworkCache.Shared is not null && ArtworkSources.CacheUrl(url) is { } cached ? cached : Rewrite(url);

    /// <summary>Picker thumbnails: the cached copy when there is one, else the online image (the user is browsing on purpose).</summary>
    public static string RewriteThumb(string url) =>
        ArtworkCache.Shared is { } cache && cache.Has(url) && ArtworkSources.CacheUrl(url) is { } cached ? cached : Rewrite(url);

    internal static string ToLocalPath(Uri uri)
    {
        var segments = uri.AbsolutePath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToList();
        if (segments.Count > 1 && segments[0] == UncPrefix) return @"\\" + string.Join('\\', segments.Skip(1));
        return string.Join('\\', segments);
    }

    /// <summary>
    /// Clips can be gigabytes: streamed from disk, and with Range support so the video
    /// element can seek (and read the index at the end of an mp4 without the whole file).
    /// </summary>
    private static CoreWebView2WebResourceResponse VideoResponse(CoreWebView2 web, string path, string mime, string? range)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        var length = file.Length;
        var common = $"Content-Type: {mime}\r\nAccept-Ranges: bytes\r\nAccess-Control-Allow-Origin: https://app.couchhud";
        if (range is not null && TryParseRange(range, length, out var start, out var end))
        {
            file.Position = start;
            var part = new RangeStream(file, end - start + 1);
            return web.Environment.CreateWebResourceResponse(part, 206, "Partial Content",
                $"{common}\r\nContent-Range: bytes {start}-{end}/{length}\r\nContent-Length: {end - start + 1}");
        }
        return web.Environment.CreateWebResourceResponse(file, 200, "OK", $"{common}\r\nContent-Length: {length}");
    }

    /// <summary>"bytes=100-" or "bytes=100-199"; a single range is all a video element asks for.</summary>
    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (length == 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = header[6..].Split(',')[0].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            // Suffix: the last N bytes.
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(parts[0], out start) || start >= length) return false;
        if (parts[1].Length > 0 && long.TryParse(parts[1], out var e)) end = Math.Min(e, length - 1);
        // Hand out at most 8 MB per request; the player asks for the rest.
        end = Math.Min(end, start + 8 * 1024 * 1024 - 1);
        return end >= start;
    }

    /// <summary>A window of a file stream; disposing it closes the file.</summary>
    private sealed class RangeStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public static void Attach(CoreWebView2 web)
    {
        ArtworkHost.Attach(web);
        web.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, e) =>
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return;

            var path = ToLocalPath(uri);
            // Plain local drive paths only: a skin pointing at \\attacker\share (or at \??\UNC\…, \\?\UNC\…) would make Windows
            // send the user's credentials there.
            if (!CouchHud.Core.Files.LocalPaths.IsAllowed(path, CouchHud.Core.Files.LocalPaths.IsNetworkDrive))
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                return;
            }
            // Only media, styles and fonts, never arbitrary files.
            if (!MimeTypes.TryGetValue(Path.GetExtension(path), out var mime))
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
                return;
            }

            Stream content;
            try
            {
                if (mime.StartsWith("video/", StringComparison.Ordinal))
                {
                    e.Response = VideoResponse(web, path, mime, e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null);
                    return;
                }
                // Copy into memory and share the file fully: Steam rewrites its artwork cache while we run,
                // and WebView2 would otherwise hold the handle until the next GC.
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var memory = new MemoryStream();
                file.CopyTo(memory);
                memory.Position = 0;
                content = memory;
                if (mime == "text/css")
                {
                    // Skin stylesheets cannot reach out to the web (see SkinCss).
                    content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CouchHud.Core.Skins.SkinCss.Sanitize(System.Text.Encoding.UTF8.GetString(memory.ToArray()))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            // Fonts are loaded with CORS from the app origin.
            var headers = $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: https://app.couchhud\r\nCache-Control: max-age=3600";
            e.Response = web.Environment.CreateWebResourceResponse(content, 200, "OK", headers);
        };
    }
}
