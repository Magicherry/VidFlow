using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VidFlow.Services;

/// <summary>Loads remote thumbnails through the shared (proxy-aware) HttpClient, with a small disk cache so the
/// download list shows its thumbnails instantly after a restart. Call on the UI thread.</summary>
public static class ImageLoader
{
    static string CacheDir => Path.Combine(AppSettings.DataDir, "thumbs");

    /// <summary>Uses the local video's Windows thumbnail, falling back to a small frame for unsupported codecs.</summary>
    public static async Task<BitmapImage?> LoadVideoAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 192,
                ThumbnailOptions.UseCurrentScale).AsTask(ct);
            if (thumbnail is { Type: ThumbnailType.Image })
            {
                var image = new BitmapImage { DecodePixelWidth = 192 };
                await image.SetSourceAsync(thumbnail).AsTask(ct);
                return image;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Info($"Windows video thumbnail not loaded ({path}): {ex.Message}"); }

        var frame = Path.Combine(Path.GetTempPath(), $"VidFlow-thumb-{Guid.NewGuid():N}.jpg");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var tools = await App.ToolUsage.UseAsync(timeout.Token);
            if (ToolManager.FfmpegPath is not { } ffmpeg) return null;
            var (code, _) = await ProcessRunner.RunAsync(ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-y", "-threads", "2", "-i", path,
                    "-map", "0:V:0", "-an", "-sn", "-vf", "scale=192:108:force_original_aspect_ratio=decrease,thumbnail=30",
                    "-frames:v", "1", "-filter_threads", "1", "-threads", "1", frame], null, timeout.Token);
            if (code != 0 || !File.Exists(frame)) return null;
            var bytes = await File.ReadAllBytesAsync(frame, timeout.Token);
            var image = new BitmapImage { DecodePixelWidth = 192 };
            using var stream = new MemoryStream(bytes);
            await image.SetSourceAsync(stream.AsRandomAccessStream()).AsTask(timeout.Token);
            return image;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Info($"Video thumbnail not loaded ({path}): {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(frame); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<BitmapImage?> LoadAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            byte[] bytes;
            try
            {
                bytes = await GetBytesAsync(url);
            }
            catch (HttpRequestException) when (url.Contains("/maxresdefault.", StringComparison.Ordinal))
            {
                // Older / low-resolution YouTube videos have no maxres thumbnail.
                bytes = await GetBytesAsync(url.Replace("/maxresdefault.", "/hqdefault.").Replace("/vi_webp/", "/vi/").Replace(".webp", ".jpg"));
            }
            var bmp = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bmp.SetSourceAsync(stream.AsRandomAccessStream());
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Info($"Thumbnail not loaded ({url}): {ex.Message}");
            return null;
        }
    }

    static async Task<byte[]> GetBytesAsync(string url)
    {
        var file = Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase))
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
        using var resp = await Http.Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        try
        {
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (IOException)
        {
            // Cache is best-effort.
        }
        return bytes;
    }

    /// <summary>Drops cached thumbnails older than 30 days.</summary>
    public static void PruneCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in new DirectoryInfo(CacheDir).GetFiles().Where(f => f.LastAccessTime < DateTime.Now.AddDays(-30)))
                f.Delete();
        }
        catch (IOException)
        {
        }
    }
}
