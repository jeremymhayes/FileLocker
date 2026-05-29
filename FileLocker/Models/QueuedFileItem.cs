using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace FileLocker;

public sealed class QueuedFileItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private string _detailSummary = "Ready to process.";
    private string _pathCaption;
    private string _predictedOutputPath = string.Empty;
    private double _progressPercent;
    private bool _isProgressVisible;
    private string _progressStatus = "Pending";

    internal QueuedFileItem(string sourcePath, string sourceRootPath, bool sourceRootIsFolder, long sizeBytes)
    {
        SourcePath = sourcePath;
        SourceRootPath = sourceRootPath;
        SourceRootIsFolder = sourceRootIsFolder;
        SizeBytes = sizeBytes;
        DisplayName = $"{Path.GetFileName(sourcePath)} ({FormatFileSize(sizeBytes)})";
        FileName = Path.GetFileName(sourcePath);
        ItemType = GetItemType(sourcePath);
        SizeDisplay = FormatFileSize(sizeBytes);
        _pathCaption = sourceRootIsFolder
            ? $"From folder: {sourceRootPath}"
            : $"From: {Path.GetDirectoryName(sourcePath) ?? sourcePath}";
    }

    public string SourcePath { get; }

    public string SourceRootPath { get; }

    public bool SourceRootIsFolder { get; }

    public long SizeBytes { get; }

    public string DisplayName { get; }

    public string FileName { get; }

    public string ItemType { get; }

    public string SizeDisplay { get; }

    public string RootSelectionCaption => SourceRootIsFolder
        ? SourceRootPath
        : "Direct file selection";

    public string PathCaption
    {
        get => _pathCaption;
        set => SetField(ref _pathCaption, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
                OnPropertyChanged(nameof(EncryptStatusDisplay));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public string StatusSummary => $"Status: {Status}";

    public string EncryptStatusDisplay => string.Equals(Status, "Queued", StringComparison.OrdinalIgnoreCase)
        ? "Ready"
        : Status;

    public string DetailSummary
    {
        get => _detailSummary;
        set => SetField(ref _detailSummary, value);
    }

    public string PredictedOutputPath
    {
        get => _predictedOutputPath;
        set => SetField(ref _predictedOutputPath, value);
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

    public bool CanRetry =>
        string.Equals(Status, "Needs attention", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, "Cancelled", StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetQueued(string detail)
    {
        Status = "Queued";
        DetailSummary = detail;
        ProgressPercent = 0;
        IsProgressVisible = false;
        ProgressStatus = "Pending";
    }

    public void SetProcessing()
    {
        SetState("Processing", "Working on this file now.");
        IsProgressVisible = true;
        ProgressPercent = Math.Max(ProgressPercent, 2);
        ProgressStatus = $"{ProgressPercent:0}%";
    }

    public void SetCompleted(string detail)
    {
        SetState("Completed", detail);
        IsProgressVisible = true;
        ProgressPercent = 100;
        ProgressStatus = "100%";
    }

    public void SetVerified(string detail)
    {
        SetState("Verified", detail);
        IsProgressVisible = true;
        ProgressPercent = 100;
        ProgressStatus = "100%";
    }

    public void SetNeedsAttention(string detail)
    {
        SetState("Needs attention", detail);
        if (!IsProgressVisible)
        {
            ProgressPercent = 0;
        }

        ProgressStatus = "Issue";
    }

    public void SetCancelled(string detail)
    {
        SetState("Cancelled", detail);
        if (!IsProgressVisible)
        {
            ProgressPercent = 0;
        }

        ProgressStatus = "Cancelled";
    }

    public void UpdateProgress(double percent, string? status = null)
    {
        IsProgressVisible = true;
        ProgressPercent = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        ProgressStatus = status ?? $"{ProgressPercent:0}%";
    }

    private void SetState(string status, string detail)
    {
        Status = status;
        DetailSummary = detail;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double length = Math.Max(bytes, 0);
        int order = 0;
        while (length >= 1024 && order < sizes.Length - 1)
        {
            order++;
            length /= 1024;
        }

        return $"{length:0.##} {sizes[order]}";
    }

    private static string GetItemType(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "PDF Document",
            ".doc" or ".docx" => "Word Document",
            ".xls" or ".xlsx" => "Excel Workbook",
            ".zip" => "ZIP Archive",
            ".txt" => "Text Document",
            ".png" => "PNG Image",
            ".jpg" or ".jpeg" => "JPEG Image",
            ".locked" => "Locked File",
            "" => "File",
            _ => $"{extension.TrimStart('.').ToUpperInvariant()} File"
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
