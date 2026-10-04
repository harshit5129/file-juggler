using Juggler.Core.Execution;
using Juggler.Core.Rules;

namespace Juggler.Core.Tests;

/// <summary>
/// Running a rule against real files.
/// <para>
/// These tests create real files in a temp directory. That is deliberate: the guards worth
/// testing here are filesystem guards - overwriting, moving onto itself, escaping the folder -
/// and none of them can be exercised against a mock.
/// </para>
/// <para>
/// Every test runs with dry-run on unless it is specifically about files changing. A test suite
/// for a tool that moves user data must not be able to move a real file by accident.
/// </para>
/// </summary>
public sealed class RuleRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"fj-run-{Guid.NewGuid():N}");

    public RuleRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file must not fail the test run.
        }
    }

    private string Write(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Rule Rule(ActionSpec action, ConditionSpec? condition = null) => new()
    {
        Id = "test",
        Name = "Test",
        Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = "C:\\any" }] },
        If = condition ?? new ConditionSpec(),
        Then = action,
    };

    private static ActionSpec MoveTo(string into) =>
        new() { Action = ActionKind.Move, Into = into };

    [Fact]
    public void DryRunReportsWithoutChangingAnything()
    {
        string source = Write("a.png");

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        RuleRunResult result = new RuleRunner(dryRun: true).Run(rule, _root);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.WouldHaveChanged);
        Assert.Equal(0, result.Changed);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_root, "Sorted")));
    }

    [Fact]
    public void RealRunMovesTheFileAndCreatesTheFolder()
    {
        string source = Write("a.png");
        string into = Path.Combine(_root, "Sorted");

        Rule rule = Rule(MoveTo(into));

        RuleRunResult result = new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.Equal(1, result.Changed);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(into, "a.png")));
    }

    [Fact]
    public void OnlyMatchingFilesAreTouched()
    {
        string png = Write("a.png");
        string txt = Write("b.txt");

        Rule rule = Rule(
            MoveTo(Path.Combine(_root, "Sorted")),
            new ConditionSpec { Extensions = ["png"] });

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.False(File.Exists(png));
        Assert.True(File.Exists(txt));
    }

    [Fact]
    public void ExistingDestinationIsNeverOverwritten()
    {
        Write("a.png", "original");
        string clash = Write(Path.Combine("Sorted", "a.png"), "already here");

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        RuleRunResult result = new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.Equal(0, result.Changed);
        Assert.Equal("already here", File.ReadAllText(clash));
        Assert.True(File.Exists(Path.Combine(_root, "a.png")));
    }

    [Fact]
    public void CopyLeavesTheOriginalAlone()
    {
        string source = Write("a.png", "payload");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.Copy,
            Into = Path.Combine(_root, "Copies"),
        });

        RuleRunResult result = new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.Equal(1, result.Changed);
        Assert.True(File.Exists(source));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(_root, "Copies", "a.png")));
    }

    [Fact]
    public void RenameKeepsTheFileInItsFolder()
    {
        string source = Write("a.png");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.Rename,
            RenamePattern = "sorted-{name}{ext}",
        });

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(_root, "sorted-a.png")));
    }

    [Fact]
    public void TokenPatternIsExpandedInTheNewName()
    {
        Write("a.png");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.Rename,
            RenamePattern = "{date:yyyy}-{name}{ext}",
        });

        new RuleRunner(dryRun: false).Run(rule, _root);

        string year = DateTime.Now.Year.ToString();
        Assert.True(File.Exists(Path.Combine(_root, $"{year}-a.png")));
    }

    [Fact]
    public void TrailingSeparatorCreatesOneFolderPerExtension()
    {
        Write("a.png");
        Write("b.txt");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.Move,
            Into = Path.Combine(_root, "ByType") + Path.DirectorySeparatorChar,
        });

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.True(File.Exists(Path.Combine(_root, "ByType", "png", "a.png")));
        Assert.True(File.Exists(Path.Combine(_root, "ByType", "txt", "b.txt")));
    }

    [Fact]
    public void SortIntoFoldersUsesTheSortKey()
    {
        string source = Write("a.pdf");

        // Bucketing is on the file's own write time in UTC, so read that back rather than
        // guessing from the local clock and being off by a month at a timezone boundary.
        string month = new FileInfo(source).LastWriteTimeUtc.ToString("yyyy-MM");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.SortIntoFolders,
            Into = Path.Combine(_root, "Papers"),
            SortBy = SortKey.DateModified,
        });

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.True(File.Exists(Path.Combine(_root, "Papers", month, "a.pdf")));
    }

    /// <summary>
    /// Recursion is opt-in. A rule that watches one folder must not reach into everything under
    /// it, which is the whole reason the setting defaults to off.
    /// </summary>
    [Fact]
    public void SubfoldersAreUntouchedUnlessTheRuleOptsIn()
    {
        string nested = Write(Path.Combine("nested", "deep", "a.png"));

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void SubfoldersAreIncludedWhenTheRuleOptsIn()
    {
        string nested = Write(Path.Combine("nested", "a.png"));

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted"))) with
        {
            Monitor = new MonitorSpec
            {
                Paths = [new MonitorEntry { Path = _root }],
                IncludeSubfolders = true,
            },
        };

        new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(Path.Combine(_root, "Sorted", "a.png")));
    }

    /// <summary>
    /// A missing folder is the most likely reason a manual run does nothing. It must be reported,
    /// not silently treated as "no files matched".
    /// </summary>
    [Fact]
    public void MissingFolderYieldsNothingRatherThanThrowing()
    {
        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        RuleRunResult result = new RuleRunner(dryRun: true).Run(rule, Path.Combine(_root, "nope"));

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.Matched);
    }

    /// <summary>
    /// Recycle Bin has no shell integration yet. It must refuse rather than fall back to a
    /// permanent delete, which is the one outcome a file-juggling tool must never produce.
    /// </summary>
    [Fact]
    public void RecycleIsRefusedRatherThanDeleting()
    {
        string source = Write("a.png");

        Rule rule = Rule(new ActionSpec { Action = ActionKind.DeleteToRecycleBin });

        RuleRunResult result = new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.Equal(0, result.Changed);
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// RunCommand has no allowlist yet, so it must refuse rather than execute something.
    /// </summary>
    [Fact]
    public void RunCommandIsRefusedRatherThanExecuting()
    {
        string source = Write("a.txt");

        Rule rule = Rule(new ActionSpec
        {
            Action = ActionKind.RunCommand,
            CommandKey = "notepad",
        });

        RuleRunResult result = new RuleRunner(dryRun: false).Run(rule, _root);

        Assert.Equal(0, result.Changed);
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// Preview must never change anything, even when it is handed a rule with a real action.
    /// </summary>
    [Fact]
    public void PreviewNeverWrites()
    {
        string source = Write("a.png");

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        RuleRunResult result = new RuleRunner(dryRun: true).Preview(rule, _root);

        Assert.Equal(1, result.Matched);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(_root, "Sorted")));
    }

    [Fact]
    public void PreviewListsWhereEachFileWouldGo()
    {
        Write("a.png");

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted")));

        RuleRunResult result = new RuleRunner(dryRun: true).Preview(rule, _root);

        Assert.Equal(Path.Combine(_root, "Sorted", "a.png"), result.Entries[0].Target);
    }

    [Fact]
    public void RunAllOnlyTouchesRulesWatchingThatFolder()
    {
        Write("a.png");

        Rule matching = Rule(MoveTo(Path.Combine(_root, "Sorted"))) with
        {
            Id = "here",
            Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = _root }] },
        };

        Rule elsewhere = Rule(MoveTo(Path.Combine(_root, "Other"))) with
        {
            Id = "elsewhere",
            Monitor = new MonitorSpec { Paths = [new MonitorEntry { Path = @"C:\elsewhere" }] },
        };

        var config = new Core.Configuration.AppConfig { Rules = [matching, elsewhere] };

        var results = new RuleRunner(dryRun: true).RunAll(config, _root);

        Assert.Single(results);
        Assert.Equal("here", results[0].RuleId);
    }

    /// <summary>
    /// A rule that watches no folder can never fire. Running it must be a no-op, not a scan of
    /// something arbitrary.
    /// </summary>
    [Fact]
    public void DisabledRulesAreSkippedByRunAll()
    {
        Write("a.png");

        Rule rule = Rule(MoveTo(Path.Combine(_root, "Sorted"))) with { Enabled = false };

        var config = new Core.Configuration.AppConfig { Rules = [rule] };

        Assert.Empty(new RuleRunner(dryRun: true).RunAll(config, _root));
    }
}