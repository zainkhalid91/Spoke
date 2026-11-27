using System.ComponentModel;
using Microsoft.UI.Xaml;
using Spoke.Core;

namespace Spoke.Settings;

/// <summary>One slice in the Wheel page's list: wraps the <see cref="SliceConfig"/> being edited.</summary>
public sealed partial class SliceRow : INotifyPropertyChanged
{
    internal SliceConfig Config { get; }

    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string Glyph { get; private set; } = "";
    public Visibility ErrorVisibility { get; private set; } = Visibility.Collapsed;
    internal string? Error { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SliceRow(SliceConfig config)
    {
        Config = config;
        Update();
    }

    /// <summary>Re-reads the slice (after an edit): label, detail, icon and whether it's valid.</summary>
    internal void Update()
    {
        var resolved = ConfigLoader.Resolve(Config, out string? error);
        Error = error;
        if (resolved != null)
        {
            Title = resolved.Label;
            Subtitle = resolved.Detail;
            Glyph = Glyphs.For(resolved).ToString();
        }
        else
        {
            Title = string.IsNullOrWhiteSpace(Config.Label) ? TypeName(Config.Type) : Config.Label!;
            Subtitle = error ?? "";
            Glyph = "";
        }
        ErrorVisibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // every property
    }

    internal static string TypeName(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "app" => "App",
        "url" => "Website",
        "folder" => "Folder",
        "search" => "Web search",
        "text" => "Text snippet",
        "tool" => "Tool",
        "group" => "Group",
        _ => "Slice",
    };
}
