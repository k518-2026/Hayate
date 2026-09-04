using System;
using System.Collections.Generic;
using System.Threading;

namespace Hayate.Core;

public enum CopyMode
{
    /// <summary>コピー（元は残す）</summary>
    Copy,
    /// <summary>移動（検証に成功したものだけ元を削除）</summary>
    Move
}

public enum ConflictPolicy
{
    /// <summary>上書きする（一時ファイル経由なので途中で壊れない）</summary>
    Overwrite,
    /// <summary>コピー先にあれば飛ばす</summary>
    Skip,
    /// <summary>コピー元の方が新しいときだけ上書き</summary>
    NewerOnly,
    /// <summary>別名 (2) を付けて保存</summary>
    Rename
}

public enum VerifyLevel
{
    /// <summary>検証しない（最速）</summary>
    None,
    /// <summary>サイズだけ照合</summary>
    Size,
    /// <summary>書き込んだ内容を読み戻して xxHash64 で全バイト照合（既定）</summary>
    Hash
}

public sealed class CopyOptions
{
    public List<string> Sources { get; init; } = new();
    public string Destination { get; set; } = "";

    public CopyMode Mode { get; set; } = CopyMode.Copy;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Overwrite;
    public VerifyLevel Verify { get; set; } = VerifyLevel.Hash;

    /// <summary>同時に処理するファイル数。SSD/NVMe なら 4〜8、HDD なら 1〜2。</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>読み書きバッファのサイズ（バイト）。</summary>
    public int BufferSize { get; set; } = 4 * 1024 * 1024;

    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;

    /// <summary>閉じる前に OS のキャッシュをディスクへ強制的に書き出す。</summary>
    public bool FlushToDisk { get; set; } = true;

    /// <summary>実際には書き込まず、対象と件数だけを確認する。</summary>
    public bool DryRun { get; set; }

    /// <summary>サイズと更新日時が同じファイルは処理しない。</summary>
    public bool SkipIdentical { get; set; } = true;

    public int RetryCount { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 500;

    /// <summary>コピー先に完了記録を残し、中断後の再開時に済んだ分を飛ばす。</summary>
    public bool UseJournal { get; set; } = true;

    /// <summary>同一ボリューム内の移動はリネームで一瞬で済ませる。</summary>
    public bool FastMoveSameVolume { get; set; } = true;

    /// <summary>開始前に空き容量を確認する。</summary>
    public bool CheckFreeSpace { get; set; } = true;

    /// <summary>除外パターン（*.tmp など、セミコロン区切り）。</summary>
    public string ExcludePatterns { get; set; } = "";
}

public sealed class CopyItem
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string RelativeKey { get; init; } = "";
}

public sealed class CopyStats
{
    public long TotalFiles;
    public long TotalBytes;
    public long DoneFiles;
    public long DoneBytes;
    public long SkippedFiles;
    public long FailedFiles;
    public long VerifiedFiles;

    private string _current = "";
    public string CurrentFile
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public void Reset()
    {
        TotalFiles = TotalBytes = DoneFiles = DoneBytes = 0;
        SkippedFiles = FailedFiles = VerifiedFiles = 0;
        CurrentFile = "";
    }
}

public sealed class CopyResult
{
    public long Copied { get; init; }
    public long Skipped { get; init; }
    public long Failed { get; init; }
    public long Bytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Canceled { get; init; }
    public List<string> Errors { get; init; } = new();
}
