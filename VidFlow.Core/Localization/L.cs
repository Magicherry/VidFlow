using System.Globalization;
using System.Resources;

namespace VidFlow.Localization;

/// <summary>
/// Localized strings. English is the neutral language (Strings.resx); Chinese lives in Strings.zh.resx.
/// XAML uses it through function binding: <c>Text="{x:Bind l:L.Get('Home_Title')}"</c>.
/// </summary>
public static class L
{
    static readonly ResourceManager Resources = new("VidFlow.Localization.Strings", typeof(L).Assembly);

    /// <summary>Applies the user's language choice ("" = follow Windows, "zh", "en"). Call before any UI is created.</summary>
    public static void ApplyLanguage(string language)
    {
        var culture = language switch
        {
            "zh" => new CultureInfo("zh-CN"),
            "en" => new CultureInfo("en-US"),
            _ => CultureInfo.InstalledUICulture,
        };
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static bool IsChinese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
