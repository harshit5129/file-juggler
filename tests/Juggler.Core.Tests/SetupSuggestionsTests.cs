using Juggler.Core.Configuration;
using Juggler.Core.Rules;
using Juggler.Core.Setup;

namespace Juggler.Core.Tests;

/// <summary>
/// First-run suggestions.
/// <para>
/// The value this feature promises is that the user arrives at a working state without typing
/// anything, which means every generated rule must already be valid. A proposal that fails
/// validation would be worse than no proposal: the user sees an error they did not cause and
/// cannot explain.
/// </para>
/// </summary>
public sealed class SetupSuggestionsTests
{
    private static DetectedFolder Folder(StandardFolderKind kind, string path) => new(kind, path);

    [Fact]
    public void NoFolders_ProducesNoRules()
    {
        Assert.Empty(SetupSuggestions.Build([]));
    }

    /// <summary>
    /// The central guarantee: whatever is proposed passes the real validator.
    /// </summary>
    [Fact]
    public void EverySuggestedRuleValidatesCleanly()
    {
        var folders = new[]
        {
            Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads"),
            Folder(StandardFolderKind.Desktop, @"C:\Users\me\Desktop"),
            Folder(StandardFolderKind.Pictures, @"C:\Users\me\Pictures"),
            Folder(StandardFolderKind.Documents, @"C:\Users\me\Documents"),
            Folder(StandardFolderKind.Videos, @"C:\Users\me\Videos"),
            Folder(StandardFolderKind.Music, @"C:\Users\me\Music"),
        };

        var suggestions = SetupSuggestions.Build(folders);

        Assert.NotEmpty(suggestions);

        AppConfig config = new() { Rules = [.. suggestions.Select(s => s.Rule)] };

        Assert.True(
            ConfigValidator.CanApply(config, out var issues),
            "suggestions must be valid: " + string.Join("; ", issues.Select(i => i.Message)));
    }

    /// <summary>
    /// A destination that is not absolute would make behaviour depend on the working directory,
    /// which the validator rejects and which is exactly the bug class this project guards.
    /// </summary>
    [Fact]
    public void EveryDestinationIsAbsolute()
    {
        var folders = new[]
        {
            Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads"),
            Folder(StandardFolderKind.Pictures, @"C:\Users\me\Pictures"),
            Folder(StandardFolderKind.Documents, @"C:\Users\me\Documents"),
        };

        foreach (var suggestion in SetupSuggestions.Build(folders))
        {
            Assert.NotNull(suggestion.Rule.Then.Into);
            Assert.True(
                Path.IsPathRooted(suggestion.Rule.Then.Into!),
                $"not rooted: {suggestion.Rule.Then.Into}");
        }
    }

    /// <summary>
    /// A trailing separator on the probed folder must not produce a doubled separator.
    /// </summary>
    [Fact]
    public void TrailingSeparatorOnFolderIsNotDoubled()
    {
        var suggestion = SetupSuggestions
            .Build([Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads\")])
            .First(s => s.Rule.Id == "setup-installers");

        Assert.Equal(@"C:\Users\me\Downloads\Installers", suggestion.Rule.Then.Into);
    }

    [Fact]
    public void ProposedRulesStartDisabled()
    {
        // Setup proposes; it does not act. Anything enabled by default would be moving files
        // before the user has read what a rule does.
        var folders = new[]
        {
            Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads"),
            Folder(StandardFolderKind.Pictures, @"C:\Users\me\Pictures"),
            Folder(StandardFolderKind.Documents, @"C:\Users\me\Documents"),
        };

        Assert.All(SetupSuggestions.Build(folders), s => Assert.False(s.Rule.Enabled));
    }

    [Fact]
    public void ProposedRulesDoNotRecurseByDefault()
    {
        var folders = new[]
        {
            Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads"),
            Folder(StandardFolderKind.Pictures, @"C:\Users\me\Pictures"),
        };

        Assert.All(SetupSuggestions.Build(folders), s => Assert.False(s.Rule.Monitor.IncludeSubfolders));
    }

    [Fact]
    public void SuggestedRuleIdsAreUnique()
    {
        var folders = new[]
        {
            Folder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads"),
            Folder(StandardFolderKind.Pictures, @"C:\Users\me\Pictures"),
            Folder(StandardFolderKind.Documents, @"C:\Users\me\Documents"),
        };

        var ids = SetupSuggestions.Build(folders).Select(s => s.Rule.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FoldersWithoutTemplatesProduceNothing()
    {
        // Videos and Music have no template. Inventing one would be noise, not help.
        Assert.Empty(SetupSuggestions.Build([
            Folder(StandardFolderKind.Videos, @"C:\Users\me\Videos"),
            Folder(StandardFolderKind.Music, @"C:\Users\me\Music"),
        ]));
    }

    [Fact]
    public void EveryFolderKindIsProbed()
    {
        var probed = new List<StandardFolderKind>();

        SetupSuggestions.BuildDetection(kind =>
        {
            probed.Add(kind);
            return null;
        });

        Assert.Equal(Enum.GetValues<StandardFolderKind>().Length, probed.Count);
    }

    /// <summary>
    /// A folder that cannot be resolved must be dropped, not proposed: a rule watching a
    /// non-existent path fails on every file event forever.
    /// </summary>
    [Fact]
    public void UnresolvableFoldersAreDropped()
    {
        var detected = SetupSuggestions.BuildDetection(_ => null);

        Assert.Empty(detected);
    }
}

/// <summary>
/// The first-run flag.
/// <para>
/// It cannot be inferred from the config file being absent, because the editor writes that file
/// the moment any setting is touched - long before a rule exists. These tests pin the explicit
/// flag and its round trip so the wizard is not shown twice.
/// </para>
/// </summary>
public sealed class SetupFlagTests
{
    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"fj-{prefix}-{Guid.NewGuid():N}.json");

    [Fact]
    public void DefaultsToFalse()
    {
        Assert.False(new AppConfig().SetupCompleted);
    }

    [Fact]
    public void RoundTripsThroughTheConfigFile()
    {
        var path = TempPath("setup");
        try
        {
            var store = new ConfigStore(path);
            store.Load();

            Assert.Empty(store.Save(new AppConfig { SetupCompleted = true }));

            var reloaded = store.Load();

            Assert.Null(reloaded.Error);
            Assert.True(reloaded.Config.SetupCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The flag must not suppress the real content: marking setup done is the only thing that
    /// changes.
    /// </summary>
    [Fact]
    public void FlagDoesNotDisturbExistingRules()
    {
        var path = TempPath("setup-rules");
        try
        {
            var rule = new Rule
            {
                Id = "keepme",
                Name = "Keep me",
                Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\In" }] },
                Then = new ActionSpec { Action = ActionKind.Move, Into = @"C:\Out" },
            };

            var store = new ConfigStore(path);
            store.Load();
            store.Save(new AppConfig { Rules = [rule] });

            store.Save(store.Load().Config with { SetupCompleted = true });

            var after = store.Load();

            Assert.True(after.Config.SetupCompleted);
            Assert.Single(after.Config.Rules);
            Assert.Equal("keepme", after.Config.Rules[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }
}