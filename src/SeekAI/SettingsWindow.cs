using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace SeekAI;
public sealed class SettingsWindow : Window
{
    private readonly App app = App.CurrentApp;
    private readonly ListBox folders = new() { Height = 155, Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) };
    private readonly TextBlock aiStatus = new() { Margin = new Thickness(0, 12, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock shortcutStatus = new() { Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly Button pull;
    private bool downloading;
    public SettingsWindow()
    {
        Title = "SeekAI Settings"; Width = 680; Height = 700; MinWidth = 550; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Search starts with your folders", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Choose only the folders you want in your local index.", Margin = new Thickness(0, 6, 0, 4) });
        panel.Children.Add(folders); panel.Children.Add(actions);
        AddButton(actions, "Add folder", async () =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder to index", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var path in dialog.FolderNames) if (!app.Config.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) app.Config.Folders.Add(path);
            Save(); await app.Scan();
        });
        AddButton(actions, "Remove selected", async () => { if (folders.SelectedItem is string path) { app.Config.Folders.Remove(path); Save(); await app.Scan(); } });
        AddButton(actions, "Reindex", async () => await app.Scan());
        panel.Children.Add(status); panel.Children.Add(aiStatus);
        var aiActions = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(aiActions);
        AddButton(aiActions, "Refresh AI status", async () => await RefreshAi());
        pull = AddButton(aiActions, "Download embedding model", async () =>
        {
            downloading = true; pull!.IsEnabled = false; aiStatus.Text = "Downloading nomic-embed-text (~274 MB)…";
            try { await app.Ai.PullAsync(); await app.Scan(); }
            finally { downloading = false; await RefreshAi(); }
        });
        panel.Children.Add(new TextBlock { Text = "If Ollama is unavailable, start Ollama. If it is not installed, get it from ollama.com. File contents only go to 127.0.0.1.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 16) });
        var shortcut = new CheckBox { Content = "Use Ctrl + Alt + Space instead of Ctrl + Space", IsChecked = app.Config.AltShortcut };
        shortcut.Click += (_, _) => { app.Config.AltShortcut = shortcut.IsChecked == true; app.Config.Save(app.DataPath); ((MainWindow)app.MainWindow).RegisterShortcut(); Refresh(); };
        panel.Children.Add(shortcut); panel.Children.Add(shortcutStatus);
        panel.Children.Add(new TextBlock { Text = "Local only · no accounts · no telemetry\nText, Markdown, code, and text-based PDF. Skips generated folders, files over 5 MB / 250,000 characters, and linked subfolders. Reindex to pick up edits; unchanged files keep their embeddings.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 16) });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(bottom);
        AddButton(bottom, "Open search", () => { ((MainWindow)app.MainWindow).ShowLauncher(); return Task.CompletedTask; });
        app.StatusChanged += Refresh; Closed += (_, _) => app.StatusChanged -= Refresh;
        Loaded += async (_, _) => await RefreshAi(); Refresh();
    }
    private Button AddButton(Panel panel, string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "SeekAI"); } };
        panel.Children.Add(button); return button;
    }
    private void Save() { app.Config.Save(app.DataPath); Refresh(); }
    private void Refresh()
    {
        folders.ItemsSource = app.Config.Folders.ToArray();
        var counts = app.Index.Counts(); status.Text = $"{counts.Files} files · {counts.Vectors}/{counts.Chunks} chunks embedded\n{app.IndexStatus}";
        actions.IsEnabled = !app.Scanning; shortcutStatus.Text = ((MainWindow)app.MainWindow).ShortcutStatus;
    }
    private async Task RefreshAi()
    {
        if (downloading) return;
        var state = await app.Ai.StatusAsync(); aiStatus.Text = state.Message; pull.IsEnabled = state.Running && !state.ModelAvailable;
    }
}
