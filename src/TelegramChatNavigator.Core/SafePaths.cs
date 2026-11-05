namespace TelegramChatNavigator.Core;

public static class SafePaths
{
    public static string ResolveMedia(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new IOException("Вложение должно иметь относительный путь внутри папки экспорта.");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));
        EnsureInside(root, path);
        // Проверяем каждый каталог: символическая ссылка тоже может вывести за папку экспорта.
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Недоступная символическая ссылка.");
                EnsureInside(root, current);
            }
        }
        return current;
    }

    private static void EnsureInside(string root, string path)
    {
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new IOException("Путь вложения выходит за папку экспорта.");
    }

    public static bool IsWebLink(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";
}
