using System.IO;

namespace RAMSpeed.Services;

internal static class InstallerPayloadCopier
{
    public static void CopyPayload(string sourceDir, string targetDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Installer payload source was not found: {sourceDir}");

        Directory.CreateDirectory(targetDir);

        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }
}
