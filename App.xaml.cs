using Microsoft.UI.Xaml;
using MyMusic.Data;
using MyMusic.Services;

namespace MyMusic;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static Database Database { get; private set; } = null!;
    public static LibraryService Library { get; private set; } = null!;
    public static string DataRoot { get; private set; } = "";
    public static string LibraryLocationFile => Path.Combine(DataRoot, "library-location.txt");
    private static string? _legacyLibraryToMigrate;

    public App()
    {
        // Install crash logging before loading App.xaml so startup/XAML failures are captured.
        UnhandledException += (_, e) =>
        {
            WriteStartupCrashLog("Application.UnhandledException", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteStartupCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception,
                e.ExceptionObject?.ToString());
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteStartupCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            WriteStartupCrashLog("App.InitializeComponent", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyMusic");
        Directory.CreateDirectory(DataRoot);

        var legacyDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMusic");
        foreach (var name in new[] { "mymusic.db", "settings.txt", "theme.txt", "recent-edits.json" })
        {
            var oldPath = Path.Combine(legacyDataRoot, name);
            var newPath = Path.Combine(DataRoot, name);
            if (!File.Exists(newPath) && File.Exists(oldPath))
            {
                try { File.Move(oldPath, newPath); }
                catch { File.Copy(oldPath, newPath, true); }
            }
        }

        var defaultLibrary = Path.Combine(DataRoot, "Library");
        var configuredLibrary = File.Exists(LibraryLocationFile) ? File.ReadAllText(LibraryLocationFile).Trim() : "";
        var libraryRoot = string.IsNullOrWhiteSpace(configuredLibrary) ? defaultLibrary : Path.GetFullPath(configuredLibrary);

        var legacyLibrary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MyMusic");
        if (!File.Exists(LibraryLocationFile) && Directory.Exists(legacyLibrary) && !PathsEqual(legacyLibrary, libraryRoot))
            _legacyLibraryToMigrate = legacyLibrary;

        Directory.CreateDirectory(libraryRoot);
        File.WriteAllText(LibraryLocationFile, libraryRoot);
        Database = new Database(Path.Combine(DataRoot, "mymusic.db"));
        Library = new LibraryService(Database, libraryRoot);

        MainWindow = new MainWindow();
        MainWindow.Activate();
        _ = InitializeAfterLaunchAsync();
        }
        catch (Exception ex)
        {
            WriteStartupCrashLog("App.OnLaunched", ex);
            throw;
        }
    }

    private static void WriteStartupCrashLog(string stage, Exception? exception, string? extra = null)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyMusic");
            Directory.CreateDirectory(root);
            var logPath = Path.Combine(root, "startup-crash.log");
            using var writer = new StreamWriter(logPath, append: true);
            writer.WriteLine("============================================================");
            writer.WriteLine($"UTC: {DateTime.UtcNow:O}");
            writer.WriteLine($"Stage: {stage}");
            writer.WriteLine($"Process: {Environment.ProcessPath}");
            writer.WriteLine($"Current directory: {Environment.CurrentDirectory}");
            writer.WriteLine($"Base directory: {AppContext.BaseDirectory}");
            writer.WriteLine($"OS: {Environment.OSVersion}");
            writer.WriteLine($".NET: {Environment.Version}");
            if (!string.IsNullOrWhiteSpace(extra))
                writer.WriteLine($"Extra: {extra}");

            if (exception is null)
            {
                writer.WriteLine("Exception: <not an Exception object>");
            }
            else
            {
                var current = exception;
                var depth = 0;
                while (current is not null)
                {
                    writer.WriteLine($"Exception[{depth}] type: {current.GetType().FullName}");
                    writer.WriteLine($"Exception[{depth}] HResult: 0x{current.HResult:X8}");
                    writer.WriteLine($"Exception[{depth}] message: {current.Message}");
                    writer.WriteLine($"Exception[{depth}] stack:");
                    writer.WriteLine(current.StackTrace ?? "<no stack trace>");
                    current = current.InnerException;
                    depth++;
                }
                writer.WriteLine("Full exception:");
                writer.WriteLine(exception);
            }
            writer.Flush();
        }
        catch
        {
            // Crash logging must never replace the original startup exception.
        }
    }

    private async Task InitializeAfterLaunchAsync()
    {
        try
        {
            await Database.InitializeAsync();
            if (!string.IsNullOrWhiteSpace(_legacyLibraryToMigrate) && Directory.Exists(_legacyLibraryToMigrate))
            {
                await MoveLibraryAsync(_legacyLibraryToMigrate, Library.LibraryRoot, false);
                _legacyLibraryToMigrate = null;
            }
            if (MainWindow is MainWindow window)
                await window.OnDatabaseReadyAsync();
        }
        catch (Exception ex)
        {
            WriteStartupCrashLog("InitializeAfterLaunchAsync", ex);
            if (MainWindow is MainWindow window)
                window.ShowStartupError(ex);
        }
    }
    public static async Task MoveLibraryAsync(string oldRoot, string newRoot, bool writeSetting = true)
    {
        oldRoot = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        newRoot = Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathsEqual(oldRoot, newRoot)) return;

        var oldPrefix = oldRoot + Path.DirectorySeparatorChar;
        var newPrefix = newRoot + Path.DirectorySeparatorChar;
        if (newPrefix.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) ||
            oldPrefix.StartsWith(newPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new library folder cannot be inside the current library folder, or contain it.");

        Directory.CreateDirectory(newRoot);
        foreach (var directory in Directory.EnumerateDirectories(oldRoot, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(newRoot, Path.GetRelativePath(oldRoot, directory)));
        foreach (var file in Directory.EnumerateFiles(oldRoot, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(newRoot, Path.GetRelativePath(oldRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }

        await Database.RemapManagedPathsAsync(oldRoot, newRoot);
        Library = new LibraryService(Database, newRoot);
        if (writeSetting) File.WriteAllText(LibraryLocationFile, newRoot);
        try { Directory.Delete(oldRoot, true); } catch { }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

}
