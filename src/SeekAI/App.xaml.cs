using System.IO;
using System.Windows;
using SeekAI.Core;
using Forms = System.Windows.Forms;
namespace SeekAI;
public partial class App : System.Windows.Application
{
    public Settings Config { get; private set; } = new();
    public Ollama Ai { get; } = new();
    public SearchIndex Index { get; private set; } = null!;
    public string DataPath { get; private set; } = Settings.DataDirectory;
    public string IndexStatus { get; private set; } = "Ready";
    public bool Scanning { get; private set; }
    public event Action? StatusChanged;
    private Forms.NotifyIcon? tray;
    private Mutex? mutex;
    private SettingsWindow? settings;
    private readonly CancellationTokenSource lifetime = new();
    public static App CurrentApp => (App)Current;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataArg = Array.IndexOf(e.Args, "--data");
            if (dataArg >= 0 && dataArg + 1 < e.Args.Length) DataPath = Path.GetFullPath(e.Args[dataArg + 1]);
            mutex = new Mutex(true, "Local\\SeekAI-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataPath.ToLowerInvariant())))[..16], out bool first);
            if (!first) { MessageBox.Show("SeekAI is already running. Use its tray icon or global shortcut.", "SeekAI"); Shutdown(); return; }
            Directory.CreateDirectory(DataPath);
            Config = Settings.Load(DataPath);
            Index = new SearchIndex(Path.Combine(DataPath, "index.db"), Ai);
            var launcher = new MainWindow(); MainWindow = launcher; launcher.Show();
            tray = new Forms.NotifyIcon { Text = "SeekAI · local search", Icon = System.Drawing.SystemIcons.Application, Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Search", null, (_, _) => launcher.ShowLauncher());
            menu.Items.Add("Settings", null, (_, _) => ShowSettings());
            menu.Items.Add("Reindex", null, async (_, _) => await Scan());
            menu.Items.Add("Quit", null, (_, _) => Shutdown());
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => launcher.ShowLauncher();
            if (Config.Folders.Count == 0) ShowSettings();
            _ = Scan();
        }
        catch (Exception ex) { MessageBox.Show("SeekAI could not start: " + ex.Message, "SeekAI"); Shutdown(1); }
    }
    public void ShowSettings()
    {
        MainWindow.Hide();
        if (settings == null) { settings = new SettingsWindow(); settings.Closed += (_, _) => settings = null; }
        settings.Show(); settings.Activate();
    }
    public async Task Scan()
    {
        if (Scanning) return;
        Scanning = true; IndexStatus = "Scanning folders…"; StatusChanged?.Invoke();
        var roots = Config.Folders.ToArray();
        var progress = new Progress<string>(s => { IndexStatus = s; StatusChanged?.Invoke(); });
        try { var report = await Task.Run(() => Index.ScanAsync(roots, progress, lifetime.Token)); IndexStatus = report.Message; }
        catch (OperationCanceledException) { IndexStatus = "Indexing stopped"; }
        catch (Exception ex) { IndexStatus = "Index error: " + ex.Message; }
        finally { Scanning = false; StatusChanged?.Invoke(); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel(); tray?.Dispose(); (MainWindow as MainWindow)?.ReleaseHotkey(); mutex?.Dispose(); base.OnExit(e);
    }
}
