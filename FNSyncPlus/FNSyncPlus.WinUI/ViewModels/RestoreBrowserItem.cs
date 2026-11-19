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
    public string? OriginalPath { get; set; }
    public Dictionary<string, RestoreBrowserItem> Children { get; } = new(StringComparer.Ordinal);
    public string BreadcrumbLabel => Parent == null ? Name : "›  " + Name;
    public string DisplayPath => Parent?.Parent == null ? Name : Parent.DisplayPath + "/" + Name;
    public string DisplayLocation => Parent?.Parent == null ? "" : Parent.DisplayPath;
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
}
