using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Spoke.Core;
using Windows.UI;

namespace Spoke.Settings;

/// <summary>Theme (or Match Windows), slice colours and size. Every change saves and the daemon applies it at once.</summary>
public sealed partial class AppearancePage : UserControl
{
    private readonly MainWindow _main;
    private ConfigStore Store => _main.Store;
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loading;

    internal AppearancePage(MainWindow main)
    {
        _main = main;
        // Before InitializeComponent: the slider snaps its value to Minimum while the XAML loads, which raises
        // ValueChanged, and the handler must find the timer (and _loading) ready.
        _loading = true;
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _ = Store.SaveAsync();
        InitializeComponent();
        foreach (var name in Appearance.ThemeNames) { LightBox.Items.Add(name); DarkBox.Items.Add(name); }
        foreach (var (name, scale) in Appearance.Sizes)
        {
            var b = new Button { Content = name switch { "S" => "Small", "M" => "Medium", "L" => "Large", _ => "Extra large" } };
            ToolTipService.SetToolTip(b, $"{scale * 100:0}%");
            b.Click += (_, _) => SizeSlider.Value = scale * 100;
            SizePresets.Children.Add(b);
        }
        Refresh();
    }

    internal void Refresh()
    {
        if (Store.Config is not { } c) return;
        var look = Appearance.From(c, []);
        _loading = true;
        SelectTag(ThemeChoice, look.FollowsSystem ? Appearance.Auto : look.Theme);
        LightBox.SelectedItem = look.LightTheme;
        DarkBox.SelectedItem = look.DarkTheme;
        SelectTag(AccentChoice, look.Accent switch { AccentMode.Windows => "windows", AccentMode.Custom => "custom", _ => "theme" });
        uint rgb = look.Accent == AccentMode.Custom ? look.AccentRgb : 0x4F8EF7;
        Picker.Color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        ShowColor(rgb);
        SizeSlider.Value = Math.Round(look.Scale * 100);
        SelectTag(SubMenuChoice, look.SubMenus == SubMenuStyle.Wheel ? "wheel" : "ring");
        _loading = false;
        UpdateVisibility();
        UpdateSizeText();
    }

    private static void SelectTag(RadioButtons group, string tag)
    {
        for (int i = 0; i < group.Items.Count; i++)
            if (group.Items[i] is RadioButton r && string.Equals((string)r.Tag, tag, StringComparison.OrdinalIgnoreCase)) { group.SelectedIndex = i; return; }
    }

    private static string? SelectedTag(RadioButtons group)
    {
        foreach (var o in group.Items) if (o is RadioButton { IsChecked: true } r) return r.Tag as string;
        return null;
    }

    private void UpdateVisibility()
    {
        AutoThemes.Visibility = SelectedTag(ThemeChoice) == Appearance.Auto ? Visibility.Visible : Visibility.Collapsed;
        CustomColorRow.Visibility = SelectedTag(AccentChoice) == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveNow() { _saveTimer.Stop(); _ = Store.SaveAsync(); }
    private void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    // Each RadioButton's Checked event, not RadioButtons.SelectionChanged: the group's event doesn't fire when a
    // button is checked through UI Automation (screen readers, voice access), the button's own event always does.
    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        if (_loading || Store.Config is null || (sender as RadioButton)?.Tag is not string theme) return;
        Store.Config.Theme = theme;
        SaveNow();
    }

    private void OnAutoThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Store.Config is null) return;
        if (Store.Config.LightTheme == LightBox.SelectedItem as string && Store.Config.DarkTheme == DarkBox.SelectedItem as string) return;
        Store.Config.LightTheme = LightBox.SelectedItem as string;
        Store.Config.DarkTheme = DarkBox.SelectedItem as string;
        SaveNow();
    }

    private void OnAccentChanged(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        if (_loading || Store.Config is null || (sender as RadioButton)?.Tag is not string accent) return;
        Store.Config.Accent = accent == "custom" ? Hex(Picker.Color) : accent == "theme" ? null : accent;
        SaveNow();
    }

    private void OnSubMenusChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Store.Config is null || (sender as RadioButton)?.Tag is not string style) return;
        string? value = style == "wheel" ? "wheel" : null;   // ring is the default: keep the file short
        if (Store.Config.SubMenus == value) return;
        Store.Config.SubMenus = value;
        SaveNow();
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        uint rgb = (uint)(args.NewColor.R << 16 | args.NewColor.G << 8 | args.NewColor.B);
        ShowColor(rgb);
        if (_loading || Store.Config is null || SelectedTag(AccentChoice) != "custom") return;
        Store.Config.Accent = Hex(args.NewColor);
        SaveSoon();
    }

    private void ShowColor(uint rgb)
    {
        Swatch.Background = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        ColorText.Text = $"#{rgb:X6}";
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void OnSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSizeText();
        if (_loading || Store.Config is null) return;
        float scale = (float)(SizeSlider.Value / 100);
        var preset = Appearance.Sizes.FirstOrDefault(s => MathF.Abs(s.Scale - scale) < 0.001f).Name;
        Store.Config.Size = preset ?? $"{SizeSlider.Value.ToString("0", CultureInfo.InvariantCulture)}%";
        if (preset == "M") Store.Config.Size = null;   // the default: keep the file short
        SaveSoon();
    }

    private void UpdateSizeText()
    {
        float scale = (float)(SizeSlider.Value / 100);
        string? preset = Appearance.Sizes.FirstOrDefault(s => MathF.Abs(s.Scale - scale) < 0.001f).Name;
        string name = preset switch { "S" => "Small · ", "M" => "Medium · ", "L" => "Large · ", "XL" => "Extra large · ", _ => "" };
        SizeText.Text = $"{name}{SizeSlider.Value:0}%";
    }

    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsRunning) { _saveTimer.Stop(); await Store.SaveAsync(); }
        var reply = await Store.SendAsync("preview");
        if (reply is null) _main.ShowSaveMessage("Spoke isn't running, so there's no wheel to show.");
        else if (!reply.Ok) _main.ShowSaveMessage(reply.Message ?? "");
    }
}
