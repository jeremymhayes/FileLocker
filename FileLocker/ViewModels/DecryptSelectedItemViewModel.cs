using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FileLocker;

public sealed class DecryptSelectedItemViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _detail;
    private double _progressPercent;
    private bool _isProgressVisible;
    private string _progressStatus = "Pending";

    public DecryptSelectedItemViewModel(
        string fullPath,
        string sourceRootPath,
        bool sourceRootIsFolder,
        long sizeBytes,
        bool isSupportedEncryptedFile,
        string status,
        string detail)
    {
        FullPath = fullPath;
        SourceRootPath = sourceRootPath;
        SourceRootIsFolder = sourceRootIsFolder;
        SizeBytes = sizeBytes;
        IsSupportedEncryptedFile = isSupportedEncryptedFile;
        DisplayName = Path.GetFileName(fullPath);
        ItemType = GetItemType(fullPath, isSupportedEncryptedFile);
        SizeDisplay = FormatFileSize(sizeBytes);
        _status = status;
        _detail = detail;
    }

    public string DisplayName { get; }

    public string FullPath { get; }

    public string SourceRootPath { get; }

    public bool SourceRootIsFolder { get; }

    public long SizeBytes { get; }

    public string ItemType { get; }

    public string SizeDisplay { get; }

    public bool IsSupportedEncryptedFile { get; }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(CanDecrypt));
            }
        }
    }

    public string StatusDisplay => Status;

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (SetField(ref _isProgressVisible, value))
            {
                OnPropertyChanged(nameof(ProgressVisibility));
            }
        }
    }

    public Visibility ProgressVisibility => IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

    public string ProgressStatus
    {
        get => _progressStatus;
        set => SetField(ref _progressStatus, value);
    }

    public bool CanDecrypt =>
        IsSupportedEncryptedFile &&
        !string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase);

    public Brush StatusBrush
    {
        get
        {
            if (!IsSupportedEncryptedFile ||
                string.Equals(Status, "Unsupported", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Colors.IndianRed);
            }

            if (string.Equals(Status, "Processing", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Colors.DeepSkyBlue);
            }

            if (string.Equals(Status, "Waiting", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Status, "Password required", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Colors.Orange);
            }

            return new SolidColorBrush(Colors.MediumAquamarine);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetReady(string detail = "Ready to decrypt.")
    {
        Status = "Ready";
        Detail = detail;
        IsProgressVisible = false;
        ProgressPercent = 0;
        ProgressStatus = "Pending";
    }

    public void SetPasswordRequired()
    {
        Status = "Password required";
        Detail = "Enter the password used when this file was encrypted.";
    }

    public void SetProcessing()
    {
        Status = "Processing";
        Detail = "Decrypting this encrypted file.";
        IsProgressVisible = true;
        ProgressPercent = Math.Max(ProgressPercent, 2);
        ProgressStatus = $"{ProgressPercent:0}%";
    }

    public void SetCompleted(string detail)
    {
        Status = "Completed";
        Detail = detail;
        IsProgressVisible = true;
        ProgressPercent = 100;
        ProgressStatus = "100%";
    }

    public void SetFailed(string detail)
    {
        Status = "Failed";
        Detail = detail;
        ProgressStatus = "Issue";
    }

    public void SetCancelled(string detail)
    {
        Status = "Cancelled";
        Detail = detail;
        if (!IsProgressVisible)
        {
            ProgressPercent = 0;
        }

        ProgressStatus = "Cancelled";
    }

    public void UpdateProgress(double percent, string? status = null)
    {
        IsProgressVisible = true;
        ProgressPercent = Math.Clamp(percent, 0, 100);
        ProgressStatus = status ?? $"{ProgressPercent:0}%";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double length = bytes;
        int order = 0;
        while (length >= 1024 && order < sizes.Length - 1)
        {
            order++;
            length /= 1024;
        }

        return $"{length:0.##} {sizes[order]}";
    }

    private static string GetItemType(string path, bool isSupported)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!isSupported)
        {
            return string.IsNullOrWhiteSpace(extension)
                ? "Unsupported File"
                : $"{extension.TrimStart('.').ToUpperInvariant()} File";
        }

        return extension switch
        {
            ".locked" => "LOCKED File",
            ".png" => "PNG Payload",
            _ => "Encrypted File"
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
