using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VidFlow.Services;

/// <summary>Loads remote thumbnails through the shared (proxy-aware) HttpClient, with a small disk cache so the
/// download list shows its thumbnails instantly after a restart. Call on the UI thread.</summary>
public static class ImageLoader
{
    static string CacheDir => Path.Combine(AppSettings.DataDir, "thumbs");

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
