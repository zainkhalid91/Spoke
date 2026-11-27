using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Spoke.Core;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Spoke.Settings;

/// <summary>Edits the (single, in M2) wheel: name, shortcut, and its slices with an inspector.</summary>
public sealed partial class WheelPage : UserControl
{
    private readonly MainWindow _main;
    private ConfigStore Store => _main.Store;
    private readonly ObservableCollection<SliceRow> _rows = [];
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loading, _recording;

    internal WheelPage(MainWindow main)
    {
        _main = main;
        _loading = true;   // controls raise change events while the XAML loads
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _ = SaveNowAsync();
        InitializeComponent();
        _loading = false;
        SliceList.ItemsSource = _rows;
        main.Store.DaemonChanged += UpdateHotkeyText;
        Refresh();
    }

    /// <summary>Rebuilds the page from the store (first show, or the file changed on disk).</summary>
    internal void Refresh()
    {
        _loading = true;
        int keep = Math.Max(0, SliceList.SelectedIndex);
        _rows.Clear();
        if (Store.Config is not null)
        {
            WheelName.Text = Store.Wheel.Name ?? "";
            foreach (var s in Store.Wheel.Slices!) _rows.Add(new SliceRow(s));
        }
        _loading = false;
        SliceList.SelectedIndex = _rows.Count == 0 ? -1 : Math.Min(keep, _rows.Count - 1);
        UpdateHotkeyText();
        UpdateButtons();
        ShowInspector();
    }

    // ───────────────────────── saving ─────────────────────────

    /// <summary>Text edits save after a short pause; picks and moves save at once.</summary>
    private void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    private async Task SaveNowAsync()
    {
        _saveTimer.Stop();
        SyncOrder();
        await Store.SaveAsync();
        UpdateHotkeyText();
    }

    /// <summary>The list is the source of truth for slice order.</summary>
    private void SyncOrder()
    {
        if (Store.Config is null) return;
        Store.Wheel.Slices = [.. _rows.Select(r => r.Config)];
    }

    // ───────────────────────── list ─────────────────────────

    private SliceRow? Selected => SliceList.SelectedItem as SliceRow;

    private void UpdateButtons()
    {
        int i = SliceList.SelectedIndex;
        AddButton.IsEnabled = _rows.Count < ConfigLoader.MaxSlices;
        RemoveButton.IsEnabled = i >= 0 && _rows.Count > ConfigLoader.MinSlices;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _rows.Count - 1;
        int bad = _rows.Count(r => r.Error != null);
        SliceCount.Text = $"Slices ({_rows.Count} of {ConfigLoader.MaxSlices})" + (bad > 0 ? $" · {bad} to fix" : "");
    }

