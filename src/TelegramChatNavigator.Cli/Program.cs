using System.Globalization;
using TelegramChatNavigator.Core;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help")) { Console.WriteLine(Help); return 0; }
        try
        {
            var command = args[0];
            if (command is not ("inspect" or "search" or "participants" or "save-state" or "export"))
                throw new ArgumentException("Неизвестная команда. Используйте --help.");
            if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Укажите папку экспорта или файл состояния.");
            var options = new Dictionary<string, string>();
            for (var i = 2; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !new[] { "--text", "--kind", "--sender", "--from", "--to", "--out", "--limit" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException($"Неизвестный, повторный или неполный параметр: {args[i]}");
            }
            string Get(string key, string fallback = "") => options.GetValueOrDefault(key, fallback);
            var session = new NavigatorSession();
            if (args[1].EndsWith(".tgnav.json", StringComparison.OrdinalIgnoreCase))
            {
                var state = await StateStore.LoadAsync(args[1]);
                session.Replace(state.Archive, state.Query, state.SelectedMessageId);
            }
            else session.Replace(await new ChatImporter().ReadAsync(args[1]));
            var query = session.Query;
            if (options.ContainsKey("--text")) query = query with { Text = Get("--text") };
            if (options.ContainsKey("--sender")) query = query with { SenderId = Get("--sender") };
            if (options.ContainsKey("--kind"))
            {
                if (!Enum.TryParse<MessageKind>(Get("--kind"), true, out var kind) || !Enum.IsDefined(kind)) throw new ArgumentException("Неизвестная категория. См. --help.");
                query = query with { Kind = kind };
            }
            if (options.ContainsKey("--from")) query = query with { From = ParseDate(Get("--from")) };
            if (options.ContainsKey("--to")) query = query with { To = ParseDate(Get("--to")) };
            if (query.SenderId is not null && !session.Archive.Participants.Any(p => p.Id == query.SenderId))
                throw new ArgumentException("Участник не найден. Получите ID командой participants.");
            var matches = ChatSearch.Find(session.Archive, query);
            session.SetQuery(query);
            if (command == "participants") foreach (var p in session.Archive.Participants) Console.WriteLine($"{p.Id}\t{p.Name}\t{p.MessageCount}");
            else if (command == "inspect")
            {
                Console.WriteLine($"{session.Archive.Name}\nФормат: {session.Archive.SourceFormat}\nСообщений: {session.Archive.Messages.Count}\nУчастников-авторов: {session.Archive.Participants.Count}");
                foreach (var kind in new[] { MessageKind.Photo, MessageKind.Video, MessageKind.File, MessageKind.Audio, MessageKind.Link, MessageKind.Poll })
                    Console.WriteLine($"{kind}: {session.Archive.Messages.Count(m => m.Kind.HasFlag(kind))}");
            }
            else if (command == "search")
            {
                if (!int.TryParse(Get("--limit", "50"), out var limit) || limit is < 1 or > 10000) throw new ArgumentException("--limit: от 1 до 10000.");
                Console.WriteLine($"Найдено: {matches.Count}");
                foreach (var m in matches.Take(limit)) Console.WriteLine($"#{m.Id} {m.DateLabel} | {m.Sender}\n{m.Text}\n{m.PollText}{m.AttachmentLabel}");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Get("--out"))) throw new ArgumentException("Требуется --out ПУТЬ.");
                if (command == "save-state") await StateStore.SaveAsync(Get("--out"), session);
                else await StateStore.ExportCsvAsync(Get("--out"), matches);
                Console.WriteLine("Сохранено: " + Path.GetFullPath(Get("--out")));
            }
            return 0;
        }
        catch (Exception e) when (e is ChatFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine("Ошибка: " + e.Message); return 2; }
    }

    private static DateOnly ParseDate(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw new ArgumentException("Дата должна иметь вид yyyy-MM-dd.");

    private const string Help = """
    Telegram Chat Navigator · .NET 10
    dotnet run --project src/TelegramChatNavigator.Cli -- КОМАНДА ПУТЬ [параметры]
    Команды: inspect, participants, search, save-state, export
    ПУТЬ: папка одного чата, result.json, messages.html или *.tgnav.json.
    --text СТРОКА       Подстрока без учёта регистра (текст, автор, файл, опрос, ссылка)
    --kind КАТЕГОРИЯ    All, Text, Photo, Video, File, Audio, Link, Poll, Service
    --sender ID         ID из команды participants; HTML группирует авторов по имени
    --from yyyy-MM-dd   С начала дня включительно
    --to yyyy-MM-dd     До конца дня включительно
    --limit ЧИСЛО       Сколько результатов печатать (1–10000; по умолчанию 50)
    --out ПУТЬ         Для save-state (.tgnav.json) и export (.csv)
    --help              Справка
    JSON предпочтителен при наличии обоих форматов. CSV не является состоянием.
    Пример: search examples/demo --text маршрут --kind Link
    """;
}
