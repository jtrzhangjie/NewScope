using System.IO.Compression;
using NewScope.Services;

namespace NewScope.Tests;

public class DiagnosticsServiceTests
{
    [Fact]
    public void BundleContainsSystemAndUiDetails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "diagnostics.zip");

        try
        {
            Directory.CreateDirectory(directory);
            DiagnosticsService.CreateBundle(path, "WindowState: Normal\nScreens: test");

            Assert.True(File.Exists(path));
            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("system.txt", names);

            var system = archive.GetEntry("system.txt")!;
            using var reader = new StreamReader(system.Open());
            var text = reader.ReadToEnd();
            Assert.Contains("WindowState: Normal", text);
            Assert.Contains("J-Link DLL:", text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    [Fact]
    public void MiniDumpWriterProducesAFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "newscope-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "crash.dmp");

        try
        {
            Directory.CreateDirectory(directory);
            var written = DiagnosticsService.TryWriteDump(path);

            Assert.True(written, "MiniDumpWriteDump failed.");
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

}
