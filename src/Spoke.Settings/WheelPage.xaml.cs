using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Spoke.Core;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace Spoke.Settings;

/// <summary>
/// Edits the wheels: which one (with new / copy / import / export / delete), its name and shortcut, and its slices
/// with an inspector. A group's items are edited in the same list after "Edit items" (Back returns).
/// </summary>
public sealed partial class WheelPage : UserControl
{
    private readonly MainWindow _main;
    private ConfigStore Store => _main.Store;
    private readonly ObservableCollection<SliceRow> _rows = [];
    private readonly ObservableCollection<string> _wheelNames = [];
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loading, _recording;
    /// <summary>The group whose items the list shows, or null for the wheel's slices.</summary>
    private SliceConfig? _group;
    private int _groupIndex = -1;

    private bool InGroup => _group != null;
    private List<SliceConfig> CurrentList => _group is { } g ? g.Children ??= [] : Store.Wheel.Slices!;
    private int MinRows => InGroup ? 1 : ConfigLoader.MinSlices;
    private int MaxRows => InGroup ? ConfigLoader.MaxChildren : ConfigLoader.MaxSlices;

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
        WheelList.ItemsSource = _wheelNames;
        main.Store.DaemonChanged += UpdateHotkeyText;
        Refresh();
    }

    /// <summary>Rebuilds the page from the store (first show, or the file changed on disk).</summary>
    internal void Refresh()
    {
        _loading = true;
        int keep = Math.Max(0, SliceList.SelectedIndex);
        _rows.Clear();
        _wheelNames.Clear();
        if (Store.Config is not null)
        {
            var wheel = Store.Wheel;   // clamps the index
            foreach (var (w, i) in Store.Wheels.Select((w, i) => (w, i))) _wheelNames.Add(string.IsNullOrWhiteSpace(w.Name) ? (i == 0 ? "Spoke" : $"Wheel {i + 1}") : w.Name.Trim());
            WheelList.SelectedIndex = Store.WheelIndex;
            DeleteWheelItem.IsEnabled = Store.Wheels.Count > 1;
            WheelName.Text = wheel.Name ?? "";
            WheelName.PlaceholderText = Store.WheelIndex == 0 ? "Spoke" : $"Wheel {Store.WheelIndex + 1}";
            // The file may have been reloaded: find the open group again by position.
            if (_group != null)
                _group = _groupIndex < wheel.Slices!.Count && string.Equals(wheel.Slices[_groupIndex].Type, "group", StringComparison.OrdinalIgnoreCase) ? wheel.Slices[_groupIndex] : null;
            foreach (var s in CurrentList) _rows.Add(new SliceRow(s));
        }
        GroupBack.Visibility = InGroup ? Visibility.Visible : Visibility.Collapsed;
        AddGroupItem.Visibility = InGroup ? Visibility.Collapsed : Visibility.Visible;
        TypeGroupItem.Visibility = InGroup ? Visibility.Collapsed : Visibility.Visible;
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
        if (_group is { } g) g.Children = [.. _rows.Select(r => r.Config)];
        else Store.Wheel.Slices = [.. _rows.Select(r => r.Config)];
    }

    // ───────────────────────── list ─────────────────────────

    private SliceRow? Selected => SliceList.SelectedItem as SliceRow;

    private void UpdateButtons()
    {
        int i = SliceList.SelectedIndex;
        AddButton.IsEnabled = _rows.Count < MaxRows;
        RemoveButton.IsEnabled = i >= 0 && _rows.Count > MinRows;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _rows.Count - 1;
        int bad = _rows.Count(r => r.Error != null);
        string what = _group is { } g ? $"{Store.Wheel.Name ?? "Wheel"} ▸ {g.Label ?? "Group"} ({_rows.Count} of {MaxRows})" : $"Slices ({_rows.Count} of {MaxRows})";
        SliceCount.Text = what + (bad > 0 ? $" · {bad} to fix" : "");
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
        if (_rows.Count >= MaxRows) return;
        string type = (string)((MenuFlyoutItem)sender).Tag;
        var s = type switch
        {
            "tool" => new SliceConfig { Type = "tool", Tool = "screenshot" },
            "search" => new SliceConfig { Type = "search", Label = "Search", Engine = "google" },
            "text" => new SliceConfig { Type = "text", Label = "Snippet", Text = "" },
            "url" => new SliceConfig { Type = "url", Label = "Website" },
            "folder" => new SliceConfig { Type = "folder", Label = "Folder" },
            "group" => NewGroup(),
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
        if (i < 0 || _rows.Count <= MinRows) return;
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
        var reply = await Store.SendAsync("preview", Store.WheelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        InspectorTitle.Text = InGroup ? $"Item {SliceList.SelectedIndex + 1}" : $"Slice {SliceList.SelectedIndex + 1}";
        RunAllSwitch.IsOn = s.RunAll == true;
        DelayBox.Value = s.Delay ?? 300;
        EditGroupText.Text = $"Edit items ({s.Children?.Count ?? 0})";
        AutomationProperties.SetName(EditGroupButton, EditGroupText.Text);
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
        GroupPanel.Visibility = V(type == "group");
        DelayBox.Visibility = V(type == "group" && RunAllSwitch.IsOn);
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
        if (type == "group")
        {
            s.Children ??= NewGroup().Children;
            if (string.IsNullOrWhiteSpace(s.Label)) s.Label = "Group";
        }
        else { s.Children = null; s.RunAll = null; s.Delay = null; }
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

    // ───────────────────────── groups ─────────────────────────

    private static SliceConfig NewGroup() => new()
    {
        Type = "group",
        Label = "Group",
        Children = [new() { Type = "search", Label = "Search", Engine = "google" }],
    };

    private void OnRunAllToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        bool on = RunAllSwitch.IsOn;
        if ((row.Config.RunAll == true) == on) return;
        row.Config.RunAll = on ? true : null;
        if (!on) row.Config.Delay = null;
        DelayBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Edited(true);
    }

    private void OnDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || Selected is not { } row || double.IsNaN(args.NewValue)) return;
        int ms = (int)Math.Clamp(Math.Round(args.NewValue), 0, ConfigLoader.MaxDelay);
        int? value = ms == 300 ? null : ms;   // 300 is the default: keep the file short
        if (row.Config.Delay == value) return;
        row.Config.Delay = value;
        Edited(false);
    }

    private void OnEditGroup(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || !string.Equals(row.Config.Type, "group", StringComparison.OrdinalIgnoreCase)) return;
        SyncOrder();
        _group = row.Config;
        _groupIndex = SliceList.SelectedIndex;
        SliceList.SelectedIndex = -1;
        Refresh();
        SliceList.SelectedIndex = _rows.Count > 0 ? 0 : -1;
    }

    private void OnGroupBack(object sender, RoutedEventArgs e)
    {
        if (!InGroup) return;
        SyncOrder();
        int back = _groupIndex;
        _group = null;
        _groupIndex = -1;
        SliceList.SelectedIndex = -1;
        Refresh();
        SliceList.SelectedIndex = Math.Min(back, _rows.Count - 1);
    }

    // ───────────────────────── wheels ─────────────────────────

    private void OnWheelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Store.Config is null || WheelList.SelectedIndex < 0 || WheelList.SelectedIndex == Store.WheelIndex) return;
        SyncOrder();
        _group = null;
        Store.WheelIndex = WheelList.SelectedIndex;
        SliceList.SelectedIndex = -1;
        HotkeyBar.IsOpen = false;
        Refresh();
    }

    private void ShowWheel(int index)
    {
        _group = null;
        Store.WheelIndex = index;
        SliceList.SelectedIndex = -1;
        HotkeyBar.IsOpen = false;
        Refresh();
    }

    private void OnNewWheel(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null || !CanAddWheel()) return;
        SyncOrder();
        Store.Wheels.Add(new WheelConfig
        {
            Name = Store.FreeName("New wheel"),
            Slices =
            [
                new() { Type = "search", Label = "Search", Engine = "google" },
                new() { Type = "folder", Label = "Downloads", Target = @"%USERPROFILE%\Downloads" },
            ],
        });
        ShowWheel(Store.Wheels.Count - 1);
        _ = SaveNowAsync();
        WheelName.Focus(FocusState.Programmatic);
        WheelName.SelectAll();
    }

    private void OnDuplicateWheel(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null || !CanAddWheel()) return;
        SyncOrder();
        var copy = ConfigLoader.Clone(new SpokeConfig { Wheels = [Store.Wheel] }).Wheels![0];
        copy.Name = Store.FreeName((Store.Wheel.Name ?? "Spoke") + " copy");
        copy.Hotkey = null;   // shortcuts belong to one wheel
        Store.Wheels.Insert(Store.WheelIndex + 1, copy);
        ShowWheel(Store.WheelIndex + 1);
        _ = SaveNowAsync();
    }

    private bool CanAddWheel()
    {
        if (Store.Wheels.Count < ConfigLoader.MaxWheels) return true;
        _main.ShowSaveMessage($"Spoke holds up to {ConfigLoader.MaxWheels} wheels. Delete one first.");
        return false;
    }

    private async void OnDeleteWheel(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null || Store.Wheels.Count < 2) return;
        string name = Store.Wheel.Name ?? "this wheel";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{name}\"?",
            Content = $"Its {Store.Wheel.Slices!.Count} slices are removed from settings.json. Export it first if you might want it back.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SyncOrder();
        int i = Store.WheelIndex;
        // The first wheel's shortcut lives at the top level: hand it to the new first wheel only if it had its own.
        if (i == 0) Store.Config.Hotkey = Store.Wheels[1].Hotkey ?? Store.Config.Hotkey;
        Store.Wheels.RemoveAt(i);
        if (i == 0) Store.Wheels[0].Hotkey = null;
        ShowWheel(Math.Max(0, i - 1));
        await SaveNowAsync();
    }

    private async void OnExportWheel(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null) return;
        SyncOrder();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(_main.AppWindow.Id)
            {
                SuggestedFileName = (Store.Wheel.Name ?? "Spoke").Trim(),
            };
            picker.FileTypeChoices.Add("Spoke wheel", [WheelFile.Extension]);
            var result = await picker.PickSaveFileAsync();
            if (result is null) return;
            string path = Path.HasExtension(result.Path) ? result.Path : result.Path + WheelFile.Extension;
            File.WriteAllText(path, WheelFile.Export(Store.Wheel));
            _main.ShowSaveMessage($"Exported \"{Store.Wheel.Name}\" to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Write("Export failed: " + ex);
            _main.ShowSaveMessage("Couldn't export: " + ex.Message);
        }
    }

    private async void OnImportWheel(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null || !CanAddWheel()) return;
        string text;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_main.AppWindow.Id);
            picker.FileTypeFilter.Add(WheelFile.Extension);
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            text = File.ReadAllText(result.Path);
        }
        catch (Exception ex)
        {
            _main.ShowSaveMessage("Couldn't open the file: " + ex.Message);
            return;
        }
        ImportWheel(text);
    }

    /// <summary>Adds a wheel from a .spoke file's text and shows it.</summary>
    internal void ImportWheel(string text)
    {
        if (Store.Config is null) return;
        var wheel = WheelFile.Import(text, Store.Wheels.Select(w => w.Name ?? ""), out string? error);
        if (wheel is null) { _main.ShowSaveMessage("Not imported: " + error); return; }
        SyncOrder();
        Store.Wheels.Add(wheel);
        ShowWheel(Store.Wheels.Count - 1);
        _ = SaveNowAsync();
        var missing = WheelFile.Missing(wheel);
        if (missing.Count > 0)
            ShowHotkeyBar(InfoBarSeverity.Warning, $"Imported \"{wheel.Name}\": {missing.Count} not on this PC",
                string.Join("\n", missing.Take(5)) + (missing.Count > 5 ? $"\n…and {missing.Count - 5} more" : "") + "\nFix their paths in the list below.", null);
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
        if (_recording || Store.Config is null) return;
        int w = Store.WheelIndex;
        string? configured = Store.HotkeyOf(w);
        string active = Store.Daemon?.Hotkey ?? "";
        HotkeyDefault.Content = w == 0 ? "Use default" : "Remove";
        ToolTipService.SetToolTip(HotkeyDefault, w == 0 ? "Let Spoke pick the first free shortcut (Ctrl+Alt+Space if it's free)" : "No shortcut of its own: reach it with Tab from another wheel");
        HotkeyDefault.IsEnabled = configured != null;
        if (w > 0)
        {
            HotkeyText.Text = configured ?? "None · Tab reaches it from another wheel";
            return;
        }
        HotkeyText.Text = configured ?? (active.Length > 0 ? active : "Ctrl+Alt+Space");
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

    /// <summary>The other wheel that already uses <paramref name="hk"/>, if any.</summary>
    private string? WheelUsing(Hotkey hk)
    {
        for (int i = 0; i < Store.Wheels.Count; i++)
        {
            if (i == Store.WheelIndex) continue;
            string? key = i == 0 ? Store.HotkeyOf(0) ?? Store.Daemon?.Hotkey : Store.HotkeyOf(i);
            if (key != null && Hotkey.TryParse(key, out var k) && k == hk) return Store.Wheels[i].Name ?? $"Wheel {i + 1}";
        }
        return null;
    }

    private void TryUseHotkey(Hotkey hk)
    {
        if (WheelUsing(hk) is { } other)
        {
            UpdateHotkeyText();
            ShowHotkeyBar(InfoBarSeverity.Error, $"{hk} already opens \"{other}\"", "Each wheel needs its own shortcut. Try one of these:", hk);
            return;
        }
        string active = Store.Daemon?.Hotkey ?? "";
        string? mine = Store.HotkeyOf(Store.WheelIndex);
        // Spoke itself holds the shortcuts it registered, so they look taken to the probe.
        bool isSpokes = (Store.WheelIndex == 0 && Hotkey.TryParse(active, out var current) && current == hk)
                        || (mine != null && Hotkey.TryParse(mine, out var own) && own == hk);
        if (!isSpokes && !Native.IsHotkeyFree(_main.Hwnd, hk))
        {
            UpdateHotkeyText();
            ShowHotkeyBar(InfoBarSeverity.Error, $"{hk} is used by another app",
                "Windows only lets one app own a shortcut. Try one of these free ones:", hk);
            return;
        }
        Store.SetHotkey(Store.WheelIndex, hk.ToString());
        HotkeyText.Text = hk.ToString();
        HotkeyDefault.IsEnabled = true;
        if (hk.Warning() is { } warning) ShowHotkeyBar(InfoBarSeverity.Warning, $"{hk} works, but…", warning, null);
        else HotkeyBar.IsOpen = false;
        _ = SaveNowAsync();
    }

    private void OnHotkeyDefault(object sender, RoutedEventArgs e)
    {
        if (Store.Config is null) return;
        Store.SetHotkey(Store.WheelIndex, null);
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
                if (!Hotkey.TryParse(text, out var s) || s == t || WheelUsing(s) != null) continue;
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
