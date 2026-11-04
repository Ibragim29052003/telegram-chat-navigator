using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TelegramChatNavigator.Core;

internal static partial class ImportSupport
{
    public const int MaxMessages = 200_000;
    public const long MaxFileBytes = 128 * 1024 * 1024;
    public static void CheckSize(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл экспорта не найден.", path);
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new ChatFormatException("Файл больше 128 МиБ. Экспортируйте меньший период переписки.");
    }

    public static ChatArchive Finish(string name, string format, string root, List<ChatMessage> messages)
    {
        if (messages.Count > MaxMessages) throw new ChatFormatException("Больше 200 000 сообщений. Разделите экспорт по датам.");
        if (messages.Select(m => m.Id).Distinct().Count() != messages.Count)
            throw new ChatFormatException("В экспорте повторяются идентификаторы сообщений.");
        return new ChatArchive(string.IsNullOrWhiteSpace(name) ? "Чат без названия" : name, format,
            messages.OrderBy(m => m.Date).ThenBy(m => m.Id).ToArray()) { RootPath = Path.GetFullPath(root) };
    }

    public static string String(JsonElement element, string key, string fallback = "") =>
        element.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;
    public static long Long(JsonElement element, string key, long fallback = 0) =>
        long.TryParse(String(element, key), out var value) ? value : fallback;
    public static string RichText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(RichText)),
        JsonValueKind.Object => element.TryGetProperty("text", out var text) ? RichText(text) : "",
        _ => ""
    };
    public static DateTime Date(string text)
    {
        // Telegram dates without offsets are wall-clock values from the export machine.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        throw new ChatFormatException($"Некорректная дата сообщения: {text}");
    }

    [GeneratedRegex(@"https?://[^\s<>""\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex WebRegex();
    public static IEnumerable<string> ExtractLinks(string text) => WebRegex().Matches(text)
        .Select(m => m.Value.TrimEnd('.', ',', ')', ';', '!', '?')).Where(SafePaths.IsWebLink);
    public static MessageKind FileKind(string type, string path) => type switch
    {
        "video_file" or "video_message" or "animation" => MessageKind.Video,
        "audio_file" or "voice_message" => MessageKind.Audio,
        "sticker" => Path.GetExtension(path).ToLowerInvariant() is ".webp" or ".png" ? MessageKind.Photo : MessageKind.File,
        _ => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" => MessageKind.Photo,
            ".mp4" or ".mov" or ".webm" or ".gif" => MessageKind.Video,
            ".mp3" or ".wav" or ".m4a" or ".ogg" or ".opus" => MessageKind.Audio,
            _ => MessageKind.File
        }
    };
}

public sealed class ChatImporter : IChatImporter
{
    private readonly IChatImporter[] _readers = [new TelegramJsonImporter(), new TelegramHtmlImporter()];
    public bool CanRead(string path) => _readers.Any(r => r.CanRead(path));
    public Task<ChatArchive> ReadAsync(string path, CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            var json = Path.Combine(path, "result.json");
            var html = Path.Combine(path, "messages.html");
            path = File.Exists(json) ? json : File.Exists(html) ? html :
                throw new ChatFormatException("В папке нет result.json или messages.html. Распакуйте экспорт одного чата Telegram Desktop.");
        }
        var reader = _readers.FirstOrDefault(r => r.CanRead(path)) ??
            throw new ChatFormatException("Поддерживаются result.json и messages*.html из Telegram Desktop.");
        return reader.ReadAsync(path, cancellationToken, progress);
    }
}
