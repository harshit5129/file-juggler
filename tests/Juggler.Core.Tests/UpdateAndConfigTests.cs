using Juggler.Core.Configuration;
using Juggler.Core.Execution;
using Juggler.Core.Rules;
using Juggler.Core.Setup;
using Juggler.Ui.Services;
using Xunit;

namespace Juggler.Core.Tests;

/// <summary>
/// Version comparison for the update check.
/// <para>
/// The failure this guards against is subtle and permanent: a plain string compare puts "0.10.0"
/// before "0.9.0", so after the tenth release every user is told they are already up to date and
/// never sees an update again.
/// </para>
/// </summary>
public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.1", true)]
    [InlineData("0.1.1", "0.1.1", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.2.0", "0.2", false)]      // same version, different spelling
    [InlineData("0.2.1", "0.2", true)]      // genuinely newer than the short spelling
    public void ComparesNumericallyNotAsText(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(candidate, current));

    /// <summary>
    /// A pre-release tag must not throw. It should read as the release it leads to.
    /// </summary>
    [Theory]
    [InlineData("0.2.0-beta", "0.1.0", true)]
    [InlineData("v0.3.0", "0.2.0", true)]
    [InlineData("garbage", "0.1.0", false)]
    public void ToleratesTagsThatAreNotPureVersions(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(candidate, current));

    /// <summary>
    /// An offline machine must be indistinguishable from being up to date. Any other outcome
    /// shows the user an error they can do nothing about.
    /// </summary>
    [Fact]
    public async Task FailedCheckReportsNoUpdate()
    {
        UpdateCheckResult result = await UpdateCheck.CheckAsync();

        // This test may run with no network at all, which is the point: it must not throw,
        // and it must not claim an update is available on the strength of a failed request.
        Assert.False(result.UpdateAvailable);
        Assert.Equal(UpdateStatus.None, result.Status);
    }

    [Fact]
    public void CurrentVersionIsAlwaysReported()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", UpdateCheck.CurrentVersion);
    }
}

