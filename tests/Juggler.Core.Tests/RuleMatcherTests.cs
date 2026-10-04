using Juggler.Core.Execution;
using Juggler.Core.Rules;

namespace Juggler.Core.Tests;

/// <summary>
/// The matcher.
/// <para>
/// Wrong matches here are the worst kind of bug in this product: the user writes "screenshots",
/// and something else gets moved. These tests pin the semantics the docs describe.
/// </para>
/// </summary>
public sealed class RuleMatcherTests
{
    private static FileCandidate File(
        string name = "photo.png",
        long length = 1024,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null)
    {
        var stamp = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        return new FileCandidate(
            $@"C:\In\{name}",
            length,
            created ?? stamp,
            modified ?? stamp);
    }

    [Fact]
    public void EmptyConditionsMatchEverything()
    {
        Assert.True(RuleMatcher.Matches(new ConditionSpec(), File()));
    }

    [Fact]
    public void ExtensionMatchIsCaseInsensitive()
    {
        ConditionSpec spec = new() { Extensions = ["PNG"] };

        Assert.True(RuleMatcher.Matches(spec, File("PHOTO.png")));
    }

    [Fact]
    public void ExtensionMatchIgnoresLeadingDotInTheRule()
    {
        ConditionSpec spec = new() { Extensions = [".png"] };

        Assert.True(RuleMatcher.Matches(spec, File()));
    }

    [Fact]
    public void AnyListedExtensionMatches()
    {
        ConditionSpec spec = new() { Extensions = ["jpg", "png"] };

        Assert.True(RuleMatcher.Matches(spec, File()));
        Assert.False(RuleMatcher.Matches(spec, File("notes.txt")));
    }

    [Fact]
    public void AFileWithNoExtensionDoesNotMatchAnExtensionRule()
    {
        ConditionSpec spec = new() { Extensions = ["png"] };

        Assert.False(RuleMatcher.Matches(spec, File("README")));
    }

    [Fact]
    public void MinSizeIsInclusive()
    {
        Assert.True(RuleMatcher.Matches(new ConditionSpec { MinSizeBytes = 1024 }, File(length: 1024)));
        Assert.False(RuleMatcher.Matches(new ConditionSpec { MinSizeBytes = 1025 }, File(length: 1024)));
    }

    [Fact]
    public void MaxSizeIsInclusive()
    {
        Assert.True(RuleMatcher.Matches(new ConditionSpec { MaxSizeBytes = 1024 }, File(length: 1024)));
        Assert.False(RuleMatcher.Matches(new ConditionSpec { MaxSizeBytes = 1023 }, File(length: 1024)));
    }

    [Fact]
    public void ConditionsCombineWithAnd()
    {
        // Extension says yes, size says no. AND means no match.
        ConditionSpec spec = new() { Extensions = ["png"], MinSizeBytes = 5000 };

        Assert.False(RuleMatcher.Matches(spec, File(length: 1024)));
    }

