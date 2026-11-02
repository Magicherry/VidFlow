using System.Text.Json;
using System.Text.RegularExpressions;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>
/// TikTok photo posts (slideshows), which yt-dlp doesn't read. The post's data is in the page itself, but only on its
/// /video/ address: the /photo/ address serves a page without it, even for the same post.
/// </summary>
public static partial class TikTokPhotos
{
    [GeneratedRegex(@"tiktok\.com/(?:@(?<user>[^/?#]+)/)?(?<kind>video|photo)/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PostAddress();

    [GeneratedRegex(@"<script[^>]*id=""__UNIVERSAL_DATA_FOR_REHYDRATION__""[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex PageData();

    public static bool IsPhotoUrl(string url) =>
        PostAddress().Match(url) is { Success: true } m && m.Groups["kind"].Value.Equals("photo", StringComparison.OrdinalIgnoreCase);

    /// <summary>The address whose page carries the post's data.</summary>
    public static string? DataPageUrl(string url)
    {
        var m = PostAddress().Match(url);
        if (!m.Success) return null;
        var user = m.Groups["user"].Success ? m.Groups["user"].Value : "i";
        return $"https://www.tiktok.com/@{user}/video/{m.Groups["id"].Value}";
    }

    /// <summary>The post in a page, or null if the page has no photo post (a video, or nothing readable).</summary>
    public static PhotoPost? Parse(string html)
    {
        if (PageData().Match(html) is not { Success: true } data) return null;
        try
        {
            using var doc = JsonDocument.Parse(data.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("__DEFAULT_SCOPE__", out var scope)
                || !scope.TryGetProperty("webapp.video-detail", out var detail)
                || !detail.TryGetProperty("itemInfo", out var info)
                || !info.TryGetProperty("itemStruct", out var item)
                || !item.TryGetProperty("imagePost", out var post)
                || !post.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return null;

            var list = images.EnumerateArray().Select(i => new PostImage(Urls(i))).Where(i => i.Urls.Count > 0).ToList();
            if (list.Count == 0) return null;

            var id = Str(item, "id") ?? "";
            var title = (Str(item, "desc") ?? Str(post, "title") ?? "").Trim();
            if (title.Length == 0) title = $"TikTok {id}";
            var date = item.TryGetProperty("createTime", out var ct) && long.TryParse(ct.ToString(), out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyyMMdd")
                : null;
            var author = item.TryGetProperty("author", out var a) ? Str(a, "nickname") ?? Str(a, "uniqueId") : null;
            var cover = post.TryGetProperty("cover", out var c) ? Urls(c).FirstOrDefault() : null;
            var music = item.TryGetProperty("music", out var m) ? Str(m, "playUrl") : null;
            return new(id, title, author, date, cover ?? list[0].Urls[0], list, string.IsNullOrEmpty(music) ? null : music);
        }
        catch (JsonException ex)
        {
            Log.Info($"TikTok page data unreadable: {ex.Message}");
            return null;
        }
    }

    static List<string> Urls(JsonElement image) =>
        image.TryGetProperty("imageURL", out var u) && u.TryGetProperty("urlList", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => x.Length > 0).ToList()
            : [];

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads the post's page, signed in if there is a TikTok sign-in. Null if it isn't a photo post.</summary>
    public static async Task<PhotoPost?> FetchAsync(string url, CancellationToken ct)
    {
        if (DataPageUrl(url) is not { } page) return null;
        var handler = Http.CreateHandler();
        handler.UseCookies = false; // Otherwise the Cookie header below is ignored.
        using var client = Http.Create(handler, TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        if (AccountStore.CookieHeader(Platform.TikTok) is { } cookies) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static VideoInfo ToVideoInfo(PhotoPost post, string url)
    {
        var user = PostAddress().Match(url).Groups["user"];
        return new VideoInfo
        {
            Url = $"https://www.tiktok.com/@{(user.Success ? user.Value : "i")}/photo/{post.Id}",
            Title = post.Title,
            Uploader = post.Author,
            ThumbnailUrl = post.Cover,
            UploadDate = post.UploadDate,
            Platform = Platform.TikTok,
            PhotoCount = post.Images.Count,
        };
    }

    /// <summary>Image addresses expire, so the page is read again right before.</summary>
    public static async Task<string> DownloadAsync(DownloadRequest request, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        var post = await FetchAsync(request.Url, ct) ?? throw new YtDlpException(L.Get("Error_TikTokNoPhotos"));
        return await PhotoPosts.SaveAsync(post, request.OutputFolder, null, onEvent, ct) ?? throw new YtDlpException(L.Get("Error_TikTokNoPhotos"));
    }
}
