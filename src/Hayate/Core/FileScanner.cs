using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;

namespace Hayate.Core;

public sealed class ScanResult
{
    public List<string> Directories { get; } = new();
    public List<CopyItem> Files { get; } = new();
    public List<string> Errors { get; } = new();
    public long TotalBytes { get; set; }
}

/// <summary>
/// コピー元を走査して作業リストを作る。
/// 走査中に例外が出ても止めず、読めなかった場所を記録して続ける。
/// </summary>
public static class FileScanner
{
    public static ScanResult Scan(CopyOptions opt, Action<string>? log, CancellationToken ct)
    {
        var result = new ScanResult();
        string[] excludes = PathUtil.ParsePatterns(opt.ExcludePatterns);
        string destFull = Path.GetFullPath(opt.Destination);

        foreach (string rawSource in opt.Sources)
        {
            ct.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(rawSource);

            try
            {
                if (File.Exists(PathUtil.Ext(source)))
                {
                    string name = Path.GetFileName(source);
                    if (excludes.Length > 0 && PathUtil.MatchesAny(name, excludes)) continue;
                    if (PathUtil.IsPartFile(name)) continue;

                    var fi = new FileInfo(PathUtil.Ext(source));
                    var item = new CopyItem
                    {
                        Source = source,
                        Destination = Path.Combine(destFull, name),
                        Size = fi.Length,
                        LastWriteUtc = fi.LastWriteTimeUtc,
                        RelativeKey = name
                    };
                    result.Files.Add(item);
                    result.TotalBytes += item.Size;
                }
                else if (Directory.Exists(PathUtil.Ext(source)))
                {
                    string folderName = new DirectoryInfo(source).Name;
                    string destRoot = Path.Combine(destFull, folderName);

                    // 自分自身の中へコピーしようとしていないか
                    if (IsInside(destFull, source))
                    {
                        result.Errors.Add($"コピー先がコピー元の内側です: {source}");
                        log?.Invoke($"[中止] コピー先がコピー元の内側にあります: {source}");
                        continue;
                    }

                    result.Directories.Add(destRoot);
                    ScanDirectory(source, destRoot, folderName, excludes, result, log, ct);
                }
                else
                {
                    result.Errors.Add($"見つかりません: {source}");
                    log?.Invoke($"[警告] 見つかりません: {source}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{source}: {ex.Message}");
                log?.Invoke($"[警告] 走査できません: {PathUtil.Shorten(source)} ({ex.Message})");
            }
        }

        return result;
    }

    private static void ScanDirectory(string sourceDir, string destDir, string relPrefix,
                                      string[] excludes, ScanResult result,
                                      Action<string>? log, CancellationToken ct)
    {
        var stack = new Stack<(string src, string dst, string rel)>();
        stack.Push((sourceDir, destDir, relPrefix));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (src, dst, rel) = stack.Pop();

            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(PathUtil.Ext(src));
                dirs = Directory.GetDirectories(PathUtil.Ext(src));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{src}: {ex.Message}");
                log?.Invoke($"[警告] 読めないフォルダーを飛ばしました: {PathUtil.Shorten(src)}");
                continue;
            }

            foreach (string f in files)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(f);
                if (PathUtil.IsPartFile(name)) continue;
                if (name.Equals(Journal.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (excludes.Length > 0 && PathUtil.MatchesAny(name, excludes)) continue;

                try
                {
                    var fi = new FileInfo(PathUtil.Ext(f));
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue; // リンクは辿らない

                    var item = new CopyItem
                    {
                        Source = PathUtil.Plain(f),
                        Destination = Path.Combine(dst, name),
                        Size = fi.Length,
                        LastWriteUtc = fi.LastWriteTimeUtc,
                        RelativeKey = rel + "/" + name
                    };
                    result.Files.Add(item);
                    result.TotalBytes += item.Size;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{f}: {ex.Message}");
                }
            }

            foreach (string d in dirs)
            {
                try
                {
                    var di = new DirectoryInfo(PathUtil.Ext(d));
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    string name = di.Name;
                    string childDst = Path.Combine(dst, name);
                    result.Directories.Add(childDst);
                    stack.Push((PathUtil.Plain(d), childDst, rel + "/" + name));
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{d}: {ex.Message}");
                }
            }
        }
    }

    private static bool IsInside(string candidateChild, string parent)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateChild));
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
