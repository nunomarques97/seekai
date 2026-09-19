using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using SeekAI.Core;
namespace SeekAI;
public partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    private IntPtr handle;
    private CancellationTokenSource? search;
    public string ShortcutStatus { get; private set; } = "";
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; HwndSource.FromHwnd(handle).AddHook(Hook); RegisterShortcut(); };
        Loaded += (_, _) => ShowLauncher();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) search?.Cancel(); };
    }
    public bool RegisterShortcut()
    {
        ReleaseHotkey();
        bool ok = RegisterHotKey(handle, 1, 0x4002u | (App.CurrentApp.Config.AltShortcut ? 1u : 0u), 0x20);
        ShortcutStatus = ok ? (App.CurrentApp.Config.AltShortcut ? "Ctrl + Alt + Space" : "Ctrl + Space") : "Shortcut in use · choose the alternative in Settings; tray search works";
        Status.Text = ShortcutStatus; return ok;
    }
    public void ReleaseHotkey() { if (handle != IntPtr.Zero) UnregisterHotKey(handle, 1); }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (msg == 0x312 && wParam.ToInt32() == 1) { if (IsVisible) Hide(); else ShowLauncher(); handled = true; } return IntPtr.Zero; }
    public void ShowLauncher()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2; Top = area.Top + Math.Max(20, (area.Height - Height) / 3);
        Show(); Activate(); SetForegroundWindow(handle); Query.Focus(); Query.SelectAll();
        if (Query.Text.Length > 0) BeginSearch();
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => App.CurrentApp.ShowSettings();
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) BeginSearch(); }
    private async void BeginSearch()
    {
        search?.Cancel(); search = new CancellationTokenSource(); var ct = search.Token; var query = Query.Text;
        try
        {
            await Task.Delay(140, ct);
            var hits = await Task.Run(() => App.CurrentApp.Index.SearchAsync(query, false, ct), ct);
            ct.ThrowIfCancellationRequested(); SetResults(hits);
            if (string.IsNullOrWhiteSpace(query)) { Status.Text = "Type a filename, phrase, or idea"; return; }
            Status.Text = $"{hits.Count} results · checking semantic search…";
            var ai = await App.CurrentApp.Ai.StatusAsync(ct); ct.ThrowIfCancellationRequested();
            if (ai.ModelAvailable)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { hits = await Task.Run(() => App.CurrentApp.Index.SearchAsync(query, true, timeout.Token), timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Status.Text = $"{hits.Count} results · semantic search timed out"; return; }
                ct.ThrowIfCancellationRequested(); SetResults(hits);
            }
            Status.Text = $"{hits.Count} results" + (ai.ModelAvailable ? " · local search" : " · filename / text only");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status.Text = "Search error: " + ex.Message; }
    }
    private void SetResults(List<SearchResult> hits)
    {
        var selected = (Results.SelectedItem as SearchResult)?.Path;
        Results.ItemsSource = hits; Results.SelectedIndex = Math.Max(0, hits.FindIndex(h => h.Path == selected));
    }
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up)
        { if (Results.Items.Count > 0) { Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1); Results.ScrollIntoView(Results.SelectedItem); } e.Handled = true; }
        else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
    }
    private void ResultClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        if (source is ListBoxItem) OpenSelected();
    }
    private void OpenSelected()
    {
        if (Results.SelectedItem is not SearchResult result) return;
        try { Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true }); Hide(); }
        catch (Exception ex) { Status.Text = "Could not open file: " + ex.Message; }
    }
}
