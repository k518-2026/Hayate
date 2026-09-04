using System;
using System.IO;
using System.Text;

namespace Hayate.Core;

public static class PathUtil
{
    /// <summary>
    /// 260 文字制限を回避するための拡張長パス表記に変換する。
    /// </summary>
    public static string Ext(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    public static string Plain(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    public static bool SameVolume(string a, string b)
    {
        try
        {
            string? ra = Path.GetPathRoot(Path.GetFullPath(Plain(a)));
            string? rb = Path.GetPathRoot(Path.GetFullPath(Plain(b)));
            return ra is not null && rb is not null &&
                   string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>コピー先が既にあるとき「名前 (2).ext」を探す。</summary>
    public static string MakeUnique(string destination)
    {
        if (!File.Exists(Ext(destination))) return destination;

        string dir = Path.GetDirectoryName(destination) ?? "";
        string name = Path.GetFileNameWithoutExtension(destination);
        string ext = Path.GetExtension(destination);

        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(Ext(candidate))) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public static string TempPartPath(string destination)
        => destination + "." + Guid.NewGuid().ToString("N")[..8] + ".hayate-part";

    public static bool IsPartFile(string path)
        => path.EndsWith(".hayate-part", StringComparison.OrdinalIgnoreCase);

    public static string FormatBytes(long bytes)
    {
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
        => FormatBytes((long)bytesPerSecond) + "/s";

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}時間{t.Minutes}分{t.Seconds}秒";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}分{t.Seconds}秒";
        return $"{t.TotalSeconds:0.0}秒";
    }

    public static string Shorten(string path, int max = 70)
    {
        path = Plain(path);
        if (path.Length <= max) return path;
        return string.Concat(path.AsSpan(0, 18), " … ", path.AsSpan(path.Length - (max - 21)));
    }

    /// <summary>セミコロン区切りのワイルドカードでファイル名を判定する。</summary>
    public static bool MatchesAny(string fileName, string[] patterns)
    {
        foreach (string p in patterns)
        {
            if (Wildcard(fileName, p)) return true;
        }
        return false;
    }

    public static string[] ParsePatterns(string raw)
        => raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries |
                                          StringSplitOptions.TrimEntries);

    private static bool Wildcard(string text, string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (char c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, sb.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
