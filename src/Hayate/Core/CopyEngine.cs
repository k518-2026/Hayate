using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Hayate.Core;

/// <summary>
/// 速さの工夫:
///   - 複数ファイルを同時並行で処理（SSD/NVMe のキュー深度を埋める）
///   - 大きめのバッファ + 非同期 I/O + SequentialScan ヒント
///   - 書き込み先をあらかじめ SetLength で確保して断片化と拡張コストを避ける
///   - 同一ボリュームの「移動」はリネーム 1 回で完了
///   - 同じ内容のファイルは触らない
///   - 検証用ハッシュは読み込みバッファをそのまま使うので追加の読み込みが発生しない
///
/// 安全の工夫:
///   - 常に一時ファイル (.hayate-part) に書いてから、最後に原子的に置き換える
///     → 途中で落ちてもコピー先の既存ファイルは壊れない
///   - 書き込んだ内容を読み戻して xxHash64 で全バイト照合
///   - 閉じる前にディスクへ強制フラッシュ
///   - 移動は「検証に成功した後」にだけ元を消す
///   - 失敗時は自動リトライ、それでも駄目なら一時ファイルを片付けて次へ
///   - 完了記録（ジャーナル）で中断後に再開できる
/// </summary>
public sealed class CopyEngine : IDisposable
{
    private readonly CopyOptions _opt;
    private readonly ManualResetEventSlim _gate = new(true);
    private readonly ConcurrentBag<string> _errors = new();

    public CopyStats Stats { get; } = new();
    public event Action<string>? Log;

