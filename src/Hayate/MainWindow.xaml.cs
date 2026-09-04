using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Hayate.ViewModels;

namespace Hayate;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ((INotifyCollectionChanged)Vm.LogLines).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
        };
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && !Vm.IsRunning;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (Vm.IsRunning) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            Vm.AddPaths(paths);
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm.IsRunning)
        {
            var answer = MessageBox.Show(
                "コピー中です。中止して閉じますか？",
                "Hayate", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            Vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
