using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>A getting-started card on the New download page.</summary>
public sealed record HomeTip(string Glyph, string Title, string Body);

/// <summary>A supported-platform card on the New download page, with its sign-in state.</summary>
public sealed record HomePlatform(Platform Platform, ImageSource? Logo, string Name, string Supports, bool SignedIn)
{
    public bool SignedOut => !SignedIn;

    public static HomePlatform Create(Platform p, string supports) =>
        new(p, LogoFor(p), p.DisplayName(), supports, AccountStore.IsLoggedIn(p));

    public static ImageSource? LogoFor(Platform p) => p switch
    {
        Platform.Bilibili => Asset("bilibili"),
        Platform.YouTube => Asset("youtube"),
        Platform.Douyin => Asset("douyin"),
        _ => null,
    };

    static BitmapImage Asset(string name) => new(new Uri($"ms-appx:///Assets/Platforms/{name}.png"));
}
