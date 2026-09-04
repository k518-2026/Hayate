using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hayate.Core;
using Microsoft.Win32;

namespace Hayate.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private CopyEngine? _engine;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _clock = new();

    private long _lastBytes;
    private TimeSpan _lastTick;
    private double _smoothedSpeed;

    public MainViewModel()
    {
        AddFilesCommand = new RelayCommand(AddFiles);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected);
        ClearSourcesCommand = new RelayCommand(() => Sources.Clear());
        BrowseDestinationCommand = new RelayCommand(BrowseDestination);
        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsRunning);
        PauseCommand = new RelayCommand(TogglePause, () => IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearLogCommand = new RelayCommand(() => LogLines.Clear());

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += (_, _) => UpdateProgress();
    }

    // ---------------- 入出力 ----------------

    public ObservableCollection<string> Sources { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private string _destination = "";
    public string Destination
    {
        get => _destination;
        set => SetProperty(ref _destination, value);
    }

    // ---------------- 設定 ----------------

    public string[] ModeNames { get; } = { "コピー（元を残す）", "移動（検証後に元を削除）" };
    private int _modeIndex;
    public int ModeIndex { get => _modeIndex; set => SetProperty(ref _modeIndex, value); }

    public string[] ConflictNames { get; } =
        { "上書きする", "既にあれば飛ばす", "新しいときだけ上書き", "別名で保存" };
    private int _conflictIndex;
    public int ConflictIndex { get => _conflictIndex; set => SetProperty(ref _conflictIndex, value); }

    public string[] VerifyNames { get; } =
        { "検証なし（最速）", "サイズのみ照合", "全バイト照合（推奨）" };
    private int _verifyIndex = 2;
    public int VerifyIndex { get => _verifyIndex; set => SetProperty(ref _verifyIndex, value); }

    public string[] ParallelNames { get; } = { "1（HDD向け）", "2", "4（既定）", "8", "16" };
    private int _parallelIndex = 2;
    public int ParallelIndex { get => _parallelIndex; set => SetProperty(ref _parallelIndex, value); }

    public string[] BufferNames { get; } = { "256 KB", "1 MB", "4 MB（既定）", "16 MB", "64 MB" };
    private int _bufferIndex = 2;
    public int BufferIndex { get => _bufferIndex; set => SetProperty(ref _bufferIndex, value); }

    private bool _preserveTimestamps = true;
    public bool PreserveTimestamps { get => _preserveTimestamps; set => SetProperty(ref _preserveTimestamps, value); }

    private bool _preserveAttributes = true;
    public bool PreserveAttributes { get => _preserveAttributes; set => SetProperty(ref _preserveAttributes, value); }

    private bool _flushToDisk = true;
    public bool FlushToDisk { get => _flushToDisk; set => SetProperty(ref _flushToDisk, value); }

    private bool _skipIdentical = true;
    public bool SkipIdentical { get => _skipIdentical; set => SetProperty(ref _skipIdentical, value); }

    private bool _useJournal = true;
    public bool UseJournal { get => _useJournal; set => SetProperty(ref _useJournal, value); }

    private bool _checkFreeSpace = true;
    public bool CheckFreeSpace { get => _checkFreeSpace; set => SetProperty(ref _checkFreeSpace, value); }

    private bool _dryRun;
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }

    private string _excludePatterns = "";
    public string ExcludePatterns { get => _excludePatterns; set => SetProperty(ref _excludePatterns, value); }

    private bool _notifyWithPopup = true;
    public bool NotifyWithPopup { get => _notifyWithPopup; set => SetProperty(ref _notifyWithPopup, value); }

    private bool _notifyWithSound = true;
    public bool NotifyWithSound { get => _notifyWithSound; set => SetProperty(ref _notifyWithSound, value); }

    // ---------------- 状態 ----------------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            StartCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsRunning;

    private double _overallPercent;
    public double OverallPercent { get => _overallPercent; set => SetProperty(ref _overallPercent, value); }

    private string _statusText = "待機中";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _speedText = "-";
    public string SpeedText { get => _speedText; set => SetProperty(ref _speedText, value); }

    private string _elapsedText = "経過 0.0秒";
    public string ElapsedText { get => _elapsedText; set => SetProperty(ref _elapsedText, value); }

    private string _currentFileText = "";
    public string CurrentFileText { get => _currentFileText; set => SetProperty(ref _currentFileText, value); }

    private string _pauseButtonText = "一時停止";
    public string PauseButtonText { get => _pauseButtonText; set => SetProperty(ref _pauseButtonText, value); }

    // ---------------- コマンド ----------------

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearSourcesCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    private void AddFiles()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "コピー元のファイルを選ぶ" };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "コピー元のフォルダーを選ぶ" };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FolderNames);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (string p in paths)
        {
            if (!Sources.Contains(p, StringComparer.OrdinalIgnoreCase))
                Sources.Add(p);
        }
    }

    private void RemoveSelected(object? parameter)
    {
        if (parameter is not System.Collections.IList list) return;
        foreach (object? o in list.Cast<object?>().ToList())
        {
            if (o is string s) Sources.Remove(s);
        }
    }

    private void BrowseDestination()
    {
        var dlg = new OpenFolderDialog { Title = "コピー先のフォルダーを選ぶ" };
        if (dlg.ShowDialog() == true) Destination = dlg.FolderName;
    }

    private void TogglePause()
    {
        if (_engine is null) return;
        if (_engine.IsPaused)
        {
            _engine.Resume();
            PauseButtonText = "一時停止";
            _clock.Start();
        }
        else
        {
            _engine.Pause();
            PauseButtonText = "再開";
            _clock.Stop();
            StatusText = "一時停止中";
        }
    }

    private void Cancel() => _cts?.Cancel();

    // ---------------- 実行 ----------------

    private async Task StartAsync()
    {
        if (Sources.Count == 0)
        {
            MessageBox.Show("コピー元を追加してください。", "Hayate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(Destination))
        {
            MessageBox.Show("コピー先を指定してください。", "Hayate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var options = BuildOptions();

        if (options.Mode == CopyMode.Move && !options.DryRun)
        {
            var answer = MessageBox.Show(
                "移動モードです。検証に成功したファイルのみ、コピー元から削除されます。\n実行しますか？",
                "Hayate", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        LogLines.Clear();
        _smoothedSpeed = 0;
        _lastBytes = 0;
        _lastTick = TimeSpan.Zero;
        OverallPercent = 0;
        CurrentFileText = "";
        ElapsedText = "経過 0.0秒";
        StatusText = "準備中…";
        PauseButtonText = "一時停止";

        _cts = new CancellationTokenSource();
        var engine = new CopyEngine(options);
        var token = _cts.Token;
        _engine = engine;
        engine.Log += OnEngineLog;

        IsRunning = true;
        _clock.Restart();
        _timer.Start();

        CopyResult result;
        try
        {
            result = await Task.Run(() => engine.RunAsync(token), token);
        }
        catch (OperationCanceledException)
        {
            result = new CopyResult { Canceled = true, Elapsed = _clock.Elapsed };
        }
        catch (Exception ex)
        {
            AppendLog("[エラー] " + ex.Message);
            result = new CopyResult { Elapsed = _clock.Elapsed, Errors = { ex.Message } };
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
            IsRunning = false;
            engine.Log -= OnEngineLog;
        }

        UpdateProgress();
        ElapsedText = "経過 " + PathUtil.FormatDuration(result.Elapsed);
        ReportFinish(result);
        NotifyFinished(result, options);

        _engine = null;
        engine.Dispose();
        _cts?.Dispose();
        _cts = null;
    }

    private CopyOptions BuildOptions()
    {
        int[] parallels = { 1, 2, 4, 8, 16 };
        int[] buffers =
        {
            256 * 1024, 1024 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024, 64 * 1024 * 1024
        };

        var opt = new CopyOptions
        {
            Destination = Destination.Trim(),
            Mode = ModeIndex == 1 ? CopyMode.Move : CopyMode.Copy,
            Conflict = (ConflictPolicy)ConflictIndex,
            Verify = (VerifyLevel)VerifyIndex,
            Parallelism = parallels[Math.Clamp(ParallelIndex, 0, parallels.Length - 1)],
            BufferSize = buffers[Math.Clamp(BufferIndex, 0, buffers.Length - 1)],
            PreserveTimestamps = PreserveTimestamps,
            PreserveAttributes = PreserveAttributes,
            FlushToDisk = FlushToDisk,
            SkipIdentical = SkipIdentical,
            UseJournal = UseJournal,
            CheckFreeSpace = CheckFreeSpace,
            DryRun = DryRun,
            ExcludePatterns = ExcludePatterns
        };
        opt.Sources.AddRange(Sources);
        return opt;
    }

    private void OnEngineLog(string line)
        => Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(line));

    private void AppendLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        if (LogLines.Count > 2000) LogLines.RemoveAt(0);
    }

    private void UpdateProgress()
    {
        var engine = _engine;
        if (engine is null) return;

        var s = engine.Stats;
        long total = Interlocked.Read(ref s.TotalBytes);
        long done = Interlocked.Read(ref s.DoneBytes);
        long files = Interlocked.Read(ref s.TotalFiles);
        long doneFiles = Interlocked.Read(ref s.DoneFiles);
        long skipped = Interlocked.Read(ref s.SkippedFiles);
        long failed = Interlocked.Read(ref s.FailedFiles);

        OverallPercent = total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : 0;

        TimeSpan now = _clock.Elapsed;
        double dt = (now - _lastTick).TotalSeconds;
        if (dt >= 0.15)
        {
            double instant = (done - _lastBytes) / dt;
            _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.7 + instant * 0.3;
            _lastBytes = done;
            _lastTick = now;
        }

        SpeedText = _smoothedSpeed > 0 ? PathUtil.FormatSpeed(_smoothedSpeed) : "-";
        ElapsedText = "経過 " + PathUtil.FormatDuration(now);

        string cur = s.CurrentFile;
        CurrentFileText = string.IsNullOrEmpty(cur) ? "" : PathUtil.Shorten(cur, 80);

        if (IsRunning && !engine.IsPaused)
        {
            StatusText = $"{doneFiles + skipped:N0} / {files:N0} 件   " +
                         $"{PathUtil.FormatBytes(done)} / {PathUtil.FormatBytes(total)}" +
                         (failed > 0 ? $"   失敗 {failed:N0}" : "");
        }
    }

    private void NotifyFinished(CopyResult r, CopyOptions options)
    {
        if (NotifyWithSound)
        {
            if (r.Canceled) MelodyPlayer.PlayCanceled();
            else if (r.Failed > 0) MelodyPlayer.PlayWarning();
            else MelodyPlayer.PlaySuccess();
        }

        if (!NotifyWithPopup) return;

        var owner = Application.Current?.MainWindow;
        var dialog = new CompletionWindow(r, options.Destination, options.DryRun);
        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            // 他のアプリを触っていてもタスクバーで気づけるようにする
            if (!owner.IsActive) owner.Activate();
        }
        dialog.ShowDialog();
    }

    private void ReportFinish(CopyResult r)
    {
        double mbps = r.Elapsed.TotalSeconds > 0 ? r.Bytes / r.Elapsed.TotalSeconds : 0;

        AppendLog(r.Canceled
            ? "── 中止しました ──"
            : "── 完了 ──");
        AppendLog($"成功 {r.Copied:N0} 件 / 飛ばした {r.Skipped:N0} 件 / 失敗 {r.Failed:N0} 件");
        AppendLog($"転送量 {PathUtil.FormatBytes(r.Bytes)}   所要 {PathUtil.FormatDuration(r.Elapsed)}   " +
                  $"平均 {PathUtil.FormatSpeed(mbps)}");

        if (r.Errors.Count > 0)
        {
            AppendLog($"エラー {r.Errors.Count:N0} 件:");
            foreach (string e in r.Errors.Take(50)) AppendLog("  " + e);
            if (r.Errors.Count > 50) AppendLog($"  …ほか {r.Errors.Count - 50:N0} 件");
        }

        StatusText = r.Canceled
            ? "中止しました"
            : r.Failed > 0
                ? $"完了（失敗 {r.Failed:N0} 件）"
                : "完了";

        CurrentFileText = "";
        if (!r.Canceled && r.Failed == 0) OverallPercent = 100;
    }
}
