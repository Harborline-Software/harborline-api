namespace Harborline.Api.LocalNodeHost.Tests.TestDoubles;

internal static class SqliteTestDatabase
{
    public static string ConnectionString(string path) => $"Data Source={path};Pooling=False";

    public static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
