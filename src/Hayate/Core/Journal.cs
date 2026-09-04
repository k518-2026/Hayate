using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hayate.Core;

/// <summary>
/// 完了したファイルをコピー先に追記記録する。
/// 途中で電源が落ちても、次回は済んだ分を飛ばして再開できる。
/// </summary>
public sealed class Journal : IDisposable
{
    public const string FileName = ".hayate-journal";

    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private int _sinceFlush;

    public Journal(string destinationRoot, bool enabled)
    {
        if (!enabled) return;

        string path = Path.Combine(destinationRoot, FileName);
        try
        {
            if (File.Exists(PathUtil.Ext(path)))
            {
                foreach (string line in File.ReadLines(PathUtil.Ext(path)))
                {
                    if (line.Length > 0) _done.Add(line);
                }
            }

            var fs = new FileStream(PathUtil.Ext(path), FileMode.Append, FileAccess.Write,
                                    FileShare.Read, 4096, FileOptions.None);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
            try
            {
                File.SetAttributes(PathUtil.Ext(path), FileAttributes.Hidden);
            }
            catch { /* 属性が付けられなくても動作に支障はない */ }
        }
        catch
        {
            _writer = null; // ジャーナルが使えなくてもコピー自体は続ける
        }
    }

    public int RestoredCount => _done.Count;

    public bool IsDone(string key) => _done.Count > 0 && _done.Contains(key);

    public void MarkDone(string key)
    {
        if (_writer is null) return;
        lock (_lock)
        {
            _done.Add(key);
            _writer.WriteLine(key);
            if (++_sinceFlush >= 64)
            {
                _writer.Flush();
                _sinceFlush = 0;
            }
        }
    }

    /// <summary>全部成功して終わったら記録は不要なので消す。</summary>
    public void Complete(string destinationRoot)
    {
        Dispose();
        try
        {
            string path = PathUtil.Ext(Path.Combine(destinationRoot, FileName));
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { /* 消せなくても実害はない */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { }
        }
    }
}
