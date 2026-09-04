using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Hayate.Core;

namespace Hayate;

public partial class CompletionWindow : Window
{
    private readonly string _destination;

    public CompletionWindow(CopyResult result, string destination, bool dryRun)
    {
        InitializeComponent();
        _destination = destination;

        double bytesPerSecond = result.Elapsed.TotalSeconds > 0
            ? result.Bytes / result.Elapsed.TotalSeconds
            : 0;

        ElapsedValue.Text = PathUtil.FormatDuration(result.Elapsed);
        BytesValue.Text = PathUtil.FormatBytes(result.Bytes);
        SpeedValue.Text = bytesPerSecond > 0 ? PathUtil.FormatSpeed(bytesPerSecond) : "-";
        CountValue.Text = $"成功 {result.Copied:N0} 件 / 飛ばした {result.Skipped:N0} 件";
        FailedValue.Text = result.Failed > 0 ? $"{result.Failed:N0} 件" : "なし";
        FailedValue.Foreground = result.Failed > 0
            ? (Brush)Application.Current.Resources["Err"]
            : (Brush)Application.Current.Resources["FgDim"];

        if (result.Canceled)
        {
            Title = "Hayate — 中止";
            HeadlineText.Text = "中止しました";
            HeadlineText.Foreground = (Brush)Application.Current.Resources["Warn"];
            SubText.Text = "書きかけの一時ファイルは削除済みです。" +
                           "記録が有効なら、次回は済んだ分を飛ばして続きから再開できます。";
        }
        else if (result.Failed > 0)
        {
            Title = "Hayate — 完了（失敗あり）";
            HeadlineText.Text = "失敗したファイルがあります";
            HeadlineText.Foreground = (Brush)Application.Current.Resources["Warn"];
            SubText.Text = "失敗したファイルはコピー先に書き込まれていません。" +
                           "詳しい内容はメイン画面のログを確認してください。";
        }
        else if (dryRun)
        {
            Title = "Hayate — ドライラン完了";
            HeadlineText.Text = "ドライランが終わりました";
            HeadlineText.Foreground = (Brush)Application.Current.Resources["Accent"];
            SubText.Text = "何も書き込んでいません。対象の一覧はログで確認できます。";
        }
        else
        {
            HeadlineText.Text = "コピーが完了しました";
            SubText.Text = "すべてのファイルが検証に成功し、正しく書き込まれました。";
        }

        OpenFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(destination) &&
                                     Directory.Exists(PathUtil.Ext(destination));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + PathUtil.Plain(_destination) + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "フォルダーを開けませんでした。\n" + ex.Message,
                "Hayate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 他のアプリを操作していても気づけるよう、いったん最前面に出してから通常の重なり順に戻す
        Activate();
        Dispatcher.BeginInvoke(new Action(() => Topmost = false),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
