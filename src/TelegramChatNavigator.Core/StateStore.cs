using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelegramChatNavigator.Core;

public sealed record NavigatorState(int Version, string MediaRoot, ChatArchive Archive,
    ChatQuery Query, long? SelectedMessageId);
public sealed record UserPreferences(int Version = 1, string Theme = "System", double FontSize = 15);

public static class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }, MaxDepth = 64
    };

    public static Task SaveAsync(string path, NavigatorSession session, CancellationToken ct = default)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, session.Archive.RootPath);
        var state = new NavigatorState(1, relative, session.Archive, session.Query, session.SelectedMessageId);
        Validate(state);
        return AtomicWriteAsync(path, state, ct);
    }

    public static async Task<NavigatorState> LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            ImportSupport.CheckSize(path);
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<NavigatorState>(stream, Options, ct)
                ?? throw new ChatFormatException("Пустой файл состояния.");
            Validate(state); // Только проверенное состояние можно передать текущей сессии.
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, state.MediaRoot));
            return state with { Archive = state.Archive with { RootPath = root } };
        }
        catch (JsonException e) { throw new ChatFormatException("Повреждён JSON состояния.", e); }
        catch (ArgumentException e) { throw new ChatFormatException("Некорректные пути или параметры состояния.", e); }
    }

    private static void Validate(NavigatorState state)
    {
        if (state.Version != 1) throw new ChatFormatException("Поддерживается только версия состояния 1.");
        if (string.IsNullOrWhiteSpace(state.MediaRoot) || state.Archive?.Messages is null || state.Query is null || state.Query.Text is null)
            throw new ChatFormatException("В состоянии отсутствуют обязательные поля.");
        var messages = state.Archive.Messages;
        if (messages.Count > ImportSupport.MaxMessages || state.Archive.Name is null || state.Query.From > state.Query.To ||
            !Enum.IsDefined(state.Query.Kind)) throw new ChatFormatException("Некорректные параметры состояния.");
        var ids = new HashSet<long>();
        DateTime previous = DateTime.MinValue;
        foreach (var m in messages)
        {
            if (m is null || m.Id <= 0 || !ids.Add(m.Id) || m.Date < previous || m.Date == default || m.Text is null || m.Sender is null || m.SenderId is null ||
                m.Attachments is null || m.Links is null || m.Attachments.Any(a => a is null || string.IsNullOrWhiteSpace(a.RelativePath) || Path.IsPathRooted(a.RelativePath) || a.RelativePath.Replace('\\', '/').Split('/').Contains("..") || a.Name is null ||
                    a.Kind is not (MessageKind.Photo or MessageKind.Video or MessageKind.Audio or MessageKind.File)) ||
                m.Links.Any(l => l is null || !SafePaths.IsWebLink(l)) ||
                (m.Kind & ~(MessageKind.Text | MessageKind.Photo | MessageKind.Video | MessageKind.File | MessageKind.Audio | MessageKind.Link | MessageKind.Poll | MessageKind.Service)) != 0 ||
                m.Poll is not null && (m.Poll.Question is null || m.Poll.TotalVoters < 0 || m.Poll.Answers is null || m.Poll.Answers.Any(a => a is null || a.Text is null || a.Voters < 0)))
                throw new ChatFormatException("Состояние содержит некорректное сообщение.");
            previous = m.Date;
        }
        if (state.SelectedMessageId is not null && !ids.Contains(state.SelectedMessageId.Value))
            throw new ChatFormatException("Выбранное сообщение отсутствует в состоянии.");
        if (state.Query.SenderId is not null && !messages.Any(m => m.SenderId == state.Query.SenderId))
            throw new ChatFormatException("Участник фильтра отсутствует в состоянии.");
    }

    public static async Task<UserPreferences> LoadPreferencesAsync(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(path), Options);
            return value is { Version: 1, FontSize: >= 12 and <= 22, Theme: "System" or "Light" or "Dark" } ? value : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static Task SavePreferencesAsync(string path, UserPreferences preferences) => AtomicWriteAsync(path, preferences);

    public static async Task ExportCsvAsync(string path, IReadOnlyList<ChatMessage> messages)
    {
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        // Апостроф не даёт редактору таблиц принять текст сообщения за формулу.
        static string Cell(string s) => Q(s.Length > 0 && "=+-@\t\r\n".Contains(s[0]) ? "'" + s : s);
        var rows = messages.Select(m => string.Join(',', m.Id.ToString(), Q(m.Date.ToString("yyyy-MM-dd HH:mm:ss")), Cell(m.Sender), Cell(m.Text), Cell(m.PollText), Cell(m.AttachmentLabel), Cell(string.Join(" ", m.Links))));
        await AtomicTextAsync(path, "id,date,sender,text,poll,attachments,links\r\n" + string.Join("\r\n", rows));
    }

    private static Task AtomicWriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var text = JsonSerializer.Serialize(value, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > ImportSupport.MaxFileBytes)
            throw new ChatFormatException("Состояние больше 128 МиБ. Откройте экспорт меньшего периода.");
        return AtomicTextAsync(path, text, ct);
    }

    private static async Task AtomicTextAsync(string path, string text, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            // Переименование в том же каталоге атомарно на локальной файловой системе macOS.
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
