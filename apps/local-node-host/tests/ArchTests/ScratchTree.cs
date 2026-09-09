namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

internal static class ScratchTree
{
    public static void Delete(string root)
    {
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);

        File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }
}
