namespace TubeForge.Updates;

/// <summary>
/// Removes the previous installation that the installer keeps beside the current one so it can
/// roll back a failed update. That copy is only cleared at the start of the next install or on
/// uninstall, so a user who updates once and then stops carries a full duplicate of the old
/// version — roughly half a gigabyte — indefinitely.
/// </summary>
public static class SupersededInstallCleaner
{
    private const string InstallDirectoryName = "TubeForge";
    private const string RollbackDirectoryName = "TubeForge.rollback";

    /// <summary>
    /// Deletes the retained previous installation, but only when called from the executable that
    /// replaced it. Running is the proof the new version works, which is the condition the
    /// rollback copy was being kept for. Returns true when a copy was removed.
    /// </summary>
    /// <param name="programsRoot">Directory holding both the install and rollback folders.</param>
    /// <param name="currentExecutablePath">Path of the running process image.</param>
    public static bool TryRemoveRetainedRollback(string? programsRoot, string? currentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(programsRoot) || string.IsNullOrWhiteSpace(currentExecutablePath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(programsRoot).TrimEnd(Path.DirectorySeparatorChar);
            var installDirectory = Path.Combine(root, InstallDirectoryName);
            var rollbackDirectory = Path.Combine(root, RollbackDirectoryName);
            if (!Directory.Exists(rollbackDirectory))
            {
                return false;
            }

            // Only an installed build may do this. A portable or development build runs from
            // somewhere else entirely and has no business deleting an installation's files.
            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(currentExecutablePath));
            if (executableDirectory is null ||
                !executableDirectory.TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(installDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Belt and braces: the target must be the exact expected sibling of the install
            // directory, never anything reached through a link or a relative segment.
            var resolved = Path.GetFullPath(rollbackDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!resolved.Equals(rollbackDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(resolved), root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).Equals(RollbackDirectoryName, StringComparison.Ordinal))
            {
                return false;
            }

            Directory.Delete(resolved, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException or
                                          PathTooLongException)
        {
            // Housekeeping only. A locked file or a permission change is not worth surfacing.
            return false;
        }
    }

    /// <summary>Default per-user location that the installer writes both directories into.</summary>
    public static string DefaultProgramsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs");
}
