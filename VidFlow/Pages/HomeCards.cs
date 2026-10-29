using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>A supported-platform card on the New download page, with its sign-in state (for platforms that have one)
/// and, for platforms that need a proxy, whether the last connection check failed.</summary>
public sealed record HomePlatform(Platform Platform, ImageSource? Logo, string Name, string Supports, bool CanSignIn, bool SignedIn,
    bool Unreachable)
{
    /// <summary>Signing in doesn't help while the site can't be reached: the connection warning shows instead.</summary>
    public bool SignedOut => CanSignIn && !SignedIn && !Unreachable;

    public static HomePlatform Create(Platform p, string supports) =>
        new(p, LogoFor(p), p.DisplayName(), supports, AccountStore.HasSignIn(p), AccountStore.IsLoggedIn(p),
            Connectivity.NeedsProxy(p) && Connectivity.YouTubeUnreachable);

    public static ImageSource? LogoFor(Platform p) => p switch
    {
        Platform.Bilibili => Asset("bilibili"),
        Platform.YouTube => Asset("youtube"),
        Platform.Douyin => Asset("douyin"),
        Platform.Weibo => Asset("weibo"),
        Platform.Xiaohongshu => Asset("xiaohongshu"),
        Platform.X => Asset("x"),
        _ => null,
    };

    static BitmapImage Asset(string name) => new(new Uri($"ms-appx:///Assets/Platforms/{name}.png"));
}
