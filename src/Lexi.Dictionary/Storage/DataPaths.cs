namespace Lexi.Dictionary.Storage;

/// <summary>Well-known locations. Everything lives under %LOCALAPPDATA%\Lexi unless overridden.</summary>
public static class DataPaths
{
    public const string DatabaseFileName = "lexi.db";
    public const string OverrideEnvironmentVariable = "LEXI_DB";

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Root => Path.Combine(LocalAppData, "Lexi");

    public static string RawDirectory => Path.Combine(Root, "raw");

    public static string DataDirectory => Path.Combine(Root, "data");

    public static string DefaultDatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>
    /// Database lookup order: LEXI_DB environment variable → %LOCALAPPDATA%\Lexi\data\lexi.db →
    /// Data\lexi.db next to the executable (database bundled into the package).
    /// </summary>
    public static IEnumerable<string> DatabaseCandidates()
    {
        var env = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return DefaultDatabasePath;
        yield return Path.Combine(AppContext.BaseDirectory, "Data", DatabaseFileName);
    }

    public static string? FindDatabase() => DatabaseCandidates().FirstOrDefault(File.Exists);

    /// <summary>
    /// Detects whether this process's writes to %LOCALAPPDATA% are redirected into a packaged app's private
    /// folder (Packages\&lt;app&gt;\LocalCache\Local). Windows does this for processes started by an MSIX app, e.g. a
    /// terminal inside the Claude desktop app. Files written there are invisible to Command Palette and to the
    /// extension. Returns the package folder name, or null when writes reach the real %LOCALAPPDATA%.
    /// </summary>
    public static string? DetectAppDataRedirection()
    {
        var name = $"lexi-probe-{Guid.NewGuid():N}.tmp";
        var probe = Path.Combine(LocalAppData, name);
        try
        {
            File.WriteAllText(probe, string.Empty);
            foreach (var package in Directory.EnumerateDirectories(Path.Combine(LocalAppData, "Packages")))
            {
                if (File.Exists(Path.Combine(package, "LocalCache", "Local", name)))
                {
                    return Path.GetFileName(package);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>
    /// A Lexi folder with a database inside some packaged app's private LocalCache (written through AppData
    /// redirection, see <see cref="DetectAppDataRedirection"/>), or null.
    /// </summary>
    public static string? FindRedirectedRoot()
    {
        try
        {
            foreach (var package in Directory.EnumerateDirectories(Path.Combine(LocalAppData, "Packages")))
            {
                var root = Path.Combine(package, "LocalCache", "Local", "Lexi");
                if (File.Exists(Path.Combine(root, "data", DatabaseFileName)))
                {
                    return root;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not readable: nothing to report.
        }

        return null;
    }
}
