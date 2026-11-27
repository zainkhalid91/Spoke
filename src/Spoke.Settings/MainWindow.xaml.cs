using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Spoke.Settings;

public sealed partial class MainWindow : Window
{
    internal ConfigStore Store { get; }
    internal nint Hwnd { get; }

    private WheelPage? _wheel;
    private AppearancePage? _appearance;
    private GeneralPage? _general;

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        string ico = Path.Combine(AppContext.BaseDirectory, "spoke.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        double scale = Native.GetDpiForWindow(Hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(760 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = (int)(760 * scale); p.PreferredMinimumHeight = (int)(560 * scale); }
        CenterOnScreen();

        Store = new ConfigStore(DispatcherQueue);
        Store.Saved += (msg, error) => SaveState.Text = msg;
        Store.DaemonChanged += UpdateBanners;
        Store.ReloadedFromDisk += () =>
        {
            UpdateBanners();
            RefreshPages();
            SaveState.Text = "settings.json was changed outside Settings; showing the new version.";
        };
        Closed += (_, _) => Store.Dispose();

        UpdateBanners();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "wheel";
        PageHost.Content = tag switch
        {
            "appearance" => _appearance ??= new AppearancePage(this),
            "general" => _general ??= new GeneralPage(this),
            _ => _wheel ??= new WheelPage(this),
        };
    }

    internal void UpdateBanners()
    {
        var d = Store.Daemon;
        DaemonBar.IsOpen = d is null;
        DaemonState.Text = d is null ? "Spoke isn't running"
            : d.Hotkey.Length > 0 ? $"Spoke is running\nOpen the wheel with {d.Hotkey}" : "Spoke is running\nNo shortcut is free";

        var errors = Store.Config is null ? Store.FileErrors : [];
        FileBar.IsOpen = errors.Count > 0;
        FileBar.Message = errors.Count > 0 ? string.Join("\n", errors) + "\nFix the file and Settings will pick it up." : "";
        PageHost.IsEnabled = Store.Config is not null;
        _general?.Refresh();
    }

    internal void RefreshPages()
    {
        _wheel?.Refresh();
        _appearance?.Refresh();
        _general?.Refresh();
    }

    internal void ShowSaveMessage(string text) => SaveState.Text = text;

    private void OnStartSpoke(object sender, RoutedEventArgs e)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Spoke.exe");
        if (!File.Exists(exe)) { SaveState.Text = "Spoke.exe isn't next to Settings; start it from where you installed it."; return; }
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SaveState.Text = "Couldn't start Spoke: " + ex.Message; return; }
        DispatcherQueue.TryEnqueue(async () => { await Task.Delay(800); await Store.RefreshDaemonAsync(); });
    }

    private void OnOpenFile(object sender, RoutedEventArgs e) => OpenSettingsFile();

    internal static void OpenSettingsFile()
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + ConfigStore.FilePath + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
