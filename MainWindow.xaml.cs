using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MovieConcatenater.Models;
using MovieConcatenater.Services;

namespace MovieConcatenater;

public partial class MainWindow : Window
{
    private const string IdleMessage = "準備完了 — ファイルをドロップしてください";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".flv", ".webm", ".mpeg", ".mpg", ".ts", ".m2ts",
    };

    private sealed class ConcatOutputState
    {
        public string Folder { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool UserEditedFileName { get; set; }
    }

    private readonly ObservableCollection<VideoFileItem> _files = new();
    private readonly ObservableCollection<ResizeItem> _resizeItems = new();
    private readonly StatusBarProgressViewModel _status = new();
    private readonly ConcatOutputState _concatOutput = new();
    private ResolutionPreset _defaultPreset = ResizeService.ResolutionPresets[1];
    private FpsOption _defaultFps = ResizeService.FpsOptions[0];
    private string _resizeDefaultFolder = "";
    private CancellationTokenSource? _runCts;
    private bool _syncingOutputPaths;
    private bool _syncingCombos;

    public MainWindow()
    {
        InitializeComponent();
        FileListView.ItemsSource = _files;
        ResizeFileListView.ItemsSource = _resizeItems;
        ResolutionCombo.ItemsSource = ResizeService.ResolutionPresets;
        FpsCombo.ItemsSource = ResizeService.FpsOptions;
        RefreshResizeCombos();
        OperationStatusBar.DataContext = _status;
        _status.SetIdle(IdleMessage);
    }

    private bool IsBusy => _runCts is not null;

    private bool IsResizeTabActive => ReferenceEquals(MainTabs.SelectedItem, ResizeTab);

    private List<ResizeItem> SelectedResizeItems() =>
        ResizeFileListView.SelectedItems.Cast<ResizeItem>().ToList();

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 子の ListView / ComboBox の SelectionChanged もバブルしてくるので除外
        if (!ReferenceEquals(e.Source, MainTabs) || !IsLoaded)
        {
            return;
        }

        if (IsResizeTabActive)
        {
            _concatOutput.Folder = OutputFolderBox.Text;
            _concatOutput.FileName = OutputFileNameBox.Text;
            RefreshResizeOutputFields();
        }
        else
        {
            OutputFileNameBox.IsEnabled = true;
            OutputFullPathBox.IsEnabled = true;
            _syncingOutputPaths = true;
            try
            {
                OutputFolderBox.Text = _concatOutput.Folder;
                OutputFileNameBox.Text = _concatOutput.FileName;
                SyncFullPathFromParts();
            }
            finally
            {
                _syncingOutputPaths = false;
            }
        }

        if (!IsBusy)
        {
            _status.SetIdle(IdleMessage);
        }
    }

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

        if (IsResizeTabActive)
        {
            await AddResizeFilesAsync(videoPaths);
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

        if (wasEmpty || !_concatOutput.UserEditedFileName)
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

    private async Task AddResizeFilesAsync(IReadOnlyList<string> paths)
    {
        if (!FfmpegLocator.TryResolveFfprobe(FfmpegPathBox.Text, out string ffprobe, out string resolveError))
        {
            MessageBox.Show(this, resolveError, "MovieConcatenater", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var added = new List<ResizeItem>();
        foreach (string path in paths)
        {
            if (_resizeItems.Any(r => string.Equals(r.Source.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var source = new VideoFileItem
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                IsAnalyzing = true,
            };
            string folder = string.IsNullOrWhiteSpace(_resizeDefaultFolder)
                ? Path.GetDirectoryName(path) ?? ""
                : _resizeDefaultFolder;
            var row = new ResizeItem(source, _defaultPreset, _defaultFps, folder);
            _resizeItems.Add(row);
            added.Add(row);
        }

        if (added.Count == 0)
        {
            return;
        }

        RefreshResizeOutputFields();
        _status.StatusText = $"解析中… ({added.Count} ファイル)";

        foreach (var row in added)
        {
            await FfprobeService.AnalyzeAsync(row.Source, ffprobe);
        }

        _status.SetIdle($"リスト: {_resizeItems.Count} ファイル");
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
        _concatOutput.UserEditedFileName = false;
        SetOutputFileName(name);
    }

    private void ResizeFileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshResizeCombos();
        RefreshResizeOutputFields();
    }

    private void ResizeFileListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || IsBusy)
        {
            return;
        }

        foreach (var row in SelectedResizeItems())
        {
            _resizeItems.Remove(row);
        }

        e.Handled = true;
        RefreshResizeCombos();
        RefreshResizeOutputFields();
    }

    /// <summary>上段のドロップダウンを、選択行（未選択なら既定値）の設定に合わせる。</summary>
    private void RefreshResizeCombos()
    {
        var selected = SelectedResizeItems();
        _syncingCombos = true;
        try
        {
            if (selected.Count == 0)
            {
                ResolutionCombo.SelectedItem = _defaultPreset;
                FpsCombo.SelectedItem = _defaultFps;
                return;
            }

            var presets = selected.Select(r => r.Preset).Distinct().ToList();
            var fpsList = selected.Select(r => r.Fps).Distinct().ToList();
            ResolutionCombo.SelectedItem = presets.Count == 1 ? presets[0] : null;
            FpsCombo.SelectedItem = fpsList.Count == 1 ? fpsList[0] : null;
        }
        finally
        {
            _syncingCombos = false;
        }
    }

    /// <summary>下段の出力欄を、選択行（未選択なら既定フォルダ）の内容に合わせる。</summary>
    private void RefreshResizeOutputFields()
    {
        if (!IsResizeTabActive)
        {
            return;
        }

        var selected = SelectedResizeItems();
        _syncingOutputPaths = true;
        try
        {
            switch (selected.Count)
            {
                case 0:
                    OutputFolderBox.Text = _resizeDefaultFolder;
                    OutputFileNameBox.Text = "";
                    OutputFullPathBox.Text = "";
                    break;
                case 1:
                    OutputFolderBox.Text = selected[0].OutputFolder;
                    OutputFileNameBox.Text = selected[0].OutputFileName;
                    OutputFullPathBox.Text = selected[0].OutputFullPath;
                    break;
                default:
                    var folders = selected.Select(r => r.OutputFolder)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    OutputFolderBox.Text = folders.Count == 1 ? folders[0] : "";
                    OutputFileNameBox.Text = "";
                    OutputFullPathBox.Text = "";
                    break;
            }

            OutputFileNameBox.IsEnabled = selected.Count == 1;
            OutputFullPathBox.IsEnabled = selected.Count == 1;
        }
        finally
        {
            _syncingOutputPaths = false;
        }
    }

    private void ResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombos || ResolutionCombo.SelectedItem is not ResolutionPreset preset)
        {
            return;
        }

        var selected = SelectedResizeItems();
        if (selected.Count == 0)
        {
            _defaultPreset = preset;
            return;
        }

        foreach (var row in selected)
        {
            row.Preset = preset;
        }

        RefreshResizeOutputFields();
    }

    private void FpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombos || FpsCombo.SelectedItem is not FpsOption fps)
        {
            return;
        }

        var selected = SelectedResizeItems();
        if (selected.Count == 0)
        {
            _defaultFps = fps;
            return;
        }

        foreach (var row in selected)
        {
            row.Fps = fps;
        }
    }

    private void ApplyResizeFolderEdit(string folder)
    {
        var selected = SelectedResizeItems();
        if (selected.Count == 0)
        {
            _resizeDefaultFolder = folder;
            return;
        }

        foreach (var row in selected)
        {
            row.OutputFolder = folder;
        }

        if (selected.Count == 1)
        {
            SetFullPathBoxSilently(selected[0].OutputFullPath);
        }
    }

    private void ApplyResizeFileNameEdit(string fileName)
    {
        var selected = SelectedResizeItems();
        if (selected.Count != 1)
        {
            return;
        }

        selected[0].OutputFileName = fileName;
        selected[0].UserEditedFileName = true;
        SetFullPathBoxSilently(selected[0].OutputFullPath);
    }

    private void SetFullPathBoxSilently(string text)
    {
        _syncingOutputPaths = true;
        try
        {
            OutputFullPathBox.Text = text;
        }
        finally
        {
            _syncingOutputPaths = false;
        }
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
            if (IsResizeTabActive)
            {
                SetOutputFolder(dialog.FolderName);
                ApplyResizeFolderEdit(dialog.FolderName);
                return;
            }

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

        if (IsResizeTabActive)
        {
            if (ReferenceEquals(sender, OutputFolderBox))
            {
                ApplyResizeFolderEdit(OutputFolderBox.Text.Trim().Trim('"'));
            }
            else if (ReferenceEquals(sender, OutputFileNameBox))
            {
                ApplyResizeFileNameEdit(OutputFileNameBox.Text.Trim().Trim('"'));
            }

            return;
        }

        if (ReferenceEquals(sender, OutputFileNameBox))
        {
            _concatOutput.UserEditedFileName = true;
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

        if (IsResizeTabActive)
        {
            ApplyResizeFullPathEdit(full);
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
                _concatOutput.UserEditedFileName = true;
                SetOutputFileName(name);
            }
        }
        catch
        {
            // 不正パスはそのまま
        }
    }

    private void ApplyResizeFullPathEdit(string full)
    {
        var selected = SelectedResizeItems();
        if (selected.Count != 1 ||
            string.Equals(full, selected[0].OutputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                selected[0].OutputFolder = dir;
            }

            if (!string.IsNullOrEmpty(name))
            {
                selected[0].OutputFileName = name;
                selected[0].UserEditedFileName = true;
            }
        }
        catch
        {
            // 不正パスはそのまま
        }

        RefreshResizeOutputFields();
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
        if (IsResizeTabActive)
        {
            return;
        }

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

        if (IsResizeTabActive)
        {
            _resizeItems.Clear();
            RefreshResizeCombos();
            RefreshResizeOutputFields();
            _status.SetIdle(IdleMessage);
            return;
        }

        _files.Clear();
        _concatOutput.UserEditedFileName = false;
        SetOutputFileName("");
        _status.SetIdle(IdleMessage);
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

        if (IsResizeTabActive)
        {
            await RunResizeBatchAsync();
        }
        else
        {
            await RunConcatAsync();
        }
    }

    private async Task RunConcatAsync()
    {
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

    private async Task RunResizeBatchAsync()
    {
        if (_resizeItems.Count == 0)
        {
            MessageBox.Show(this, "動画ファイルをドロップしてください。", "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = _resizeItems.Where(r => r.State != ResizeJobState.Done).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "処理対象がありません（すべて完了済みです）。", "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var errors = ResizeService.ValidateBatch(targets);
        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "次の問題があるため開始できません。\n\n" + FormatLines(errors),
                "検証エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            _status.SetIdle("検証エラー");
            return;
        }

        if (!FfmpegLocator.TryResolveFfmpeg(FfmpegPathBox.Text, out string ffmpeg, out string ffmpegError))
        {
            MessageBox.Show(this, ffmpegError, "MovieConcatenater", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var row in targets)
        {
            row.State = ResizeJobState.Waiting;
            row.ErrorMessage = "";
        }

        _runCts = new CancellationTokenSource();
        CancellationToken token = _runCts.Token;
        SetRunningUi(true);
        _status.Begin($"解像度変更を開始しています… (0/{targets.Count})");

        int succeeded = 0;
        var failures = new List<string>();
        bool canceled = false;

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var row = targets[i];
                int index = i;
                row.State = ResizeJobState.Running;

                var progress = new Progress<ConcatProgress>(p =>
                {
                    double overall = (index + p.Percent / 100.0) / targets.Count * 100.0;
                    _status.Report(overall,
                        $"[{index + 1}/{targets.Count}] {row.Source.FileName} — {p.Percent:0.0}%");
                });

                try
                {
                    await ResizeService.ResizeAsync(
                        row.Source, row.Preset, row.Fps, ffmpeg, row.OutputFullPath, progress, token);
                    row.State = ResizeJobState.Done;
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    row.State = ResizeJobState.Waiting;
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    string message = ex.Message.Split('\n')[0];
                    row.ErrorMessage = message;
                    row.State = ResizeJobState.Failed;
                    failures.Add($"・{row.Source.FileName}: {ex.Message.Replace("\n", " ")}");
                }
            }
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetRunningUi(false);
            RefreshResizeOutputFields();
        }

        if (canceled)
        {
            _status.SetIdle($"キャンセルしました（完了 {succeeded} 本）");
            return;
        }

        if (failures.Count == 0)
        {
            _status.SetIdle($"完了: {succeeded} 本");
            MessageBox.Show(this, $"解像度変更が完了しました（{succeeded} 本）。", "MovieConcatenater",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _status.SetIdle($"完了: 成功 {succeeded} / 失敗 {failures.Count}");
        MessageBox.Show(this,
            $"成功 {succeeded} / 失敗 {failures.Count}\n\n" + FormatLines(failures),
            "解像度変更の結果", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatLines(IReadOnlyList<string> lines, int maxLines = 15)
    {
        var sb = new StringBuilder();
        foreach (string line in lines.Take(maxLines))
        {
            string shown = line.Length > 300 ? line[..300] + "…" : line;
            sb.AppendLine(shown);
        }

        if (lines.Count > maxLines)
        {
            sb.AppendLine($"…ほか {lines.Count - maxLines} 件");
        }

        return sb.ToString().TrimEnd();
    }

    private void SetRunningUi(bool running)
    {
        // 操作可能なのはステータスバーの「中止」のみ
        MainTabs.IsEnabled = !running;
        OptionsPanel.IsEnabled = !running;
        ActionPanel.IsEnabled = !running;
        AllowDrop = !running;
        StatusCancelButton.IsEnabled = running;
    }
}
