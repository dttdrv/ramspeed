using optiRAM.Services;

namespace optiRAM.Tests;

public class InstallerPayloadCopierTests
{
    [Fact]
    public void CopyPayload_throws_when_source_directory_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"optiRAM.Tests.MissingPayload.{Guid.NewGuid():N}");
        var source = Path.Combine(root, "missing");
        var target = Path.Combine(root, "target");

        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => InstallerPayloadCopier.CopyPayload(source, target));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopyPayload_copies_all_files_and_subdirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"optiRAM.Tests.Payload.{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");

        Directory.CreateDirectory(Path.Combine(source, "assets"));
        File.WriteAllText(Path.Combine(source, "optiRAM.exe"), "exe");
        File.WriteAllText(Path.Combine(source, "optiRAM.dll"), "dll");
        File.WriteAllText(Path.Combine(source, "optiRAM.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(source, "assets", "app.ico"), "icon");

        try
        {
            InstallerPayloadCopier.CopyPayload(source, target);

            Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "optiRAM.exe")));
            Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "optiRAM.dll")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "optiRAM.runtimeconfig.json")));
            Assert.Equal("icon", File.ReadAllText(Path.Combine(target, "assets", "app.ico")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopyPayload_overwrites_existing_target_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"optiRAM.Tests.OverwritePayload.{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "optiRAM.dll"), "new");
        File.WriteAllText(Path.Combine(target, "optiRAM.dll"), "old");

        try
        {
            InstallerPayloadCopier.CopyPayload(source, target);

            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "optiRAM.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
