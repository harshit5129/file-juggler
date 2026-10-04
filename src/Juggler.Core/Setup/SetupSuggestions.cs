using Juggler.Core.Rules;

namespace Juggler.Core.Setup;

/// <summary>
/// Which standard folder a suggestion targets.
/// <para>
/// Deliberately not a free-form path. Every template keys off one of these, so the wizard can
/// prove a suggestion applies before offering it rather than proposing a rule for a folder whose
/// contents it knows nothing about.
/// </para>
/// </summary>
public enum StandardFolderKind
{
    Downloads,
    Desktop,
    Pictures,
    Documents,
    Videos,
    Music,
}

/// <summary>A standard folder that exists on this machine.</summary>
public sealed record DetectedFolder(StandardFolderKind Kind, string Path)
{
    public string Label => Kind switch
    {
        StandardFolderKind.Downloads => "Downloads",
        StandardFolderKind.Desktop => "Desktop",
        StandardFolderKind.Pictures => "Pictures",
        StandardFolderKind.Documents => "Documents",
        StandardFolderKind.Videos => "Videos",
        StandardFolderKind.Music => "Music",
        _ => Kind.ToString(),
    };
}

/// <summary>A rule the wizard proposes, plus the reason it is proposing it.</summary>
public sealed record RuleSuggestion(Rule Rule, string Rationale);

/// <summary>
/// First-run rule suggestions.
/// <para>
/// Split deliberately into a probing half and a pure half. <see cref="Detect"/> touches the
/// filesystem and cannot be asserted on in a test; <see cref="Build"/> is a pure function of the
/// folders handed to it, which is what the test suite exercises.
/// </para>
/// <para>
/// The suggestions are fixed templates rather than a scan of folder contents. Scanning would be
/// cleverer and worse: a folder holding one <c>.bak</c> file from 2019 yields a junk rule nobody
/// asked for, and enumerating a large folder before the user has agreed to anything is slow and
/// surprising. Templates are predictable, cheap, and explainable in one line each.
/// </para>
/// </summary>
public static class SetupSuggestions
{
    /// <summary>Standard folders that actually exist, in a stable order.</summary>
    public static IReadOnlyList<DetectedFolder> Detect() => BuildDetection(Probe);

    /// <summary>
    /// Probing seam. Public so the test suite can drive detection without touching the real
    /// shell: the point of splitting this out is that the folder enumeration is the one part of
    /// setup that cannot be asserted on directly.
    /// </summary>
    public static IReadOnlyList<DetectedFolder> BuildDetection(Func<StandardFolderKind, string?> probe)
    {
        List<DetectedFolder> found = [];

        foreach (StandardFolderKind kind in Enum.GetValues<StandardFolderKind>())
        {
            string? path = probe(kind);

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                found.Add(new DetectedFolder(kind, path));
            }
        }

