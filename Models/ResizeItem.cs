using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MovieConcatenater.Services;

namespace MovieConcatenater.Models;

public enum ResizeJobState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>解像度変更タブの 1 行。元ファイル情報と、行ごとの出力設定・処理状態を持つ。</summary>
public sealed class ResizeItem : INotifyPropertyChanged
{
    private ResolutionPreset _preset;
    private FpsOption _fps;
    private string _outputFolder = "";
    private string _outputFileName = "";
    private ResizeJobState _state = ResizeJobState.Waiting;
    private string _errorMessage = "";

    public ResizeItem(VideoFileItem source, ResolutionPreset preset, FpsOption fps, string outputFolder)
    {
        Source = source;
        _preset = preset;
        _fps = fps;
        _outputFolder = outputFolder;
        _outputFileName = ResizeService.SuggestOutputFileName(source.FullPath, preset);
        Source.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OutputSizeText));
            OnPropertyChanged(nameof(StatusText));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VideoFileItem Source { get; }

    public bool UserEditedFileName { get; set; }

    public ResolutionPreset Preset
    {
        get => _preset;
        set
        {
            if (!SetField(ref _preset, value))
            {
                return;
            }

            if (!UserEditedFileName)
            {
                OutputFileName = ResizeService.SuggestOutputFileName(Source.FullPath, value);
            }

            OnPropertyChanged(nameof(OutputSizeText));
            ResetIfDone();
        }
    }

    public FpsOption Fps
    {
        get => _fps;
        set
        {
            if (SetField(ref _fps, value))
            {
                ResetIfDone();
            }
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetField(ref _outputFolder, value ?? ""))
            {
                OnPropertyChanged(nameof(OutputFullPath));
                ResetIfDone();
            }
        }
    }

    public string OutputFileName
    {
        get => _outputFileName;
        set
        {
            if (SetField(ref _outputFileName, value ?? ""))
            {
                OnPropertyChanged(nameof(OutputFullPath));
                ResetIfDone();
            }
        }
    }

    public string OutputFullPath
    {
        get
        {
            string folder = OutputFolder.Trim().Trim('"');
            string name = OutputFileName.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            try
            {
                return Path.Combine(folder, name);
            }
            catch
            {
                return "";
            }
        }
    }

    public string OutputSizeText
    {
        get
        {
            if (Source.IsAnalyzing || Source.Width <= 0 || Source.Height <= 0)
            {
                return "";
            }

            var (w, h) = ResizeService.CalculateOutputSize(Source, Preset);
            return $"{w}×{h}";
        }
    }

    public ResizeJobState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetField(ref _errorMessage, value ?? ""))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText
    {
        get
        {
            if (Source.IsAnalyzing || !string.IsNullOrEmpty(Source.Error))
            {
                return Source.StatusText;
            }

            return State switch
            {
                ResizeJobState.Running => "処理中…",
                ResizeJobState.Done => "完了",
                ResizeJobState.Failed => "失敗: " + ErrorMessage,
                _ => "待機",
            };
        }
    }

    private void ResetIfDone()
    {
        if (State == ResizeJobState.Done)
        {
            State = ResizeJobState.Waiting;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
