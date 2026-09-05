using Microsoft.UI.Xaml;

namespace PhotoOrganizer;

public partial class App : Application
{
    internal static MainWindow MainWindowInstance { get; private set; } = null!;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "FirstBatch.log");

    public App()
    {
        Log("App constructor started.");
        UnhandledException += (_, args) => Log($"Unhandled exception: {args.Exception}");
        InitializeComponent();
        Log("App resources initialized.");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            Log("Launch received.");
            MainWindowInstance = new MainWindow();
            Log("Main window created.");
            MainWindowInstance.Activate();
            Log("Main window activated.");
        }
        catch (Exception ex)
        {
            Log($"Launch failed: {ex}");
            throw;
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never interfere with application startup.
        }
    }
}
