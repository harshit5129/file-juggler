using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Juggler.Ui.Services;

/// <summary>
/// Logical-tree lookups.
/// <para>
/// Code-behind resolves its controls through these rather than through the fields the XAML
/// compiler would normally generate for <c>x:Name</c>. Resolving by <see cref="Control.Name"/>
/// at runtime is explicit, survives a rename that only touches one file, and behaves the same
/// whether or not the compiled populate pass has run.
/// </para>
/// </summary>
public static class Tree
{
    /// <summary>First descendant of type <typeparamref name="T"/>, optionally filtered by name.</summary>
    public static T? Find<T>(this ILogical root, string? name = null) where T : Control
    {
        foreach (ILogical node in Walk(root))
        {
            if (node is T control && (name is null || control.Name == name))
            {
                return control;
            }
        }

        return null;
    }

    /// <summary>Every descendant of type <typeparamref name="T"/>, in tree order.</summary>
    public static List<T> FindAll<T>(this ILogical root) where T : Control
    {
        List<T> found = [];

        foreach (ILogical node in Walk(root))
        {
            if (node is T control)
            {
                found.Add(control);
            }
        }

        return found;
    }

    /// <summary>
    /// Throws with a useful message instead of returning null, so a missing control names itself
    /// at the first call rather than producing a null dereference three frames deeper.
    /// </summary>
    public static T Require<T>(this ILogical root, string name) where T : Control =>
        root.Find<T>(name)
        ?? throw new InvalidOperationException(
            $"No {typeof(T).Name} named '{name}' found in the {root.GetType().Name} visual tree.");

    /// <summary>Depth-first enumeration, skipping the root itself.</summary>
    private static IEnumerable<ILogical> Walk(ILogical root)
    {
        foreach (ILogical child in root.GetLogicalChildren())
        {
            yield return child;

            foreach (ILogical descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
