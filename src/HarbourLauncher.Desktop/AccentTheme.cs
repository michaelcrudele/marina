using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace HarbourLauncher.Desktop;

/// <summary>
/// Recolours the app around the player's accent colour. Everything that uses the
/// accent refers to it with DynamicResource, so swapping the resources here
/// updates the open window straight away.
/// </summary>
internal static class AccentTheme
{
    /// <summary>Marina's own orange (shipofharkinian.com's mainOrange).</summary>
    public static Color Default { get; } = Color.FromRgb(0xEB, 0x80, 0x2B);

    /// <summary>The window background the accent has to stand out against (WindowBackground in App.axaml).</summary>
    private static readonly Color Background = Color.FromRgb(0x0E, 0x13, 0x18);

    public static Color Parse(string? hex) => hex is not null && Color.TryParse(hex, out var c) ? WithoutAlpha(c) : Default;

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithoutAlpha(Color c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>True when the colour is too dark to read as text on the window background.</summary>
    public static bool IsHardToSee(Color accent) => Contrast(accent, Background) < 3.0;

    public static void Apply(Color accent)
    {
        if (Application.Current is not { } app) return;
        accent = WithoutAlpha(accent);
        var res = app.Resources;

        // The app's own brushes (see App.axaml).
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentHover"] = new SolidColorBrush(Mix(accent, Colors.White, 0.16));
        res["AccentPressed"] = new SolidColorBrush(Mix(accent, Colors.Black, 0.15));
        res["OnAccent"] = new SolidColorBrush(TextOn(accent));
        res["NoticeBackground"] = new SolidColorBrush(Mix(Background, accent, 0.13));

        // Fluent's accent, used by checkboxes, progress bars, focused text boxes and so on.
        // These sit in Application.Resources, which is looked in before the theme's palette.
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15);
        res["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.30);
        res["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.45);

        // And the palette in App.axaml, which is where the theme itself reads it from.
        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is not null && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
            palette.Accent = accent;
    }

    /// <summary>
    /// Text for on top of the accent (e.g. on primary buttons): a very dark shade of
    /// the accent, or white, whichever is easier to read.
    /// </summary>
    private static Color TextOn(Color accent)
    {
        var dark = Mix(accent, Colors.Black, 0.88);
        return Contrast(accent, dark) >= Contrast(accent, Colors.White) ? dark : Colors.White;
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));
    }

    /// <summary>WCAG contrast ratio, from 1 (same) to 21 (black on white).</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