    public CopyEngine(CopyOptions options) => _opt = options;

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        IsPaused = true;
        _gate.Reset();
    }

    public void Resume()
    {
        IsPaused = false;
        _gate.Set();
    }

    public async Task<CopyResult> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Stats.Reset();
        bool canceled = false;

        string destRoot = Path.GetFullPath(_opt.Destination);
        Directory.CreateDirectory(PathUtil.Ext(destRoot));

        Log?.Invoke("コピー元を確認しています…");
        ScanResult scan;
        try
        {
            scan = FileScanner.Scan(_opt, s => Log?.Invoke(s), ct);
        }
        catch (OperationCanceledException)
        {
            return new CopyResult { Canceled = true, Elapsed = sw.Elapsed };
        }

        foreach (string e in scan.Errors) _errors.Add(e);

        Interlocked.Exchange(ref Stats.TotalFiles, scan.Files.Count);
        Interlocked.Exchange(ref Stats.TotalBytes, scan.TotalBytes);
        Log?.Invoke($"対象 {scan.Files.Count:N0} 件 / {PathUtil.FormatBytes(scan.TotalBytes)}");

        if (_opt.CheckFreeSpace && !_opt.DryRun)
        {
            string? problem = CheckFreeSpace(destRoot, scan.TotalBytes);
            if (problem is not null)
            {
                Log?.Invoke("[中止] " + problem);
                _errors.Add(problem);
                return BuildResult(sw.Elapsed, false);
            }
        }

        if (_opt.DryRun)
        {
            foreach (var item in scan.Files)
                Log?.Invoke($"[試行] {PathUtil.Shorten(item.Source)} → {PathUtil.Shorten(item.Destination)}");
            Log?.Invoke("ドライラン終了（何も書き込んでいません）");
            return BuildResult(sw.Elapsed, false);
        }

        foreach (string dir in scan.Directories)
        {
            try { Directory.CreateDirectory(PathUtil.Ext(dir)); }
            catch (Exception ex) { _errors.Add($"{dir}: {ex.Message}"); }
        }

        using var journal = new Journal(destRoot, _opt.UseJournal);
        if (journal.RestoredCount > 0)
            Log?.Invoke($"前回の記録から {journal.RestoredCount:N0} 件は完了済みとして再開します");

        // 大きいファイルから先に流すと並列度が最後まで保たれ、進捗も安定する
        var work = scan.Files.OrderByDescending(f => f.Size).ToArray();

        int dop = Math.Max(1, _opt.Parallelism);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(work, parallelOptions, async (item, token) =>
            {
                _gate.Wait(token);
                await ProcessFileAsync(item, journal, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            Log?.Invoke("中止しました。書きかけの一時ファイルは削除済みです。");
        }

        if (_opt.Mode == CopyMode.Move && !canceled)
            RemoveEmptySourceDirectories();

        if (!canceled && Interlocked.Read(ref Stats.FailedFiles) == 0)
            journal.Complete(destRoot);

        Stats.CurrentFile = "";
        return BuildResult(sw.Elapsed, canceled);
    }

    private CopyResult BuildResult(TimeSpan elapsed, bool canceled) => new()
    {
        Copied = Interlocked.Read(ref Stats.DoneFiles),
        Skipped = Interlocked.Read(ref Stats.SkippedFiles),
        Failed = Interlocked.Read(ref Stats.FailedFiles),
        Bytes = Interlocked.Read(ref Stats.DoneBytes),
        Elapsed = elapsed,
        Canceled = canceled,
        Errors = _errors.ToList()
    };

    private string? CheckFreeSpace(string destRoot, long needed)
    {
        try
        {
            string? root = Path.GetPathRoot(destRoot);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;

            long margin = 64L * 1024 * 1024;
            if (drive.AvailableFreeSpace < needed + margin)
            {
                return $"空き容量が足りません（必要 {PathUtil.FormatBytes(needed)} / " +
                       $"空き {PathUtil.FormatBytes(drive.AvailableFreeSpace)}）";
            }
        }
        catch { /* ネットワークドライブなどで取得できないことがある */ }
        return null;
    }

    // ------------------------------------------------------------------
    // 1 ファイルの処理
    // ------------------------------------------------------------------

    private async Task ProcessFileAsync(CopyItem item, Journal journal, CancellationToken ct)
    {
        if (journal.IsDone(item.RelativeKey))
        {
            Interlocked.Increment(ref Stats.SkippedFiles);
            Interlocked.Add(ref Stats.DoneBytes, item.Size);
            return;
        }

        string destination = item.Destination;

        try
        {
            string destExt = PathUtil.Ext(destination);
            if (File.Exists(destExt))
            {
                switch (_opt.Conflict)
                {
                    case ConflictPolicy.Skip:
                        Interlocked.Increment(ref Stats.SkippedFiles);
                        Interlocked.Add(ref Stats.DoneBytes, item.Size);
                        return;

                    case ConflictPolicy.Rename:
                        destination = PathUtil.MakeUnique(destination);
                        break;

                    case ConflictPolicy.NewerOnly:
                    {
                        var d = new FileInfo(destExt);
                        if (d.LastWriteTimeUtc >= item.LastWriteUtc)
                        {
                            Interlocked.Increment(ref Stats.SkippedFiles);
                            Interlocked.Add(ref Stats.DoneBytes, item.Size);
                            return;
                        }
                        break;
                    }

                    case ConflictPolicy.Overwrite:
                        if (_opt.SkipIdentical && IsIdentical(destExt, item))
                        {
                            Interlocked.Increment(ref Stats.SkippedFiles);
                            Interlocked.Add(ref Stats.DoneBytes, item.Size);
                            journal.MarkDone(item.RelativeKey);
                            return;
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(item, ex);
            return;
        }

        // 同一ボリューム内の移動はリネームで一瞬
        if (_opt.Mode == CopyMode.Move && _opt.FastMoveSameVolume &&
            PathUtil.SameVolume(item.Source, destination))
        {
            try
            {
                Stats.CurrentFile = item.Source;
                ClearReadOnly(PathUtil.Ext(destination));
                File.Move(PathUtil.Ext(item.Source), PathUtil.Ext(destination), overwrite: true);
                Interlocked.Increment(ref Stats.DoneFiles);
                Interlocked.Add(ref Stats.DoneBytes, item.Size);
                journal.MarkDone(item.RelativeKey);
                return;
            }
            catch
            {
                // リネームできない場合は通常のコピーへ落とす
            }
        }

        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            _gate.Wait(ct);

            string temp = PathUtil.TempPartPath(destination);
            var written = new WrittenCounter();
            try
            {
                Stats.CurrentFile = item.Source;
                ulong sourceHash = await CopyToTempAsync(item, temp, written, ct).ConfigureAwait(false);

                if (_opt.Verify != VerifyLevel.None)
                    await VerifyAsync(temp, item.Size, sourceHash, ct).ConfigureAwait(false);

                ApplyMetadata(item, temp);

                ClearReadOnly(PathUtil.Ext(destination));
                File.Move(PathUtil.Ext(temp), PathUtil.Ext(destination), overwrite: true);

                if (_opt.Mode == CopyMode.Move)
                {
                    ClearReadOnly(PathUtil.Ext(item.Source));
                    File.Delete(PathUtil.Ext(item.Source));
                }

                Interlocked.Increment(ref Stats.DoneFiles);
                journal.MarkDone(item.RelativeKey);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                Interlocked.Add(ref Stats.DoneBytes, -written.Take());
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(temp);

                // 失敗した分の進捗を巻き戻す（再試行で二重加算しないため）
                Interlocked.Add(ref Stats.DoneBytes, -written.Take());

                if (++attempt > _opt.RetryCount)
                {
                    Fail(item, ex);
                    return;
                }

                Log?.Invoke($"[再試行 {attempt}/{_opt.RetryCount}] {PathUtil.Shorten(item.Source)} " +
                            $"({ex.GetType().Name}: {ex.Message})");
                try
                {
                    await Task.Delay(_opt.RetryDelayMs * attempt, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
            }
        }
    }

    /// <summary>1 ファイル分の加算済みバイト数（リトライ時の巻き戻し用）。</summary>
    private sealed class WrittenCounter
    {
        private long _value;
        public void Add(long n) => Interlocked.Add(ref _value, n);
        public long Take() => Interlocked.Exchange(ref _value, 0);
    }

    private async Task<ulong> CopyToTempAsync(CopyItem item, string tempPath,
                                              WrittenCounter written, CancellationToken ct)
    {
        int bufferSize = ChooseBufferSize(item.Size);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var hasher = _opt.Verify == VerifyLevel.Hash ? new XxHash64() : null;
        long progressForThisFile = 0;

        try
        {
            await using var src = new FileStream(
                PathUtil.Ext(item.Source), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var dst = new FileStream(
                PathUtil.Ext(tempPath), FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // あらかじめ領域を確保しておくと拡張のたびのメタデータ更新が減り、断片化も抑えられる
            if (item.Size > 0)
            {
                try { dst.SetLength(item.Size); } catch { }
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _gate.Wait(ct);

                int read = await src.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false);
                if (read == 0) break;

                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher?.Append(buffer.AsSpan(0, read));

                progressForThisFile += read;
                Interlocked.Add(ref Stats.DoneBytes, read);
                written.Add(read);
            }

            // 実サイズが事前確保より小さかった場合に切り詰める
            if (dst.Length != progressForThisFile)
            {
                try { dst.SetLength(progressForThisFile); } catch { }
            }

            await dst.FlushAsync(ct).ConfigureAwait(false);
            if (_opt.FlushToDisk)
                dst.Flush(flushToDisk: true);

            // ここでは counter を消さない。検証や置き換えで失敗したときに
            // このファイル分をまとめて巻き戻せるようにしておく。
            return hasher?.Finish() ?? 0UL;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task VerifyAsync(string tempPath, long expectedSize, ulong expectedHash, CancellationToken ct)
    {
        var fi = new FileInfo(PathUtil.Ext(tempPath));
        if (fi.Length != expectedSize)
            throw new IOException($"サイズが一致しません（期待 {expectedSize} / 実際 {fi.Length}）");

        if (_opt.Verify != VerifyLevel.Hash) return;

        int bufferSize = ChooseBufferSize(expectedSize);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var hasher = new XxHash64();
            await using var fs = new FileStream(
                PathUtil.Ext(tempPath), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false);
                if (read == 0) break;
                hasher.Append(buffer.AsSpan(0, read));
            }

            ulong actual = hasher.Finish();
            if (actual != expectedHash)
                throw new IOException($"検証に失敗しました（{expectedHash:X16} ≠ {actual:X16}）");

            Interlocked.Increment(ref Stats.VerifiedFiles);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private int ChooseBufferSize(long fileSize)
    {
        int b = _opt.BufferSize;
        if (fileSize > 0 && fileSize < b) b = (int)Math.Max(64 * 1024, fileSize);
        return Math.Clamp(b, 64 * 1024, 64 * 1024 * 1024);
    }

    private void ApplyMetadata(CopyItem item, string tempPath)
    {
        string t = PathUtil.Ext(tempPath);
        try
        {
            var s = new FileInfo(PathUtil.Ext(item.Source));
            if (_opt.PreserveTimestamps)
            {
                File.SetCreationTimeUtc(t, s.CreationTimeUtc);
                File.SetLastWriteTimeUtc(t, s.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(t, s.LastAccessTimeUtc);
            }
            if (_opt.PreserveAttributes)
            {
                var attrs = s.Attributes
                    & ~FileAttributes.ReparsePoint
                    & ~FileAttributes.Directory;
                File.SetAttributes(t, attrs);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[注意] 属性を引き継げませんでした: {PathUtil.Shorten(item.Destination)} ({ex.Message})");
        }
    }

    private static bool IsIdentical(string destExtPath, CopyItem item)
    {
        try
        {
            var d = new FileInfo(destExtPath);
            if (d.Length != item.Size) return false;
            // FAT32 は 2 秒刻みなので許容差を持たせる
            return Math.Abs((d.LastWriteTimeUtc - item.LastWriteUtc).TotalSeconds) <= 2.0;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearReadOnly(string extPath)
    {
        try
        {
            if (!File.Exists(extPath)) return;
            var a = File.GetAttributes(extPath);
            if ((a & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(extPath, a & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            string p = PathUtil.Ext(path);
            if (File.Exists(p))
            {
                ClearReadOnly(p);
                File.Delete(p);
            }
        }
        catch { }
    }

    private void Fail(CopyItem item, Exception ex)
    {
        Interlocked.Increment(ref Stats.FailedFiles);
        string msg = $"{PathUtil.Plain(item.Source)}: {ex.Message}";
        _errors.Add(msg);
        Log?.Invoke($"[失敗] {PathUtil.Shorten(item.Source)} ({ex.Message})");
    }

    private void RemoveEmptySourceDirectories()
    {
        foreach (string raw in _opt.Sources)
        {
            try
            {
                string src = Path.GetFullPath(raw);
                if (!Directory.Exists(PathUtil.Ext(src))) continue;
                RemoveEmptyRecursive(src, isRoot: true);
            }
            catch { }
        }
    }

    private static void RemoveEmptyRecursive(string dir, bool isRoot)
    {
        try
        {
            foreach (string sub in Directory.GetDirectories(PathUtil.Ext(dir)))
                RemoveEmptyRecursive(PathUtil.Plain(sub), isRoot: false);

            if (Directory.GetFileSystemEntries(PathUtil.Ext(dir)).Length == 0)
                Directory.Delete(PathUtil.Ext(dir));
        }
        catch { }
    }

    public void Dispose() => _gate.Dispose();
}
