namespace Spoke.Core;

/// <summary>Writes a file so readers never see half of it: a temp file beside it, then a rename over the original.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
