using System.ComponentModel;
using System.Diagnostics;

namespace TelegramChatNavigator.Desktop;

internal enum AttachmentPlatform { MacOS, Windows, Linux, Other }

internal static class SystemAttachmentOpener
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".mpg", ".mpeg", ".3gp",
        ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".aif", ".aiff", ".amr",
        ".pdf", ".txt", ".csv", ".json", ".md", ".rtf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp"
    };

    public static Task OpenAsync(string path)
    {
        var platform = OperatingSystem.IsMacOS() ? AttachmentPlatform.MacOS :
            OperatingSystem.IsWindows() ? AttachmentPlatform.Windows :
            OperatingSystem.IsLinux() ? AttachmentPlatform.Linux : AttachmentPlatform.Other;
        return StartAsync(CreateStartInfo(path, platform), platform);
    }

    internal static ProcessStartInfo CreateStartInfo(string path, AttachmentPlatform platform)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Вложение отсутствует. Повторите экспорт с медиа или укажите новую папку медиа.");
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            throw new IOException($"Внешнее открытие формата «{Path.GetExtension(path)}» не поддерживается. Программы и скрипты через эту кнопку не запускаются.");

        // В Windows оболочка выбирает просмотрщик по типу файла, без cmd.exe.
        if (platform == AttachmentPlatform.Windows)
            return new(path) { UseShellExecute = true, Verb = "open" };

        var start = platform switch
        {
            AttachmentPlatform.MacOS => new ProcessStartInfo("/usr/bin/qlmanage"),
            AttachmentPlatform.Linux => new ProcessStartInfo("xdg-open"),
            _ => throw new IOException("Открытие вложений поддерживается на macOS, Windows и Linux.")
        };
        start.UseShellExecute = false;
        if (platform == AttachmentPlatform.MacOS) start.ArgumentList.Add("-p");
        // Путь передаётся одним аргументом: пробелы и кавычки не становятся командами.
        start.ArgumentList.Add(path);
        return start;
    }

    internal static async Task StartAsync(ProcessStartInfo start, AttachmentPlatform platform)
    {
        try
        {
            using var process = Process.Start(start);
            // Оболочка Windows может передать файл уже запущенному приложению и вернуть null.
            if (process is null || platform == AttachmentPlatform.Windows) return;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await process.WaitForExitAsync(startup.Token); }
            catch (OperationCanceledException) { return; }
            // Просмотрщик может оставаться открытым. Его завершения не ждём и процесс не останавливаем.
            if (process.ExitCode != 0)
                throw new IOException(platform == AttachmentPlatform.Linux
                    ? "Система не смогла открыть вложение. Проверьте наличие xdg-utils, графического сеанса и приложения по умолчанию для этого типа файла."
                    : "Quick Look не смог открыть вложение. Проверьте файл и поддержку его формата в macOS.");
        }
        catch (Win32Exception e)
        {
            throw new IOException(platform switch
            {
                AttachmentPlatform.Windows => "Не удалось открыть вложение. Установите просмотрщик для этого типа файла и назначьте его приложением по умолчанию в Windows.",
                AttachmentPlatform.Linux => "Не удалось запустить xdg-open. Установите xdg-utils и просмотрщик для этого типа файла; нужен графический сеанс Linux.",
                _ => "Не удалось запустить Quick Look. Проверьте доступность системного просмотрщика macOS."
            }, e);
        }
    }
}
