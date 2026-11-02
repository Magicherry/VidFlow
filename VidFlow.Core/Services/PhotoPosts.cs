using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>One image of a photo post: the addresses it can be fetched from, best first.</summary>
public sealed record PostImage(IReadOnlyList<string> Urls);

public sealed record PhotoPost(string Id, string Title, string? Author, string? UploadDate, string? Cover,
    IReadOnlyList<PostImage> Images, string? MusicUrl);

/// <summary>Saving a photo post (TikTok, Douyin), which each platform's reader has found the images of.</summary>
public static class PhotoPosts
{
    public static DownloadRequest Request(string url, string folder) => new(url, DownloadRequest.PhotosMode, 0, false, folder);

    /// <summary>"Title [id]": a folder of its own, since a post is several files.</summary>
    public static string FolderName(PhotoPost post)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var title = new string(post.Title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (title.Length > 80) title = title[..80].TrimEnd();
        return $"{title} [{post.Id}]";
    }

    /// <summary>Saves the images as 01.jpg, 02.jpg… and the post's sound if it can be had. Files already there are kept,
    /// so a paused download carries on. Returns the first image; null if an image couldn't be fetched.</summary>
    public static async Task<string?> SaveAsync(PhotoPost post, string outputFolder, string? referer, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        onEvent(new StartedEvent(1, post.Title, post.Cover));
        var folder = Path.Combine(outputFolder, FolderName(post));
        Directory.CreateDirectory(folder);

        string? first = null;
        var digits = Math.Max(2, post.Images.Count.ToString().Length);
        for (var i = 0; i < post.Images.Count; i++)
        {
            var name = (i + 1).ToString().PadLeft(digits, '0');
            var path = Directory.GetFiles(folder, name + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                       ?? await FetchAsync(post.Images[i].Urls, Path.Combine(folder, name), ".jpg", referer, ct);
            if (path is null) return null;
            first ??= path;
            onEvent(new ProgressEvent(new DownloadProgress(false, false, 1, 1, (i + 1) * 100.0 / post.Images.Count, 0, 0, 0, 0)));
        }

        if (post.MusicUrl is { } music && Directory.GetFiles(folder, "sound.*").Length == 0
            && await FetchAsync([music], Path.Combine(folder, "sound"), ".mp3", referer, ct) is null)
            Log.Info($"Sound not saved for post {post.Id}");
        return first;
    }

    /// <summary>The first address that answers, with the extension its content type says; null if none does.</summary>
    static async Task<string?> FetchAsync(IReadOnlyList<string> urls, string pathWithoutExtension, string fallbackExtension, string? referer, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            var part = pathWithoutExtension + ".part";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer is not null) request.Headers.Referrer = new Uri(referer);
                using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var path = pathWithoutExtension + ExtensionFor(response.Content.Headers.ContentType?.MediaType, fallbackExtension);
                await using (var file = File.Create(part))
                    await response.Content.CopyToAsync(file, ct);
                File.Move(part, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Log.Info($"Post download failed, trying the next address: {ex.Message}");
            }
            finally
            {
                File.Delete(part);
            }
        }
        return null;
    }

    public static string ExtensionFor(string? mediaType, string fallback) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/aac" or "video/mp4" => ".m4a",
        _ => fallback,
    };
}