/// <summary>
/// The shipped example config.
/// <para>
/// It is copied straight into the config folder by users and by the smoke harness. Two things
/// have to stay true: it must load, and it must not trip the first-run wizard, which is modal
/// and would sit in front of every UI step the harness tries to take.
/// </para>
/// </summary>
public sealed class ShippedExampleTests
{
    private static string ExampleConfigPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Juggler.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "config", "example.rules.json");
    }

    [Fact]
    public void ExampleConfig_LoadsAndIsValid()
    {
        var result = new ConfigStore(ExampleConfigPath()).Load();

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Config.Rules);
        Assert.True(result.CanApply, string.Join("; ", result.Issues.Select(i => i.Message)));
    }

    /// <summary>
    /// Validation errors are session state, never file content. A file that carried them would
    /// show them again on every load, for a rule that may since have been fixed. Saving a rule
    /// that already holds errors must not write them out.
    /// </summary>
    [Fact]
    public void ValidationErrorsAreNotPersisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-errors-{Guid.NewGuid():N}.json");
        try
        {
            var rule = new Rule
            {
                Id = "stale",
                Name = "Carries stale diagnostics",
                Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\In" }] },
                Then = new ActionSpec { Action = ActionKind.Move, Into = @"C:\Out" },
                Errors = ["an error from a previous edit"],
            };

            var store = new ConfigStore(path);
            store.Load();
            store.Save(new AppConfig { Rules = [rule] });

            // Assert on the JSON property name, not the bare word, so a rule whose name happens to
            // contain the word cannot make this pass or fail by accident.
            Assert.DoesNotContain("\"errors\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"hasErrors\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            // And they are gone on the way back in.
            var reloaded = store.Load();
            Assert.Empty(reloaded.Config.Rules[0].Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// An invalid config must be refused outright rather than written and left to fail later.
    /// </summary>
    [Fact]
    public void InvalidConfigIsNeverWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-invalid-{Guid.NewGuid():N}.json");
        try
        {
            var rule = new Rule
            {
                Id = "broken",
                Name = "Relative destination",
                Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\In" }] },
                Then = new ActionSpec { Action = ActionKind.Move, Into = @"relative\path" },
            };

            var store = new ConfigStore(path);
            store.Load();

            var issues = store.Save(new AppConfig { Rules = [rule] });

            Assert.Contains(issues, i => i.Severity == IssueSeverity.Error);
            Assert.False(File.Exists(path), "an invalid config must not reach disk");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The example must survive a load/save round trip unchanged. This is the property that
    /// shipped a whole-config rewrite once already.
    /// </summary>
    [Fact]
    public void ExampleConfig_SurvivesARoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-example-{Guid.NewGuid():N}.json");
        try
        {
            File.Copy(ExampleConfigPath(), path);

            var store = new ConfigStore(path);
            var before = store.Load().Config;
            store.Save(before);
            var after = store.Load().Config;

            Assert.Equal(before.Rules.Count, after.Rules.Count);

            for (int i = 0; i < before.Rules.Count; i++)
            {
                Assert.Equal(before.Rules[i].Id, after.Rules[i].Id);
                Assert.Equal(before.Rules[i].Enabled, after.Rules[i].Enabled);
                Assert.Equal(before.Rules[i].Then.Action, after.Rules[i].Then.Action);
                Assert.Equal(before.Rules[i].Then.Into, after.Rules[i].Then.Into);
                Assert.Equal(before.Rules[i].Monitor.IncludeSubfolders, after.Rules[i].Monitor.IncludeSubfolders);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Setup must never propose a rule the validator would reject.</summary>
public sealed class SetupSuggestionSafetyTests
{
    private static readonly DetectedFolder Downloads = new(StandardFolderKind.Downloads, @"C:\Users\me\Downloads");
    private static readonly DetectedFolder Pictures = new(StandardFolderKind.Pictures, @"C:\Users\me\Pictures");
    private static readonly DetectedFolder Documents = new(StandardFolderKind.Documents, @"C:\Users\me\Documents");

    [Fact]
    public void EverySuggestedRuleIsValid()
    {
        var suggestions = SetupSuggestions.Build([Downloads, Pictures, Documents]);

        Assert.NotEmpty(suggestions);

        var config = new AppConfig { Rules = [.. suggestions.Select(s => s.Rule)] };

        Assert.True(
            ConfigValidator.CanApply(config, out var issues),
            string.Join("; ", issues.Select(i => i.Message)));
    }

    [Fact]
    public void EveryDestinationIsAbsolute()
    {
        foreach (var suggestion in SetupSuggestions.Build([Downloads, Pictures, Documents]))
        {
            Assert.True(Path.IsPathRooted(suggestion.Rule.Then.Into!));
        }
    }

    /// <summary>
    /// Setup proposes; it does not act. An enabled rule created by the wizard would start
    /// moving files before the user had read what it does.
    /// </summary>
    [Fact]
    public void SuggestedRulesStartDisabled()
    {
        Assert.All(
            SetupSuggestions.Build([Downloads, Pictures, Documents]),
            s => Assert.False(s.Rule.Enabled));
    }

    /// <summary>A trailing separator on the probed folder must not produce "Downloads\\Installers".</summary>
    [Fact]
    public void TrailingSeparatorIsNotDoubled()
    {
        var suggestion = SetupSuggestions
            .Build([new DetectedFolder(StandardFolderKind.Downloads, @"C:\Users\me\Downloads\")])
            .First(s => s.Rule.Id == "setup-installers");

        Assert.Equal(@"C:\Users\me\Downloads\Installers", suggestion.Rule.Then.Into);
    }

    [Fact]
    public void FoldersWithoutTemplatesProduceNothing()
    {
        Assert.Empty(SetupSuggestions.Build([
            new DetectedFolder(StandardFolderKind.Videos, @"C:\Users\me\Videos"),
            new DetectedFolder(StandardFolderKind.Music, @"C:\Users\me\Music"),
        ]));
    }

    [Fact]
    public void NoFoldersMeansNoRules()
    {
        Assert.Empty(SetupSuggestions.Build([]));
    }
}