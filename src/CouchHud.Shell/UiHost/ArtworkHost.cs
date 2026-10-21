using CouchHud.Core.Library;
using Microsoft.Web.WebView2.Core;

namespace CouchHud.Shell.UiHost;

/// <summary>
/// Virtual host for the image cache: https://art.couchhud/&lt;64 hex digits&gt;. Nothing but the cache's own index
/// can be reached through it (no paths, no folders). Every answer is checked again: it must still be a
/// complete png/jpeg/webp/gif of limited size, and it is served with the type found in its bytes.
/// An image that isn't cached (yet) is a plain 404 that is never remembered, so the UI shows its
/// generated banner and the next screen refresh finds the image once it has arrived.
/// Reading and checking happen off the UI thread (a deferral holds the request meanwhile): a grid of
/// covers asks for dozens of images at once.
/// </summary>
public static class ArtworkHost
{
    public static void Attach(CoreWebView2 web)
    {
        web.AddWebResourceRequestedFilter($"https://{ArtworkCache.Host}/*", CoreWebView2WebResourceContext.All);
        // The event fires on the UI thread; the answer has to be handed in there again.
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        web.WebResourceRequested += (_, e) =>
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.Equals(ArtworkCache.Host, StringComparison.OrdinalIgnoreCase)) return;

            var cache = ArtworkCache.Shared;
            if (!e.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || cache is null)
            {
                e.Response = NotFound(web);
                return;
            }

            var deferral = e.GetDeferral();
            var path = uri.AbsolutePath;
            Task.Run(() => cache.Read(path)).ContinueWith(read =>
            {
                try
                {
                    if (read.IsCompletedSuccessfully && read.Result is { } image)
                    {
                        // The app origin reads pixels for the ambient color, so the image carries its CORS header.
                        var headers = $"Content-Type: {image.ContentType}\r\nAccess-Control-Allow-Origin: https://app.couchhud\r\nCache-Control: max-age=86400\r\nX-Content-Type-Options: nosniff";
                        e.Response = web.Environment.CreateWebResourceResponse(new MemoryStream(image.Data, writable: false), 200, "OK", headers);
                    }
                    else e.Response = NotFound(web);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[artwork] host: {ex.Message}");
                }
                finally
                {
                    deferral.Complete();
                }
            }, ui);
        };
    }

    private static CoreWebView2WebResourceResponse NotFound(CoreWebView2 web) =>
        web.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
}
