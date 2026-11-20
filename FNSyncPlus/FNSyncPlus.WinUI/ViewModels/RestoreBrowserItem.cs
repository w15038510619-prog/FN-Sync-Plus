using CommunityToolkit.Mvvm.ComponentModel;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels;

public partial class RestoreBrowserItem : ObservableObject
{
    public RestoreBrowserItem(string name, RestoreBrowserItem? parent, bool isFolder, string? originalPath = null)
    {
        Name = RestoreDisplayText.OriginalPath(name);
        Parent = parent;
        IsFolder = isFolder;
        OriginalPath = originalPath;
    }

    public string Name { get; }
    public RestoreBrowserItem? Parent { get; }
    public bool IsFolder { get; }
    public bool IsFile => !IsFolder;
    public string? OriginalPath { get; set; }
    public long FileSizeBytes { get; set; }
    public Dictionary<string, RestoreBrowserItem> Children { get; } = new(StringComparer.Ordinal);

    public bool ShowBreadcrumbSeparator => Parent != null;
    public string BreadcrumbLabel => Name;
    public string DisplayPath => Parent?.Parent == null ? Name : Parent.DisplayPath + "/" + Name;
    public string DisplayLocation => Parent?.Parent == null ? "" : Parent.DisplayPath;
    public string DisplayLocationLabel => string.IsNullOrEmpty(DisplayLocation) ? "" : $"位置：{DisplayLocation}";
    public string MetaText => IsFolder
        ? (Children.Count == 0 ? "空文件夹" : $"{Children.Count} 项")
        : (FileSizeBytes > 0 ? FormatFileSize(FileSizeBytes) : "");

    public string Icon => IsFolder ? "\uE8B7" : Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".xlsx" or ".xls" or ".csv" or ".ods" => "\uE9F9",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "\uEB9F",
        ".mp4" or ".mov" or ".mkv" or ".avi" => "\uE8B2",
        ".mp3" or ".wav" or ".flac" => "\uE8D6",
        ".zip" or ".7z" or ".rar" => "\uF012",
        ".pdf" => "\uEA90",
        ".txt" or ".md" or ".doc" or ".docx" or ".rtf" => "\uE8A5",
        _ => "\uE7C3"
    };

    [ObservableProperty] private bool? _isChecked = false;
    [ObservableProperty] private bool _canCheck;
    [ObservableProperty] private bool _showLocation;

    public IEnumerable<RestoreBrowserItem> Descendants()
    {
        yield return this;
        foreach (var child in Children.Values)
            foreach (var item in child.Descendants()) yield return item;
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes:N0} B";
    }
}
