#if !BROWSER
namespace Sia.Engine.Example;

// Offline use-site policy only. The caller owns the staged files and failure cleanup.
internal static class OfflineDirectory
{
    internal static void RequireFiles(string directory, IReadOnlyCollection<string> ownedFiles, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Offline staging directory must not be a directory link.");
        var expected = new HashSet<string>(comparison);
        foreach (var file in ownedFiles) {
            var full = Path.GetFullPath(file);
            if (!comparison.Equals(Path.GetDirectoryName(full), directory) || !expected.Add(full))
                throw new InvalidDataException("Offline publication receipts must identify unique direct files.");
        }
        var count = 0;
        foreach (var file in Directory.EnumerateFileSystemEntries(directory)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expected.Contains(Path.GetFullPath(file)) || (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Unexpected offline staging child or link was preserved.");
            count++;
        }
        if (count != expected.Count) throw new IOException("An owned offline publication file is missing.");
    }

    internal static void Publish(string stage, string output, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Offline staging directory must not be a directory link.");
            try {
                Directory.Move(stage, output);
                return;
            }
            // A child handle can make Windows report access denied as well as sharing violation.
            // Bound retries of the rename itself; permanent errors still propagate to caller cleanup.
            catch (IOException error) when (OperatingSystem.IsWindows()
                && (error.HResult & 0xffff) is 5 or 32 && attempt < 7
                && Directory.Exists(stage) && !Path.Exists(output)) {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(25);
            }
        }
    }
}
#endif
