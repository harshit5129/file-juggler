using Juggler.Core.Configuration;
using Juggler.Core.Rules;
using Xunit;

namespace Juggler.Core.Tests;

/// <summary>
/// Validation is the app's safety gate, so these tests cover the cases where being permissive
/// would let a destructive rule run unattended.
/// </summary>
public class ConfigValidatorTests
{
    private static Rule Minimal(Action<RuleBuilder>? configure = null)
    {
        RuleBuilder builder = new();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private static bool HasError(AppConfig config, string fragment) =>
        ConfigValidator.Validate(config).Any(i =>
            i.Severity == IssueSeverity.Error && i.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ValidMinimalRulePasses()
    {
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\Users\me\Downloads").WithAction(ActionKind.Move, @"C:\Sorted"))] };

        Assert.True(ConfigValidator.CanApply(config, out _));
    }

    [Fact]
    public void RelativeMonitorPathIsRejected()
    {
        // A relative path would resolve against whatever working directory the daemon happened
        // to have, so the same config would behave differently between Task Scheduler and a shell.
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"Downloads\Inbox").WithAction(ActionKind.Move, @"C:\Sorted"))] };

        Assert.True(HasError(config, "must be absolute"));
    }

    [Fact]
    public void TraversalInMonitorPathIsRejected()
    {
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\Users\me\..\..\Windows").WithAction(ActionKind.Move, @"C:\Sorted"))] };

        Assert.True(HasError(config, "Traversal is not permitted"));
    }

    [Fact]
    public void TraversalInDestinationIsRejected()
    {
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Users\me\..\..\Windows\System32"))] };

        Assert.True(HasError(config, "Traversal is not permitted"));
    }

    [Fact]
    public void InvertedSizeRangeIsRejected()
    {
        // Otherwise the rule silently matches nothing and the user has no idea why.
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out").WithSize(1000, 10))] };

        Assert.True(HasError(config, "minSizeBytes is greater than maxSizeBytes"));
    }

    [Fact]
    public void InvalidRegexIsRejected()
    {
        AppConfig config = new()
        {
            Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out")
                .WithName("([unclosed", PatternKind.Regex))],
        };

        Assert.True(HasError(config, "not a valid regular expression"));
    }

    [Fact]
    public void ValidRegexIsAccepted()
    {
        AppConfig config = new()
        {
            Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out")
                .WithName(@"^Screenshot_\d{4}-\d{2}-\d{2}$", PatternKind.Regex))],
        };

        Assert.True(ConfigValidator.CanApply(config, out _));
    }

    [Fact]
    public void DuplicateRuleIdsAreRejected()
    {
        Rule a = Minimal(r => r.WithId("same").WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out"));
        Rule b = Minimal(r => r.WithId("same").WithMonitor(@"C:\Other").WithAction(ActionKind.Move, @"C:\Out2"));

        AppConfig config = new() { Rules = [a, b] };

        Assert.True(HasError(config, "Duplicate rule id"));
    }

    [Fact]
    public void MoveWithoutDestinationIsRejected()
    {
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, destination: null))] };

        Assert.True(HasError(config, "requires a destination"));
    }

    [Fact]
    public void RunCommandWithShellInterpreterIsRejected()
    {
        // Commands are an allowlist lookup, not a shell line. A shell here would defeat that.
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithCommand("cmd"))] };

        Assert.True(HasError(config, "is an interpreter and is not permitted"));
    }

    [Fact]
    public void UnclosedTokenIsRejected()
    {
        // Otherwise the literal text "{extension" ends up as part of a filename.
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out\{extension"))] };

        Assert.True(HasError(config, "unclosed"));
    }

    [Fact]
    public void ExtensionWithLeadingDotIsRejected()
    {
        AppConfig config = new() { Rules = [Minimal(r => r.WithMonitor(@"C:\In").WithAction(ActionKind.Move, @"C:\Out").WithExtensions(".png"))] };

        Assert.True(HasError(config, "leading dot"));
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        AppConfig config = new() { SchemaVersion = 99 };

        Assert.True(HasError(config, "Unsupported schemaVersion"));
    }

    [Fact]
    public void EmptyMonitorPathsIsAWarningNotAnError()
    {
        // A half-built rule in the editor is legitimate. Blocking it would make the editor unusable.
        AppConfig config = new() { Rules = [new Rule { Id = "a", Name = "Draft" }] };

        IReadOnlyList<ConfigIssue> issues = ConfigValidator.Validate(config);

        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void OutOfRangeSettingsAreRejected()
    {
        AppConfig config = new() { General = new GeneralSettings { MaxConcurrent = 99 } };

        Assert.True(HasError(config, "maxConcurrent"));
    }

    /// <summary>Small builder so each test reads as the one thing it is checking.</summary>
    private sealed class RuleBuilder
    {
        private string _id = "r1";
        private string? _monitor;
        private ActionKind _action = ActionKind.Move;
        private string? _destination = @"C:\Sorted";
        private string? _name;
        private PatternKind _kind = PatternKind.Glob;
        private List<string> _extensions = [];
        private long? _min;
        private string? _commandKey;
        private long? _max;

        public RuleBuilder WithId(string id) { _id = id; return this; }
        public RuleBuilder WithMonitor(string path) { _monitor = path; return this; }
        public RuleBuilder WithAction(ActionKind action, string? destination) { _action = action; _destination = destination; return this; }
        public RuleBuilder WithName(string name, PatternKind kind) { _name = name; _kind = kind; return this; }
        public RuleBuilder WithExtensions(params string[] exts) { _extensions = [.. exts]; return this; }
        public RuleBuilder WithSize(long? min, long? max) { _min = min; _max = max; return this; }

        /// <summary>Sets the action to RunCommand with the given allowlist key.</summary>
        public RuleBuilder WithCommand(string key)
        {
            _action = ActionKind.RunCommand;
            _destination = null;
            _name = null;
            _commandKey = key;
            return this;
        }

        public Rule Build() => new()
        {
            Id = _id,
            Name = "Test rule",
            Monitor = _monitor is null
                ? new MonitorSpec()
                : new MonitorSpec { Paths = [new MonitorEntry { Path = _monitor }] },
            If = new ConditionSpec
            {
                NamePattern = _name,
                NamePatternKind = _kind,
                Extensions = _extensions,
                MinSizeBytes = _min,
                MaxSizeBytes = _max,
            },
            Then = new ActionSpec
            {
                Action = _action,
                Into = _destination,
                CommandKey = _commandKey,
            },
        };
    }
}