    [Fact]
    public void CreatedBeforeIsRespected()
    {
        ConditionSpec spec = new() { CreatedBefore = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.False(RuleMatcher.Matches(spec, File()));
    }

    /// <summary>A file must have been modified *after* the cutoff, not before it.</summary>
    [Fact]
    public void ModifiedAfterExcludesOlderFiles()
    {
        var stamp = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.False(RuleMatcher.Matches(
            new ConditionSpec { ModifiedAfter = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            File(modified: stamp)));

        Assert.True(RuleMatcher.Matches(
            new ConditionSpec { ModifiedAfter = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            File(modified: stamp)));
    }
}

/// <summary>Name patterns, in both flavours.</summary>
public sealed class NamePatternTests
{
    [Theory]
    [InlineData("*.tmp", "scratch.tmp", true)]
    [InlineData("*.tmp", "scratch.TMP", true)]      // case-insensitive, per the docs
    [InlineData("*.tmp", "scratch.tmpx", false)]
    [InlineData("report-?.docx", "report-a.docx", true)]
    [InlineData("report-?.docx", "report-ab.docx", false)]
    [InlineData("Screenshot*", "Screenshot 2026-03-01.png", true)]
    [InlineData("Screenshot*", "My Screenshot.png", false)]
    [InlineData("*", "anything", true)]
    [InlineData("exact.txt", "exact.txt", true)]
    [InlineData("exact.txt", "exactly.txt", false)]
    public void GlobMatchesWildcards(string pattern, string name, bool expected) =>
        Assert.Equal(expected, RuleMatcher.GlobMatches(pattern, name));

    /// <summary>
    /// A pattern full of regex punctuation is a legal filename. Treating it as a regex would make
    /// those characters mean something the user never asked for.
    /// </summary>
    [Fact]
    public void GlobTreatsRegexCharactersLiterally()
    {
        Assert.True(RuleMatcher.GlobMatches("report(1).txt", "report(1).txt"));
        Assert.False(RuleMatcher.GlobMatches("report(1).txt", "report1.txt"));
    }

    /// <summary>
    /// Backtracking must not blow up on a long adversarial pattern. This is the case a naive
    /// wildcard matcher hangs on.
    /// </summary>
    [Fact]
    public void GlobTerminatesOnAdversarialInput()
    {
        string pattern = new string('*', 40) + "b";
        string name = new string('a', 200);

        Assert.False(RuleMatcher.GlobMatches(pattern, name));
    }

    [Theory]
    [InlineData("^Screenshot_\\d+", "Screenshot_123", true)]
    [InlineData("^Screenshot_\\d+", "IMG_123", false)]
    [InlineData("screenshot", "Screenshot.png", true)]   // ignore-case by default
    public void RegexMatchesAsRegex(string pattern, string name, bool expected) =>
        Assert.Equal(expected, RuleMatcher.NameMatches(pattern, PatternKind.Regex, name));

    /// <summary>
    /// An invalid regex must not throw. The validator rejects one, but a rule can reach the
    /// runner through a path that skipped validation, and a crash there would abort the run.
    /// </summary>
    [Fact]
    public void InvalidRegexFailsClosedRatherThanThrowing()
    {
        Assert.False(RuleMatcher.NameMatches("([unclosed", PatternKind.Regex, "anything"));
    }

    [Fact]
    public void OverlongGlobPatternDoesNotMatch()
    {
        Assert.False(RuleMatcher.GlobMatches(new string('*', 600) + "x", new string('a', 600)));
    }
}

/// <summary>Token expansion, which decides the destination path.</summary>
public sealed class TokenExpanderTests
{
    private static readonly FileCandidate Sample =
        new(@"C:\In\report final.pdf", 2048,
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public void ExpandsNameAndExtension()
    {
        Assert.Equal("report final", TokenExpander.Expand("{name}", Sample));
        Assert.Equal("pdf", TokenExpander.Expand("{extension}", Sample));
        Assert.Equal(".pdf", TokenExpander.Expand("{ext}", Sample));
    }

    [Fact]
    public void ExpandsSizeAsAWholeNumber()
    {
        Assert.Equal("2048", TokenExpander.Expand("{size}", Sample));
    }

    [Fact]
    public void ExpandsDateWithTheGivenFormat()
    {
        Assert.Equal("2026-03-14", TokenExpander.ExpandWithDates("{date:yyyy-MM-dd}", Sample, Sample.LastModified));
        Assert.Equal("2026", TokenExpander.ExpandWithDates("{date:yyyy}", Sample, Sample.LastModified));
    }

    /// <summary>
    /// A date token with no format is half-written. Defaulting it is friendlier than writing the
    /// literal text "{date:" into a filename.
    /// </summary>
    [Fact]
    public void DateTokenWithoutFormatFallsBackToYearMonth()
    {
        Assert.Equal("2026-03", TokenExpander.ExpandWithDates("{date}", Sample, Sample.LastModified));
    }

    [Fact]
    public void SeveralTokensInOneTemplateAllExpand()
    {
        Assert.Equal(
            "2026-03-14_report final.pdf",
            TokenExpander.ExpandWithDates("{date:yyyy-MM-dd}_{name}{ext}", Sample, Sample.LastModified));
    }

    /// <summary>
    /// A name that expands to nothing would otherwise become an empty filename, which the OS
    /// rejects with a message that means nothing to the user.
    /// </summary>
    [Fact]
    public void UnsafeCharactersAreStrippedFromGeneratedNames()
    {
        Assert.Equal("a-b-c", RuleRunner.SafeName("a/b\\c"));
        Assert.Equal("no-colon", RuleRunner.SafeName("no:colon"));
    }

    [Fact]
    public void EmptyGeneratedNameGetsAFallback()
    {
        Assert.Equal("unnamed", RuleRunner.SafeName("   "));
        Assert.Equal("unnamed", RuleRunner.SafeName("..."));
    }
}

/// <summary>Sort folder names, which become real directory names.</summary>
public sealed class SortFolderTests
{
    private static readonly FileCandidate Sample =
        new(@"C:\In\photo.png", 5 * 1024 * 1024,
            new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ExtensionSortUsesTheExtension() =>
        Assert.Equal("png", TokenExpander.SortFolderName(new ActionSpec { SortBy = SortKey.Extension }, Sample));

    [Fact]
    public void DateSortUsesTheRightDateField()
    {
        Assert.Equal(
            "2026-03",
            TokenExpander.SortFolderName(new ActionSpec { SortBy = SortKey.DateModified }, Sample));

        Assert.Equal(
            "2025-01",
            TokenExpander.SortFolderName(new ActionSpec { SortBy = SortKey.DateCreated }, Sample));
    }

    /// <summary>A folder named "5242880" is not something a person can navigate.</summary>
    [Fact]
    public void SizeSortUsesReadableBuckets() =>
        Assert.Equal(
            "1 - 10 MB",
            TokenExpander.SortFolderName(new ActionSpec { SortBy = SortKey.Size }, Sample));
}