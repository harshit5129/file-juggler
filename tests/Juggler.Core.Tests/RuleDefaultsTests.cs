using Juggler.Core.Configuration;
using Juggler.Core.Rules;

namespace Juggler.Core.Tests;

/// <summary>
/// Locks in the safe defaults for a tool that moves real files.
/// </summary>
public sealed class RuleDefaultsTests
{
    /// <summary>
    /// Recursion must be opt-in.
    /// <para>
    /// A rule that descends into subfolders by default reaches arbitrarily far from the folder
    /// the user actually named. For a tool whose whole job is moving and deleting files,
    /// "only the folder I pointed at" is the only predictable default; reaching deeper should be
    /// something you ask for.
    /// </para>
    /// </summary>
    [Fact]
    public void NewRule_DoesNotWatchSubfoldersByDefault()
    {
        var rule = Rule.New();

        Assert.False(rule.Monitor.IncludeSubfolders);
    }

    /// <summary>
    /// The same must hold for a monitor built by hand, not only via <see cref="Rule.New"/>.
    /// </summary>
    [Fact]
    public void MonitorSpec_DefaultsToNoRecursion()
    {
        var monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\tmp" }] };

        Assert.False(monitor.IncludeSubfolders);
    }

    /// <summary>Opting in must stick, so the setting is not merely hard-coded off.</summary>
    [Fact]
    public void IncludeSubfolders_CanBeTurnedOn()
    {
        var rule = Rule.New() with
        {
            Monitor = new MonitorSpec
            {
                Paths = [new MonitorEntry { Path = @"C:\tmp" }],
                IncludeSubfolders = true,
            },
        };

        Assert.True(rule.Monitor.IncludeSubfolders);
    }

    /// <summary>
    /// The shipped example must state this explicitly per rule rather than leaning on the
    /// default, so the file documents its own behaviour instead of tracking a code change.
    /// </summary>
    [Fact]
    public void ExampleConfig_StatesRecursionExplicitlyForEveryRule()
    {
        var path = ExampleConfigPath();
        string json = File.ReadAllText(path);

        var store = new ConfigStore(path);
        var result = store.Load();

        Assert.Null(result.Error);

        foreach (Rule rule in result.Config.Rules)
        {
            Assert.Contains("\"includeSubfolders\"", json, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        }
    }

    private static string ExampleConfigPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Juggler.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(dir.FullName, "config", "example.rules.json");
    }
}