    private void OnSliceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateButtons();
        ShowInspector();
    }

    private void OnSlicesReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        UpdateButtons();
        _ = SaveNowAsync();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= ConfigLoader.MaxSlices) return;
        string type = (string)((MenuFlyoutItem)sender).Tag;
        var s = type switch
        {
            "tool" => new SliceConfig { Type = "tool", Tool = "screenshot" },
            "search" => new SliceConfig { Type = "search", Label = "Search", Engine = "google" },
            "text" => new SliceConfig { Type = "text", Label = "Snippet", Text = "" },
            "url" => new SliceConfig { Type = "url", Label = "Website" },
            "folder" => new SliceConfig { Type = "folder", Label = "Folder" },
            _ => new SliceConfig { Type = "app", Label = "App" },
        };
        var row = new SliceRow(s);
        int at = SliceList.SelectedIndex >= 0 ? SliceList.SelectedIndex + 1 : _rows.Count;
        _rows.Insert(at, row);
        SliceList.SelectedIndex = at;
        // A new app/website/folder isn't valid until it has a target: it saves once it does.
        _ = SaveNowAsync();
        (type is "app" or "url" or "folder" ? (Control)TargetBox : type == "text" ? SnippetBox : LabelBox).Focus(FocusState.Programmatic);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        int i = SliceList.SelectedIndex;
        if (i < 0 || _rows.Count <= ConfigLoader.MinSlices) return;
        _rows.RemoveAt(i);
        SliceList.SelectedIndex = Math.Min(i, _rows.Count - 1);
        _ = SaveNowAsync();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int i = SliceList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        _loading = true;
        _rows.Move(i, j);
        _loading = false;
        SliceList.SelectedIndex = j;
        UpdateButtons();
        _ = SaveNowAsync();
    }

    private void OnWheelNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Store.Config is null) return;
        Store.Wheel.Name = string.IsNullOrWhiteSpace(WheelName.Text) ? null : WheelName.Text.Trim();
        SaveSoon();
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsRunning) await SaveNowAsync();
        var reply = await Store.SendAsync("preview");
        if (reply is null) _main.ShowSaveMessage("Spoke isn't running, so there's no wheel to show.");
        else if (!reply.Ok) _main.ShowSaveMessage(reply.Message ?? "");
    }

    // ───────────────────────── inspector ─────────────────────────

    private static void Select(ComboBox box, string? tag)
    {
        foreach (var o in box.Items)
            if (o is ComboBoxItem item && string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        box.SelectedIndex = -1;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ShowInspector()
    {
        var row = Selected;
        Inspector.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        var s = row.Config;
        string type = (s.Type ?? "").ToLowerInvariant();

        _loading = true;
        InspectorTitle.Text = $"Slice {SliceList.SelectedIndex + 1}";
        Select(TypeBox, type);
        Select(ToolBox, ToolTag(s.Tool));
        TargetBox.Text = s.Target ?? "";
        ArgsBox.Text = s.Args ?? "";
        WorkDirBox.Text = s.WorkDir ?? "";
        BrowserBox.Text = s.Browser ?? "";
        Select(OpenBox, string.IsNullOrEmpty(s.Open) ? "explorer" : s.Open);
        bool known = s.Engine is null || SearchEngines.Names.Contains(s.Engine, StringComparer.OrdinalIgnoreCase);
        Select(EngineBox, known ? (s.Engine ?? "google") : "custom");
        EngineUrlBox.Text = known ? "" : s.Engine ?? "";
        SnippetBox.Text = s.Text ?? "";
        LabelBox.Text = s.Label ?? "";
        DetailBox.Text = s.Detail ?? "";
        IconBox.Text = s.Icon ?? "";
        _loading = false;

        LayoutFor(type);
        UpdateRowState(row);
    }

    /// <summary>The tool names as settings.json spells them, whatever spelling the file used.</summary>
    private static string? ToolTag(string? tool) => (tool ?? "").Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
    {
        "screenshot" => "screenshot",
        "colorpicker" or "colourpicker" => "colorPicker",
        "clips" => "clips",
        "files" or "file" or "convert" => "files",
        "text" or "texttools" => "text",
        "generate" or "generators" => "generate",
        "system" => "system",
        _ => tool,
    };

    /// <summary>Shows only the fields this kind of slice uses.</summary>
    private void LayoutFor(string type)
    {
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        ToolBox.Visibility = V(type == "tool");
        TargetRow.Visibility = V(type is "app" or "url" or "folder");
        BrowseButton.Visibility = V(type is "app" or "folder");
        TargetBox.Header = type switch { "url" => "Address", "folder" => "Folder", _ => "App" };
        TargetBox.PlaceholderText = type switch
        {
            "url" => "github.com",
            "folder" => @"%USERPROFILE%\Documents",
            _ => "An exe name like code.exe, a full path, or a shortcut",
        };
        ArgsBox.Visibility = V(type == "app");
        WorkDirBox.Visibility = V(type == "app");
        BrowserBox.Visibility = V(type == "url");
        OpenBox.Visibility = V(type == "folder");
        EngineBox.Visibility = V(type == "search");
        EngineUrlBox.Visibility = V(type == "search" && TagOf(EngineBox) == "custom");
        SnippetBox.Visibility = V(type == "text");
        TokensHint.Visibility = V(type == "text");
        IconBox.PlaceholderText = type == "app" ? "Apps use their own icon unless you set one (e.g. E774)." : "A Segoe Fluent Icons code, e.g. E774";
    }

    private void UpdateRowState(SliceRow row)
    {
        row.Update();
        SliceBar.IsOpen = row.Error != null;
        SliceBar.Message = row.Error ?? "";
        if (Selected == row && row.Error == null)
        {
            var resolved = ConfigLoader.Resolve(row.Config, out _);
            LabelBox.PlaceholderText = resolved?.Label ?? "";
            DetailBox.PlaceholderText = resolved?.Detail ?? "";
        }
        UpdateButtons();
    }

    /// <summary>Called after any inspector edit: refresh the row, then save (the store refuses invalid configs).</summary>
    private void Edited(bool immediately)
    {
        if (Selected is not { } row) return;
        UpdateRowState(row);
        if (immediately) _ = SaveNowAsync(); else SaveSoon();
    }

    private static string? Value(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } row || TagOf(TypeBox) is not { } type) return;
        var s = row.Config;
        if (string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)) return;
        // Keep the label; drop what only the old kind of slice used, so the file stays clean.
        s.Type = type;
        if (type != "tool") s.Tool = null; else s.Tool ??= "screenshot";
        if (type is not ("app" or "url" or "folder")) s.Target = null;
        if (type != "app") { s.Args = null; s.WorkDir = null; }
        if (type != "url") s.Browser = null;
        if (type != "folder") s.Open = null;
        if (type != "search") s.Engine = null; else s.Engine ??= "google";
        if (type != "text") s.Text = null;
        ShowInspector();
        Edited(true);
    }

    private void OnToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        if (string.Equals(ToolTag(row.Config.Tool), TagOf(ToolBox), StringComparison.Ordinal)) return;
        row.Config.Tool = TagOf(ToolBox);
        Edited(true);
    }

    private void OnOpenChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        string? open = TagOf(OpenBox);
        if ((row.Config.Open ?? "explorer") == open) return;
        row.Config.Open = open == "explorer" ? null : open;
        Edited(true);
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        string? tag = TagOf(EngineBox);
        bool known = row.Config.Engine is null || SearchEngines.Names.Contains(row.Config.Engine, StringComparer.OrdinalIgnoreCase);
        if (tag == (known ? row.Config.Engine ?? "google" : "custom")) return;
        EngineUrlBox.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
        row.Config.Engine = tag == "custom" ? Value(EngineUrlBox) ?? "" : tag;
        if (tag == "custom") EngineUrlBox.Focus(FocusState.Programmatic);
        Edited(true);
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        var s = row.Config;
        var box = sender as TextBox;
        if (box == TargetBox) s.Target = Value(TargetBox);
        else if (box == ArgsBox) s.Args = Value(ArgsBox);
        else if (box == WorkDirBox) s.WorkDir = Value(WorkDirBox);
        else if (box == BrowserBox) s.Browser = Value(BrowserBox);
        else if (box == EngineUrlBox) s.Engine = Value(EngineUrlBox) ?? "";
        else if (box == SnippetBox) s.Text = SnippetBox.Text.Length == 0 ? null : SnippetBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        else if (box == LabelBox) s.Label = Value(LabelBox);
        else if (box == DetailBox) s.Detail = Value(DetailBox);
        else if (box == IconBox) s.Icon = Value(IconBox);
        Edited(false);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        string? path;
        try
        {
            if (string.Equals(row.Config.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_main.AppWindow.Id);
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            else
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_main.AppWindow.Id);
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".lnk");
                picker.FileTypeFilter.Add(".bat");
                picker.FileTypeFilter.Add(".cmd");
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFileAsync())?.Path;
            }
        }
        catch (Exception ex)
        {
            _main.ShowSaveMessage("Couldn't open the picker: " + ex.Message);
            return;
        }
        if (path is null) return;
        TargetBox.Text = path;   // OnFieldChanged stores it
        if (string.IsNullOrWhiteSpace(row.Config.Label) || row.Config.Label is "App" or "Folder")
        {
            string name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
            if (name.Length == 0) name = path;
            LabelBox.Text = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
        }
        _ = SaveNowAsync();
    }

    // ───────────────────────── shortcut ─────────────────────────

    private void UpdateHotkeyText()
    {
        if (_recording) return;
        string? configured = Store.Config?.Hotkey;
        string active = Store.Daemon?.Hotkey ?? "";
        HotkeyText.Text = configured ?? (active.Length > 0 ? active : "Ctrl+Alt+Space");
        HotkeyDefault.IsEnabled = configured != null;
        // The daemon fell back to another shortcut because the chosen one is taken.
        if (configured != null && active.Length > 0 && Hotkey.TryParse(configured, out var want) && Hotkey.TryParse(active, out var got) && want != got)
        {
            ShowHotkeyBar(InfoBarSeverity.Warning, $"{configured} is taken",
                $"Another app holds {configured}, so Spoke is using {active}. Pick a different shortcut, or close the other app and restart Spoke.", want);
        }
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        _recording = true;
        HotkeyText.Text = "Press the new shortcut… (Esc cancels)";
        HotkeyBar.IsOpen = false;
    }

    private void OnRecordLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recording) return;
        _recording = false;
        UpdateHotkeyText();
    }

    private static bool IsDown(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void OnRecordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;   // wait for the actual key
        if (key == VirtualKey.Escape)
        {
            _recording = false;
            UpdateHotkeyText();
            return;
        }
        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Ctrl;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (Hotkey.FromKeys(mods, (uint)key) is not { } hk)
        {
            HotkeyText.Text = mods == HotkeyModifiers.None
                ? "Hold Ctrl, Alt, Shift or Win with the key… (Esc cancels)"
                : "Use a letter, number, F-key, Space, Tab, Enter or ` … (Esc cancels)";
            return;
        }
        _recording = false;
        TryUseHotkey(hk);
    }

    private void TryUseHotkey(Hotkey hk)
    {
        string active = Store.Daemon?.Hotkey ?? "";
        bool isSpokes = Hotkey.TryParse(active, out var current) && current == hk;
        if (!isSpokes && !Native.IsHotkeyFree(_main.Hwnd, hk))
        {
            UpdateHotkeyText();
            ShowHotkeyBar(InfoBarSeverity.Error, $"{hk} is used by another app",
                "Windows only lets one app own a shortcut. Try one of these free ones:", hk);
            return;
        }
        Store.Config!.Hotkey = hk.ToString();
        HotkeyText.Text = hk.ToString();
        HotkeyDefault.IsEnabled = true;
        if (hk.Warning() is { } warning) ShowHotkeyBar(InfoBarSeverity.Warning, $"{hk} works, but…", warning, null);
        else HotkeyBar.IsOpen = false;
        _ = SaveNowAsync();
    }

    private void OnHotkeyDefault(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null) return;
        Store.Config.Hotkey = null;
        HotkeyBar.IsOpen = false;
        _ = SaveNowAsync();
    }

    /// <summary>A message under the shortcut; with <paramref name="taken"/>, also up to 3 free alternatives as buttons.</summary>
    private void ShowHotkeyBar(InfoBarSeverity severity, string title, string message, Hotkey? taken)
    {
        HotkeyBar.Severity = severity;
        HotkeyBar.Title = title;
        HotkeyBar.Message = message;
        Suggestions.Children.Clear();
        if (taken is { } t)
        {
            Hotkey.TryParse(Store.Daemon?.Hotkey ?? "", out var current);
            foreach (var text in Hotkey.Suggestions)
            {
                if (Suggestions.Children.Count == 3) break;
                if (!Hotkey.TryParse(text, out var s) || s == t) continue;
                if (s != current && !Native.IsHotkeyFree(_main.Hwnd, s)) continue;
                var b = new Button { Content = text };
                b.Click += (_, _) => TryUseHotkey(s);
                Suggestions.Children.Add(b);
            }
        }
        Suggestions.Visibility = Suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyBar.IsOpen = true;
    }
}
