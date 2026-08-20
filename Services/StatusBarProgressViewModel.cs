using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MovieConcatenater.Services;

/// <summary>
/// ウィンドウ下部ステータスバーにバインドする進捗表示用 ViewModel。
/// IndigoMovieManager の StatusBarProgressViewModel を参考。
/// </summary>
public sealed class StatusBarProgressViewModel : INotifyPropertyChanged
{
    public const double MessageFontSize = 12d;

    private bool _isActive;
    private string _statusText = "準備完了";
    private double _progressPercent;
    private bool _showProgress;
    private bool _showCancel;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value ?? "");
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    public bool ShowProgress
    {
        get => _showProgress;
        set => SetField(ref _showProgress, value);
    }

    public bool ShowCancel
    {
        get => _showCancel;
        set => SetField(ref _showCancel, value);
    }

    public void SetIdle(string message = "準備完了")
    {
        IsActive = false;
        StatusText = message;
        ProgressPercent = 0;
        ShowProgress = false;
        ShowCancel = false;
    }

    public void Begin(string message)
    {
        IsActive = true;
        StatusText = message;
        ProgressPercent = 0;
        ShowProgress = true;
        ShowCancel = true;
    }

    public void Report(double percent, string message)
    {
        ProgressPercent = Math.Clamp(percent, 0, 100);
        StatusText = message;
        ShowProgress = true;
        ShowCancel = true;
        IsActive = true;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
