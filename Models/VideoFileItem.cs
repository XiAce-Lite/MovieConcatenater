using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MovieConcatenater.Models;

public sealed class VideoFileItem : INotifyPropertyChanged
{
    private int _order;
    private string _fullPath = "";
    private string _fileName = "";
    private string _resolution = "";
    private string _videoCodec = "";
    private string _audioCodec = "";
    private string _frameRate = "";
    private string _durationText = "";
    private string _error = "";
    private TimeSpan _duration;
    private int _width;
    private int _height;
    private bool _hasAudio;
    private int _audioSampleRate;
    private int _audioChannels;
    private string _pixelFormat = "";
    private bool _isAnalyzing = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Order
    {
        get => _order;
        set => SetField(ref _order, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => SetField(ref _fullPath, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public string StatusText
    {
        get
        {
            if (IsAnalyzing)
            {
                return "解析中…";
            }

            if (!string.IsNullOrEmpty(Error))
            {
                return Error;
            }

            return "OK";
        }
    }

    public string Resolution
    {
        get => _resolution;
        set => SetField(ref _resolution, value);
    }

    public string VideoCodec
    {
        get => _videoCodec;
        set => SetField(ref _videoCodec, value);
    }

    public string AudioCodec
    {
        get => _audioCodec;
        set => SetField(ref _audioCodec, value);
    }

    public string FrameRate
    {
        get => _frameRate;
        set => SetField(ref _frameRate, value);
    }

    public string DurationText
    {
        get => _durationText;
        set => SetField(ref _durationText, value);
    }

    public string Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public bool HasAudio
    {
        get => _hasAudio;
        set => SetField(ref _hasAudio, value);
    }

    public int AudioSampleRate
    {
        get => _audioSampleRate;
        set => SetField(ref _audioSampleRate, value);
    }

    public int AudioChannels
    {
        get => _audioChannels;
        set => SetField(ref _audioChannels, value);
    }

    public string PixelFormat
    {
        get => _pixelFormat;
        set => SetField(ref _pixelFormat, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set
        {
            if (SetField(ref _isAnalyzing, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }
    }

    public string CompatibilityKey =>
        $"{Width}x{Height}|{VideoCodec}|{AudioCodec}|{FrameRate}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