        return found;
    }

    /// <summary>
    /// Resolves one standard folder, or null if it does not exist.
    /// </summary>
    private static string? Probe(StandardFolderKind kind)
    {
        try
        {
            // Downloads is not in Environment.SpecialFolder, and it is the folder most likely to
            // be redirected: on a OneDrive-known-folders machine it is not %USERPROFILE%\Downloads
            // at all. SHGetKnownFolderPath follows the redirection, which is why it is used rather
            // than string concatenation.
            if (kind == StandardFolderKind.Downloads)
            {
                return KnownFolders.Downloads();
            }

            return Environment.GetFolderPath(kind switch
            {
                StandardFolderKind.Desktop => Environment.SpecialFolder.DesktopDirectory,
                StandardFolderKind.Pictures => Environment.SpecialFolder.MyPictures,
                StandardFolderKind.Documents => Environment.SpecialFolder.MyDocuments,
                StandardFolderKind.Videos => Environment.SpecialFolder.MyVideos,
                StandardFolderKind.Music => Environment.SpecialFolder.MyMusic,
                _ => Environment.SpecialFolder.UserProfile,
            });
        }
        catch (Exception)
        {
            // A shell folder can fail to resolve on a stripped-down or redirected profile.
            // Absence is the correct answer, not a startup failure.
            return null;
        }
    }

    /// <summary>
    /// Proposes rules for the given folders. Pure: same input, same output, no I/O.
    /// </summary>
    public static IReadOnlyList<RuleSuggestion> Build(IReadOnlyList<DetectedFolder> folders)
    {
        List<RuleSuggestion> suggestions = [];

        foreach (DetectedFolder folder in folders)
        {
            foreach (RuleSuggestion suggestion in For(folder))
            {
                suggestions.Add(suggestion);
            }
        }

        return suggestions;
    }

    private static IEnumerable<RuleSuggestion> For(DetectedFolder folder) => folder.Kind switch
    {
        // Screenshots: images named like a screenshot, which is a far more reliable signal
        // than the extension alone. A photo is not moved; a screenshot is.
        StandardFolderKind.Pictures => [
            Suggest(
                id: "setup-screenshots",
                name: "Sort screenshots",
                folder,
                condition: new ConditionSpec
                {
                    Extensions = ["png", "jpg", "jpeg", "webp"],
                    NamePattern = "Screenshot*",
                },
                action: MoveTo(folder.Path, "Screenshots")),
        ],

        // Installers and archives, split because they are filed separately in practice.
        StandardFolderKind.Downloads => [
            Suggest(
                id: "setup-installers",
                name: "File away installers",
                folder,
                condition: new ConditionSpec { Extensions = ["exe", "msi", "msix"] },
                action: MoveTo(folder.Path, "Installers")),
            Suggest(
                id: "setup-archives",
                name: "File away archives",
                folder,
                condition: new ConditionSpec { Extensions = ["zip", "7z", "rar"] },
                action: MoveTo(folder.Path, "Archives")),
            Suggest(
                id: "setup-temp",
                name: "Clear out temporary files",
                folder,
                condition: new ConditionSpec { Extensions = ["tmp"], MinSizeBytes = 1024 * 1024 },
                action: MoveTo(folder.Path, "Temp")),
        ],

        // Documents: group by year rather than by type, because the useful axis for paperwork
        // is when it was written, not what format it arrived in.
        StandardFolderKind.Documents => [
            Suggest(
                id: "setup-pdfs",
                name: "Group PDFs by year",
                folder,
                condition: new ConditionSpec { Extensions = ["pdf"] },
                action: new ActionSpec
                {
                    Action = ActionKind.SortIntoFolders,
                    Into = Combine(folder.Path, "{date:yyyy}"),
                    SortBy = SortKey.DateModified,
                }),
        ],

        _ => [],
    };

    private static ActionSpec MoveTo(string root, string sub) => new()
    {
        Action = ActionKind.Move,
        Into = Combine(root, sub),
    };

    private static string Combine(string root, string relative)
    {
        // Trim trailing separators so Path.Combine does not emit "Pictures\\Screenshots" for a
        // folder the user happened to name with a backslash on the end.
        return Path.Combine(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), relative);
    }

    private static RuleSuggestion Suggest(
        string id,
        string name,
        DetectedFolder folder,
        ConditionSpec condition,
        ActionSpec action)
    {
        Rule rule = new()
        {
            Id = id,
            Name = name,

            // Off by default. Setup should propose, not act. The wizard flips this on
            // explicitly once the user has read what the rules would do.
            Enabled = false,

            Monitor = new MonitorSpec
            {
                Paths = [new MonitorEntry { Path = folder.Path }],
                IncludeSubfolders = false,
            },
            If = condition,
            Then = action,
        };

        return new RuleSuggestion(rule, Describe(rule, folder));
    }

    /// <summary>
    /// Known-folder resolution for the one folder Environment.SpecialFolder cannot express.
    /// </summary>
    internal static class KnownFolders
    {
        // FOLDERID_Downloads. Spelled as a GUID because the numeric form is not something to
        // hard-code by memory.
        private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

        // DllImport rather than LibraryImport: the source-generated marshalling would require
        // AllowUnsafeBlocks on a project that has no other reason to enable unsafe. This call is
        // blittable and called once per first run, so there is nothing to gain from the generator
        // and it stays compatible with the NativeAOT build.
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            ref Guid rfid,
            uint dwFlags,
            nint hToken,
            out nint ppszPath);

        public static string? Downloads()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            nint pointer = IntPtr.Zero;

            try
            {
                Guid id = FolderIdDownloads;

                // KF_FLAG_DEFAULT_PATH (0) rather than KF_FLAG_CREATE (0x8000): asking the shell to
                // create a missing Downloads folder just to watch it would litter the disk.
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero)
                {
                    return null;
                }

                return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    private static string Describe(Rule rule, DetectedFolder folder)
    {
        string extensions = rule.If.Extensions.Count == 0
            ? "any file"
            : "." + string.Join(", .", rule.If.Extensions);

        string? destination = rule.Then.Into;
        string target = destination is null ? "its folder" : destination;

        return $"{extensions} in {folder.Label} move to {target}.";
    }
}