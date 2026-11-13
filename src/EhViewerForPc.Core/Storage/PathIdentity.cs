namespace EhViewerForPc.Core.Storage;

public static class PathIdentity
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsDescendantOf(string path, string root)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string child = Path.GetFullPath(path);
        return !Comparer.Equals(parent, child) &&
               child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }
}
