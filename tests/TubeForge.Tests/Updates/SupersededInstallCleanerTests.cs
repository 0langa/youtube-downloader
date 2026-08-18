using TubeForge.Tests.Framework;
using TubeForge.Updates;

namespace TubeForge.Tests.Updates;

public static class SupersededInstallCleanerTests
{
    [Test]
    public static void RemovesTheRetainedRollbackWhenRunFromTheInstallDirectory()
    {
        using var root = new CleanerTestDirectory();
        var install = root.CreateDirectory("TubeForge");
        var rollback = root.CreateDirectory("TubeForge.rollback");
        File.WriteAllText(Path.Combine(rollback, "TubeForge.exe"), "previous");
        File.WriteAllText(Path.Combine(rollback, "ffmpeg", "ffmpeg.exe"), "previous");
        var executable = Path.Combine(install, "TubeForge.exe");
        File.WriteAllText(executable, "current");

        var removed = SupersededInstallCleaner.TryRemoveRetainedRollback(root.Path, executable);

        Assert.True(removed);
        Assert.False(Directory.Exists(rollback));
        // The live installation must be untouched.
        Assert.True(File.Exists(executable));
    }

    [Test]
    public static void LeavesTheRollbackAloneForBuildsRunningOutsideTheInstallDirectory()
    {
        using var root = new CleanerTestDirectory();
        root.CreateDirectory("TubeForge");
        var rollback = root.CreateDirectory("TubeForge.rollback");
        File.WriteAllText(Path.Combine(rollback, "TubeForge.exe"), "previous");
        var portable = root.CreateDirectory("portable");
        var executable = Path.Combine(portable, "TubeForge.exe");
        File.WriteAllText(executable, "portable");

        // A portable or development build has no business deleting an installation's files.
        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback(root.Path, executable));
        Assert.True(Directory.Exists(rollback));
    }

    [Test]
    public static void ReportsNothingRemovedWhenThereIsNoRollbackOrNoInputs()
    {
        using var root = new CleanerTestDirectory();
        var install = root.CreateDirectory("TubeForge");
        var executable = Path.Combine(install, "TubeForge.exe");
        File.WriteAllText(executable, "current");

        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback(root.Path, executable));
        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback(null, executable));
        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback(root.Path, null));
        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback("   ", executable));
    }

    [Test]
    public static void AcceptsATraversedPathThatStillResolvesToTheRealRoot()
    {
        using var root = new CleanerTestDirectory();
        var install = root.CreateDirectory("TubeForge");
        var rollback = root.CreateDirectory("TubeForge.rollback");
        File.WriteAllText(Path.Combine(rollback, "TubeForge.exe"), "previous");
        var executable = Path.Combine(install, "TubeForge.exe");
        File.WriteAllText(executable, "current");
        var traversed = Path.Combine(install, "..", "..", Path.GetFileName(root.Path));

        // Normalisation is the point of the check, not an obstacle to it: this path spells the
        // real root a different way, so it is the real root.
        Assert.True(SupersededInstallCleaner.TryRemoveRetainedRollback(traversed, executable));
        Assert.False(Directory.Exists(rollback));
    }

    [Test]
    public static void RefusesARootTheRunningExecutableDoesNotBelongTo()
    {
        using var root = new CleanerTestDirectory();
        var install = root.CreateDirectory("TubeForge");
        var executable = Path.Combine(install, "TubeForge.exe");
        File.WriteAllText(executable, "current");

        // A second layout elsewhere on disk, complete with its own rollback copy.
        using var other = new CleanerTestDirectory();
        other.CreateDirectory("TubeForge");
        var foreignRollback = other.CreateDirectory("TubeForge.rollback");
        File.WriteAllText(Path.Combine(foreignRollback, "TubeForge.exe"), "someone else");

        // The running executable does not live in that layout, so its files are not ours to delete.
        Assert.False(SupersededInstallCleaner.TryRemoveRetainedRollback(other.Path, executable));
        Assert.True(Directory.Exists(foreignRollback));
    }

    private sealed class CleanerTestDirectory : IDisposable
    {
        private static readonly string SafeRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TubeForge.Tests"));

        public CleanerTestDirectory()
        {
            Path = System.IO.Path.Combine(SafeRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string name)
        {
            var created = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.Combine(created, "ffmpeg"));
            return created;
        }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(SafeRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to clean a test directory outside the safe root.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
