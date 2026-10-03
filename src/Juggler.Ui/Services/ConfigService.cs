using System.Threading;
using Juggler.Core.Configuration;
using Juggler.Core.Rules;

namespace Juggler.Ui.Services;

public sealed class ConfigChangedEventArgs(AppConfig config) : EventArgs
{
    public AppConfig Config { get; } = config;
}

/// <summary>
/// Owns the loaded configuration for the editor process and keeps it in sync with the file.
/// <para>
/// The daemon hot-reloads the same file, so this is a read-through view of shared state rather
/// than a private copy. Writes go through <see cref="ConfigStore"/>, which renames atomically.
/// </para>
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly ConfigStore _store;
    private FileSystemWatcher? _watcher;

    public ConfigService(string? path = null)
    {
        _store = new ConfigStore(path);
        Reload();
        StartWatching();
    }

    public string ConfigPath => _store.Path;

    public AppConfig Current { get; private set; } = new();

    public IReadOnlyList<ConfigIssue> Issues { get; private set; } = [];

    /// <summary>Non-null when the last load failed outright.</summary>
    public string? LoadError { get; private set; }

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public event EventHandler<IReadOnlyList<ConfigIssue>>? IssuesChanged;

    public void Reload()
    {
        ConfigStore.LoadResult result = _store.Load();

        // Adopt the result only when a real file was parsed. A missing or blank file is not
        // "the user deleted their rules": it is what a reader briefly sees while an atomic
        // replace is in flight, which the watcher fires on every save. Adopting the empty
        // defaults from that window is what wiped a real 3-rule config down to "rules": [].
        //
        // On first run the in-memory default is already the empty config, so keeping it costs
        // nothing and the file still gets created on the first save.
        if (result.IsAuthoritative)
        {
            Current = result.Config;
        }

        LoadError = result.Error;
        Issues = result.Issues;

        Changed?.Invoke(this, new ConfigChangedEventArgs(Current));
        IssuesChanged?.Invoke(this, Issues);
    }

    public bool Upsert(Rule rule)
    {
        List<Rule> rules = [.. Current.Rules];
        int index = rules.FindIndex(r => r.Id == rule.Id);

        if (index >= 0)
        {
            rules[index] = rule;
        }
        else
        {
            rules.Add(rule);
        }

        return TrySave(Current with { Rules = rules });
    }

    public bool DeleteRule(string id)
    {
        List<Rule> rules = [.. Current.Rules.Where(r => r.Id != id)];
        return TrySave(Current with { Rules = rules });
    }

    public bool SaveGeneral(GeneralSettings general) =>
        TrySave(Current with { General = general });

    public bool SaveRunStats(RunStats stats) =>
        TrySave(Current with { Stats = stats });

    private bool TrySave(AppConfig candidate)
    {
        try
        {
            _store.Save(candidate);
        }
        catch (Exception ex)
        {
            // Surfaced in the UI rather than thrown: a failed write must not take the window down.
            LoadError = $"Could not save '{_store.Path}': {ex.Message}";
            IssuesChanged?.Invoke(this, Issues);
            return false;
        }

        Reload();
        return true;
    }

    /// <summary>
    /// Watches the config file so an external edit, or the daemon's own diagnostics, is picked up.
    /// The daemon also watches it, so both processes converge on the file.
    /// </summary>
    private void StartWatching()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_store.Path);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_store.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            // The store writes via temp-and-rename, so the target is replaced rather than modified.
            // Debouncing swallows the duplicate notification this produces.
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch (Exception)
        {
            // Watching is a convenience, not a requirement. Failing to watch must not stop the editor.
            _watcher = null;
        }
    }

    private Timer? _debounce;

    /// <summary>
    /// Coalesces watcher notifications with a trailing-edge debounce.
    /// <para>
    /// This must not be a leading-edge throttle. The store writes via temp-and-rename, so the
    /// target path is briefly unresolvable; a throttle that acts on the <em>first</em> event reads
    /// inside exactly that window and sees an empty file. Waiting for quiescence means the read
    /// happens after the rename has settled.
    /// </para>
    /// </summary>
    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(
                _ => ReloadAfterQuiet(),
                null,
                System.Threading.Timeout.InfiniteTimeSpan,
                DebounceDelay);
        }
    }

    /// <summary>How long the file must be quiet before it is read.</summary>
    /// <summary>How long the file must be quiet before it is read.</summary>
    private static readonly System.TimeSpan DebounceDelay = System.TimeSpan.FromMilliseconds(300);

    /// <summary>Polls a few times: the rename can settle a moment after the last notification.</summary>
    private void ReloadAfterQuiet()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ConfigStore.LoadResult probe = _store.Load();

            // A real file is there. Adopt it and stop.
            if (probe.IsAuthoritative)
            {
                Reload();
                return;
            }

            // Still missing or blank. If it never appears there was nothing to read, so the
            // current config stands.
            Thread.Sleep(100);
        }

        Reload();
    }

    private readonly Lock _gate = new();

    public DiagnosticsReport Diagnose() => new(_store.Path, LoadError, Issues, Current);

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
