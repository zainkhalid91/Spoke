using Spoke.Core;

namespace Spoke.Core.Tests;

public class AtomicFileTests
{
    [Fact]
    public void Replaces_the_file_and_leaves_no_temp_behind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spoke-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "old");
            AtomicFile.WriteAllText(path, "new");
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Equal(["settings.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }
}
