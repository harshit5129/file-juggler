using Juggler.Core.Configuration;
using Juggler.Core.Rules;

namespace Juggler.Core.Tests;

/// <summary>
/// Guards the property that matters most for a tool whose job is moving user files:
/// <b>the config on disk is never silently replaced by something else.</b>
/// <para>
/// These tests exist because a real regression shipped a whole-config rewrite. The shipped
/// <c>example.rules.json</c> wrote <c>"mode": "Resident"</c>, the serializer had no string-enum
/// converter, so deserialization threw, the load was reported as failed, the in-memory config
/// stayed at its empty default, and the next settings save wrote those defaults back over the
/// user's rules. The file was reduced to <c>"rules": []</c>.
/// </para>
/// <para>
/// The example config is hand-edited by users, so it is part of the contract: whatever ships in
/// <c>config/example.rules.json</c> must load, and must survive a load/save round trip unchanged.
/// </para>
/// </summary>
public sealed class ConfigStoreRoundTripTests
{
    private static string ExampleConfigPath()
    {
        // Walk up from the test output to the repo root, then into config/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Juggler.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(dir.FullName, "config", "example.rules.json");
    }

    private static List<ConfigIssue> Save(ConfigStore store, AppConfig config) =>
        [.. store.Save(config).Where(i => i.Severity == IssueSeverity.Error)];

    /// <summary>
    /// The shipped example must deserialize without error. It is documentation the user copies,
    /// so a parse failure here is a shipped bug, not a bad test.
    /// </summary>
    [Fact]
    public void ShippedExampleConfig_LoadsWithoutError()
    {
        var store = new ConfigStore(ExampleConfigPath());
        var result = store.Load();

        Assert.Null(result.Error);
        Assert.Equal(3, result.Config.Rules.Count);
    }

    /// <summary>
    /// Enums must survive as names, not numbers. A config full of <c>"mode": 0</c> is unreadable
    /// by a human editing it by hand, which is the supported workflow.
    /// </summary>
    [Fact]
    public void EnumsRoundTripAsNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-enum-{Guid.NewGuid():N}.json");
        try
        {
            var original = new AppConfig
            {
                General = new GeneralSettings
                {
                    Mode = RunMode.Scheduled,
                    Priority = PriorityMode.BelowNormal,
                },
                Rules =
                [
                    new Rule
                    {
                        Id = "enumcheck",
                        Name = "Enum check",
                        Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\tmp" }] },
                        If = new ConditionSpec { NamePatternKind = PatternKind.Regex, NamePattern = "^x" },
                        Then = new ActionSpec
                        {
                            Action = ActionKind.SortIntoFolders,
                            Into = @"C:\sorted\{extension}",
                            SortBy = SortKey.DateModified,
                        },
                    },
                ],
            };

            var store = new ConfigStore(path);
            Assert.Empty(Save(store, original));

            string json = File.ReadAllText(path);
            Assert.Contains("\"Scheduled\"", json, StringComparison.Ordinal);
            Assert.Contains("\"BelowNormal\"", json, StringComparison.Ordinal);
            Assert.Contains("\"SortIntoFolders\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Regex\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"mode\": 0", json, StringComparison.Ordinal);

            var reloaded = store.Load();
            Assert.Null(reloaded.Error);
            Assert.Equal(RunMode.Scheduled, reloaded.Config.General.Mode);
            Assert.Equal(ActionKind.SortIntoFolders, reloaded.Config.Rules[0].Then.Action);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A round trip must not lose or mutate rules. This is the property that was broken.
    /// </summary>
    [Fact]
    public void SaveThenLoad_PreservesEveryRule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-rt-{Guid.NewGuid():N}.json");
        try
        {
            // Start from the shipped example, not an empty file, so this exercises real rules.
            File.Copy(ExampleConfigPath(), path);

            var store = new ConfigStore(path);
            var loaded = store.Load();
            Assert.Null(loaded.Error);

            var original = loaded.Config;
            Assert.Equal(3, original.Rules.Count);

            Assert.Empty(Save(store, original));

            var after = store.Load();
            Assert.Null(after.Error);
            Assert.Equal(original.Rules.Count, after.Config.Rules.Count);

            for (int i = 0; i < original.Rules.Count; i++)
            {
                Rule a = original.Rules[i];
                Rule b = after.Config.Rules[i];

                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Enabled, b.Enabled);
                Assert.Equal(a.Monitor.Paths.Count, b.Monitor.Paths.Count);
                Assert.Equal(a.Monitor.IncludeSubfolders, b.Monitor.IncludeSubfolders);
                Assert.Equal(a.Then.Action, b.Then.Action);
                Assert.Equal(a.Then.Into, b.Then.Into);
                Assert.Equal(a.If.Extensions, b.If.Extensions);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that vanishes mid-read must be reported as non-authoritative, so a caller keeps the
    /// config it already holds. This is the second half of the data-loss bug: the atomic save
    /// briefly makes the path unresolvable, the watcher reloads, and the empty default got adopted
    /// and then written back out as <c>"rules": []</c>.
    /// </summary>
    [Fact]
    public void MissingFile_IsNotAuthoritative()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-gone-{Guid.NewGuid():N}.json");
        try
        {
            File.Copy(ExampleConfigPath(), path);

            var store = new ConfigStore(path);
            var good = store.Load();
            Assert.True(good.IsAuthoritative);
            Assert.Equal(3, good.Config.Rules.Count);

            File.Delete(path);
            var gone = store.Load();

            Assert.Equal(ConfigStore.LoadSource.Missing, gone.Source);
            Assert.False(gone.IsAuthoritative);
            Assert.Null(gone.Error);
            Assert.True(store.CanSave());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An empty file is unreadable, not an instruction to delete every rule.</summary>
    [Fact]
    public void BlankFile_IsNotAuthoritative()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-blank-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "   \n  ");
            var result = new ConfigStore(path).Load();

            Assert.Equal(ConfigStore.LoadSource.Blank, result.Source);
            Assert.False(result.IsAuthoritative);
            Assert.Null(result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A file that cannot be parsed must not be replaced by defaults on the next save. If the app
    /// cannot read the user's config, it must refuse to write over it rather than destroy it.
    /// </summary>
    [Fact]
    public void SaveRefusesWhenLastLoadFailed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"schemaVersion":1,"general":{"mode":"Resident"},"rules":[]}""");

            var store = new ConfigStore(path);
            Assert.Null(store.Load().Error);
            Assert.True(store.CanSave());

            // Corrupt the file, then reload. The load now fails.
            File.WriteAllText(path, "{ this is not json ");
            Assert.NotNull(store.Load().Error);

            Assert.False(store.CanSave());
            Assert.NotEmpty(Save(store, new AppConfig()));

            // The user's file is untouched: still the corrupt text, not a defaults rewrite.
            Assert.Equal("{ this is not json ", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A malformed file yields defaults plus an error, and never throws.
    /// </summary>
    [Fact]
    public void MalformedFile_YieldsErrorNotException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fj-junk-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json at all ");

            var result = new ConfigStore(path).Load();

            Assert.NotNull(result.Error);
            Assert.True(result.Failed);
            Assert.False(result.CanApply);
        }
        finally
        {
            File.Delete(path);
        }
    }
}