namespace HarbourLauncher.Core.SelfUpdate;

/// <summary>
/// A version like 0.1.42, 1.2.3-beta or v1.2.3 (a leading "v" and anything after
/// "+" are ignored). A version with a pre-release part (e.g. -beta, -dev) comes
/// before the same version without one, as in semantic versioning.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string PreRelease = "")
    : IComparable<AppVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        string pre = dash >= 0 ? s[(dash + 1)..] : "";
        string core = dash >= 0 ? s[..dash] : s;

        var parts = core.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        if (dash >= 0 && pre.Length == 0) return false;

        version = new AppVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (IsPreRelease != other.IsPreRelease) return IsPreRelease ? -1 : 1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    // Dot-separated parts: numbers compare as numbers and before words; words compare as text.
    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xNum = int.TryParse(x[i], out int xn);
            bool yNum = int.TryParse(y[i], out int yn);
            int c = (xNum, yNum) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
