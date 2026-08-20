using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MovieConcatenater.Models;
using MovieConcatenater.Services;

namespace MovieConcatenater;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".flv", ".webm", ".mpeg", ".mpg", ".ts", ".m2ts",
    };

    private readonly ObservableCollection<VideoFileItem> _files = new();
    private readonly StatusBarProgressViewModel _status = new();
    private CancellationTokenSource? _runCts;
    private bool _syncingOutputPaths;
    private bool _userEditedFileName;

    public MainWindow()
    {
        InitializeComponent();
        FileListView.ItemsSource = _files;
        OperationStatusBar.DataContext = _status;
        _status.SetIdle("準備完了 — ファイルをドロップしてください");
    }

    private bool IsBusy => _runCts is not null;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (IsBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        var videoPaths = OutputNameHelper.OrderPathsNaturally(
                paths
                    .Where(p => File.Exists(p) && VideoExtensions.Contains(Path.GetExtension(p)))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (videoPaths.Count == 0)
        {
            MessageBox.Show(this, "対応する動画ファイルが見つかりませんでした。", "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddFilesAsync(videoPaths);
    }

    private async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (!FfmpegLocator.TryResolveFfprobe(FfmpegPathBox.Text, out string ffprobe, out string resolveError))
        {
            MessageBox.Show(this, resolveError, "MovieConcatenater", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool wasEmpty = _files.Count == 0;
        var added = new List<VideoFileItem>();

        foreach (string path in paths)
        {
            if (_files.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new VideoFileItem
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                IsAnalyzing = true,
            };
            _files.Add(item);
            added.Add(item);
        }

        Renumber();

        if (added.Count == 0)
        {
            return;
        }

        if (wasEmpty || !_userEditedFileName)
        {
            ApplySuggestedOutputName();
        }

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text) && added.Count > 0)
        {
            SetOutputFolder(Path.GetDirectoryName(added[0].FullPath) ?? "");
        }

        _status.StatusText = $"解析中… ({added.Count} ファイル)";

        foreach (var item in added)
        {
            await FfprobeService.AnalyzeAsync(item, ffprobe);
        }

        _status.SetIdle($"リスト: {_files.Count} ファイル");
    }

    private void Renumber()
    {
        for (int i = 0; i < _files.Count; i++)
        {
            _files[i].Order = i + 1;
        }
    }

    private void ApplySuggestedOutputName()
    {
        var paths = _files.Select(f => f.FullPath).ToList();
        string name = OutputNameHelper.SuggestDefaultFileName(paths);
        _userEditedFileName = false;
        SetOutputFileName(name);
    }

    private void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "出力フォルダを選択",
        };

        if (!string.IsNullOrWhiteSpace(OutputFolderBox.Text) && Directory.Exists(OutputFolderBox.Text))
        {
            dialog.InitialDirectory = OutputFolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SetOutputFolder(dialog.FolderName);
        }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe を選択",
            Filter = "ffmpeg|ffmpeg.exe|実行ファイル|*.exe|すべて|*.*",
            FileName = "ffmpeg.exe",
        };

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegPathBox.Text = dialog.FileName;
        }
    }

    private void OutputPathFields_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingOutputPaths)
        {
            return;
        }

        if (ReferenceEquals(sender, OutputFileNameBox))
        {
            _userEditedFileName = true;
        }

        SyncFullPathFromParts();
    }

    private void OutputFullPathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string full = OutputFullPathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(full))
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                SetOutputFolder(dir);
            }

            if (!string.IsNullOrEmpty(name))
            {
                _userEditedFileName = true;
                SetOutputFileName(name);
            }
        }
        catch
        {
            // 不正パスはそのまま
        }
    }

    private void SetOutputFolder(string folder)
    {
        _syncingOutputPaths = true;
        try
        {
            OutputFolderBox.Text = folder;
            SyncFullPathFromParts();
        }
        finally
        {
            _syncingOutputPaths = false;
        }
    }

    private void SetOutputFileName(string fileName)
    {
        _syncingOutputPaths = true;
        try
        {
            OutputFileNameBox.Text = fileName;
            SyncFullPathFromParts();
        }
        finally
        {
            _syncingOutputPaths = false;
        }
    }

    private void SyncFullPathFromParts()
    {
        bool previous = _syncingOutputPaths;
        _syncingOutputPaths = true;
        try
        {
            string folder = OutputFolderBox.Text.Trim().Trim('"');
            string name = OutputFileNameBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name))
            {
                OutputFullPathBox.Text = "";
                return;
            }

            OutputFullPathBox.Text = Path.Combine(folder, name);
        }
        catch
        {
            OutputFullPathBox.Text = "";
        }
        finally
        {
            _syncingOutputPaths = previous;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        _files.Clear();
        _userEditedFileName = false;
        SetOutputFileName("");
        _status.SetIdle("準備完了 — ファイルをドロップしてください");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBusy)
        {
            return;
        }

        _runCts?.Cancel();
        _status.StatusText = "中止要求中…";
        StatusCancelButton.IsEnabled = false;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        CompatibilityResult compatibility;
        try
        {
            compatibility = ConcatService.CheckCompatibility(_files.ToList());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "検証エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            _status.SetIdle("検証エラー");
            return;
        }

        ConcatMode mode = ConcatMode.Copy;
        if (!compatibility.IsCompatible)
        {
            string confirmMessage = ConcatService.BuildReencodeConfirmMessage(compatibility);
            MessageBoxResult confirm = MessageBox.Show(
                this,
                confirmMessage,
                "形式不一致 — 再エンコード確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                _status.SetIdle("キャンセルしました");
                return;
            }

            mode = ConcatMode.Reencode;
        }

        string outputPath = OutputFullPathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "出力パスを指定してください。", "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!FfmpegLocator.TryResolveFfmpeg(FfmpegPathBox.Text, out string ffmpeg, out string ffmpegError))
        {
            MessageBox.Show(this, ffmpegError, "MovieConcatenater", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _runCts = new CancellationTokenSource();
        SetRunningUi(true);
        _status.Begin(mode == ConcatMode.Copy
            ? "連結を開始しています…"
            : "再エンコード連結を開始しています…");

        var progress = new Progress<ConcatProgress>(p =>
        {
            _status.Report(p.Percent, p.Message);
        });

        try
        {
            await ConcatService.ConcatenateAsync(
                _files.ToList(),
                ffmpeg,
                outputPath,
                mode,
                progress,
                _runCts.Token);

            _status.SetIdle("完了: " + outputPath);
            MessageBox.Show(this, "連結が完了しました。\n" + outputPath, "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.SetIdle("キャンセルしました");
        }
        catch (Exception ex)
        {
            _status.SetIdle("失敗");
            MessageBox.Show(this, ex.Message, "連結エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetRunningUi(false);
        }
    }

    private void SetRunningUi(bool running)
    {
        // 操作可能なのはステータスバーの「中止」のみ
        MainContentPanel.IsEnabled = !running;
        OptionsPanel.IsEnabled = !running;
        ActionPanel.IsEnabled = !running;
        AllowDrop = !running;
        StatusCancelButton.IsEnabled = running;
    }
}
