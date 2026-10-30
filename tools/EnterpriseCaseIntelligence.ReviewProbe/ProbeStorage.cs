namespace EnterpriseCaseIntelligence.ReviewProbe;

internal static class ProbeStorage
{
    public static void ValidateLocation(string directory, string contentRoot)
    {
        if (!Path.IsPathFullyQualified(directory) || Within(directory, AppContext.BaseDirectory) || Within(directory, contentRoot)) throw new ArgumentException();
    }

    public static bool? UnixOwnerOnly(string directory)
        => OperatingSystem.IsWindows() ? null : (File.GetUnixFileMode(directory) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    public static bool IsDirectoryLink(string directory) => new DirectoryInfo(directory).LinkTarget is not null;

    private static bool Within(string path, string root)
    {
        // Resolve existing directory aliases even when the final directory does
        // not exist yet. Lexical comparison misses /var vs /private/var on macOS,
        // and a configured parent link into the publication/content directory.
        var directory = Canonical(path);
        var boundary = Canonical(root);
        // Conservatively reject case aliases on macOS/Windows, including when
        // the final component is absent on a case-sensitive macOS volume.
        var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(boundary) ? boundary : boundary + Path.DirectorySeparatorChar;
        return string.Equals(directory, boundary, comparison) || directory.StartsWith(prefix, comparison);
    }

    private static string Canonical(string path, int remaining = 128)
    {
        if (remaining <= 0) throw new ArgumentException();
        var info = new DirectoryInfo(Path.GetFullPath(path));
        if (info.Parent is null) return info.FullName;
        var candidate = new DirectoryInfo(Path.Combine(Canonical(info.Parent.FullName, remaining - 1), info.Name));
        return candidate.LinkTarget is null ? candidate.FullName : Canonical(candidate.ResolveLinkTarget(returnFinalTarget: true)!.FullName, remaining - 1);
    }
}
