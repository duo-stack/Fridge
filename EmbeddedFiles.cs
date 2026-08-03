using System.Reflection;

namespace Fridge;

internal static class EmbeddedFiles
{
    private const string Prefix = "Fridge.Assets.";

    public static bool Exists(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceInfo(Prefix + name) is not null;
    }

    public static Stream Open(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"当前工具未包含资源文件：{name}");
    }

    public static async Task ExtractAsync(string name, string destination, CancellationToken cancellationToken)
    {
        await using var source = Open(name);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(target, cancellationToken);
    }
}
