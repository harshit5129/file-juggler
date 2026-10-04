using System.Text.Json.Serialization;

namespace Juggler.Core.Configuration;

/// <summary>How aggressively the app yields to the user. See docs/background-operation.md.</summary>
public enum RunMode
{
    /// <summary>Tray icon, event-driven, resident. Reacts in milliseconds.</summary>
    Resident,

    /// <summary>Run one pass and exit, for Task Scheduler. Zero resident memory.</summary>
    Scheduled,
}

/// <summary>Process priority class. Lower means the user wins.</summary>
public enum PriorityMode
{
    BelowNormal,
    Idle,
}

/// <summary>Global settings, not per-rule.</summary>
public sealed record GeneralSettings
{
    public RunMode Mode { get; init; } = RunMode.Resident;

    /// <summary>Master switch. Individual events still require their own opt-in.</summary>
    public bool Notify { get; init; }

    /// <summary>Only events that indicate degraded operation may notify. See the table in docs.</summary>
    public bool NotifyOnError { get; init; } = true;
    public bool NotifyOnWatchedFolderMissing { get; init; } = true;
    public bool NotifyOnLowMemoryMode { get; init; } = true;

    /// <summary>How long a file must be quiet before it is touched. Avoids half-copied files.</summary>
    public int DebounceSeconds { get; init; } = 5;

    /// <summary>Safety-net sweep interval in minutes. Catches events the OS dropped.</summary>
    public int SweepIntervalMinutes { get; init; } = 15;

    /// <summary>Parallel file operations. 1 is correct for mechanical disks; see docs/performance.</summary>
    public int MaxConcurrent { get; init; } = 1;

    public PriorityMode Priority { get; init; } = PriorityMode.BelowNormal;

    public bool SuspendOnLock { get; init; } = true;
    public bool SuspendOnScreenSaver { get; init; } = true;

    /// <summary>Throttle to one file per 5 s rather than suspending, so a laptop on battery still tidies up.</summary>
    public bool ThrottleOnBattery { get; init; } = true;
    public bool ThrottleWhenMonitorOff { get; init; } = true;

    public bool RegisterAutostart { get; init; } = true;

    /// <summary>Log file cap in megabytes, per rotation.</summary>
    public int LogMaxMegabytes { get; init; } = 5;

    /// <summary>Number of rotated log files to keep.</summary>
    public int LogRotations { get; init; } = 3;

    /// <summary>Global dry-run. When true no action is ever executed, whatever the rules say.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Counters shown in the status bar. Written by the daemon, read by the UI.</summary>
public sealed record RunStats
{
    public int FilesProcessed { get; init; }
    public long BytesProcessed { get; init; }
    public int FilesMatched { get; init; }
    public int RulesFailed { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

/// <summary>The complete on-disk configuration. <c>config/rules.json</c> or the local override.</summary>
public sealed record AppConfig
{
    /// <summary>Bumped on breaking changes. The loader refuses versions it does not understand.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("general")]
    public GeneralSettings General { get; init; } = new();

    [JsonPropertyName("rules")]
    public List<Rules.Rule> Rules { get; init; } = [];

    [JsonPropertyName("stats")]
    public RunStats Stats { get; init; } = new();

    /// <summary>
    /// Whether first-run setup has been completed or explicitly skipped.
    /// <para>
    /// This cannot be inferred from the config file being absent. The editor writes this file the
    /// moment any setting is touched, which is typically long before the first rule exists, so
    /// "no file" only distinguishes a genuinely untouched install from one whose file was
    /// deleted. An explicit flag is the only reliable signal. See SetupWizard.
    /// </para>
    /// </summary>
    [JsonPropertyName("setupCompleted")]
    public bool SetupCompleted { get; init; }
}
