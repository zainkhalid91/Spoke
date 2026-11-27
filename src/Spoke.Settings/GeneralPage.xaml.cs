using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Spoke.Settings;

/// <summary>Start with Windows (asked of the daemon, which owns the Run entry), the settings file, reset.</summary>
public sealed partial class GeneralPage : UserControl
{
    private readonly MainWindow _main;
    private ConfigStore Store => _main.Store;
    private bool _loading;

    internal GeneralPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        FilePathText.Text = ConfigStore.FilePath;
        var v = typeof(GeneralPage).Assembly.GetName().Version;
        VersionText.Text = $"Spoke Settings {v?.ToString(3) ?? ""}";
        Refresh();
    }

    internal void Refresh()
    {
        var d = Store.Daemon;
        _loading = true;
        AutostartSwitch.IsEnabled = d is not null;
        AutostartSwitch.IsOn = d?.Autostart ?? false;
        AutostartHint.Text = d is null
            ? "Start Spoke to change this."
            : "Spoke starts when you sign in, so the wheel is always one shortcut away.";
        _loading = false;
    }

    private async void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = AutostartSwitch.IsOn;
        var reply = await Store.SendAsync("autostart", want ? "on" : "off");
        if (reply is null || !reply.Ok)
            _main.ShowSaveMessage(reply is null ? "Spoke isn't running." : "Couldn't change start-up: " + reply.Message);
        else
            _main.ShowSaveMessage(want ? "Spoke will start with Windows." : "Spoke won't start with Windows.");
        Refresh();
    }

    private void OnOpenFile(object sender, RoutedEventArgs e) => MainWindow.OpenSettingsFile();

    private void OnShowFolder(object sender, RoutedEventArgs e) =>
        Launch("explorer.exe", $"/select,\"{ConfigStore.FilePath}\"");

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spoke", "spoke.log");
        if (File.Exists(log)) Launch("notepad.exe", $"\"{log}\"");
        else _main.ShowSaveMessage("There's no log yet.");
    }

    private static void Launch(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async void OnResetWheel(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset the wheel?",
            Content = "Your slices are replaced by Spoke's starter wheel. This can't be undone from Settings.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Store.ResetWheel();
        await Store.SaveAsync();
        _main.RefreshPages();
    }
}
