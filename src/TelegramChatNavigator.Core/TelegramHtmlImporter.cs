using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using static TelegramChatNavigator.Core.ImportSupport;

namespace TelegramChatNavigator.Core;

public sealed partial class TelegramHtmlImporter : IChatImporter
{
    public bool CanRead(string path) => Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase);

    public async Task<ChatArchive> ReadAsync(string path, CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var files = Directory.GetFiles(directory, "messages*.html")
            .Where(f => PageName().IsMatch(Path.GetFileName(f)))
            .OrderBy(f => PageNumber(f)).ToArray();
        if (files.Length == 0) throw new ChatFormatException("В папке нет messages.html / messages2.html из Telegram Desktop.");
        if (files.Length > 500 || files.Sum(f => new FileInfo(f).Length) > 256L * 1024 * 1024)
            throw new ChatFormatException("HTML-экспорт слишком велик (до 500 страниц и 256 МиБ). Разделите экспорт.");
        var messages = new List<ChatMessage>();
        string name = "", sender = "Неизвестный участник";
        DateTime separatorDay = default;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckSize(file);
            var html = await File.ReadAllTextAsync(file, cancellationToken);
            // Разбираем HTML как данные. Скрипты, стили и сетевые запросы не выполняются.
            using var document = new HtmlParser().ParseDocument(html);
            if (name.Length == 0) name = document.QuerySelector(".page_header .text")?.TextContent.Trim() ?? "Чат HTML";
            var nodes = document.QuerySelectorAll(".message[id]");
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawId = node.Id ?? "";
                if (rawId.StartsWith("message-", StringComparison.Ordinal))
                {
                    DateTime.TryParse(node.QuerySelector(".body")?.TextContent.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out separatorDay);
                    continue;
                }
                if (!rawId.StartsWith("message", StringComparison.Ordinal) || !long.TryParse(rawId[7..], out var id) || id <= 0) continue;
                var body = node.Children.FirstOrDefault(e => e.ClassList.Contains("body"));
                var dateText = body?.Children.FirstOrDefault(e => e.ClassList.Contains("date") && e.HasAttribute("title"))?.GetAttribute("title");
                // Разделители дней имеют отрицательный ID и не считаются сообщениями.
                var validDate = TryDate(dateText, out var date);
                if (!validDate && node.ClassList.Contains("service") && separatorDay != default) date = separatorDay.Date;
                else if (!validDate) throw new ChatFormatException($"HTML: нет корректной даты у сообщения {id}.");
                // Вложенный forwarded.body содержит автора пересылки, а не автора сообщения в чате.
                var ownSender = body?.Children.FirstOrDefault(e => e.ClassList.Contains("from_name"))?.TextContent.Trim();
                if (ownSender is not null) ownSender = ViaBotSuffix().Replace(ownSender, "");
                if (!string.IsNullOrWhiteSpace(ownSender)) sender = ownSender;
                var service = node.ClassList.Contains("service");
                var contentBody = body?.Children.FirstOrDefault(e => e.ClassList.Contains("forwarded") && e.ClassList.Contains("body")) ?? body;
                var textNode = contentBody?.Children.FirstOrDefault(x => x.ClassList.Contains("text"));
                var text = textNode is null ? service ? body?.TextContent.Trim() ?? "Служебное событие" : "" : PlainText(textNode).Trim();
                var attachments = new List<Attachment>();
                var links = ExtractLinks(text).ToList();
                foreach (var anchor in node.QuerySelectorAll("a[href]"))
                {
                    var href = anchor.GetAttribute("href") ?? "";
                    if (SafePaths.IsWebLink(href)) { links.Add(href); continue; }
                    // Telegram может записать emoji варианта опроса как ссылку с числовым ID.
                    if (anchor.Closest(".media_poll") is not null) continue;
                    if (href.StartsWith('#') || href.Contains("://", StringComparison.Ordinal) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (anchor.Closest(".media_wrap") is null && !anchor.ClassList.Contains("photo_wrap")) continue;
                    var relative = Uri.UnescapeDataString(href);
                    var kind = anchor.ClassList.Contains("photo_wrap") ? MessageKind.Photo : FileKind("", relative);
                    if (anchor.ClassName?.Contains("video", StringComparison.Ordinal) == true) kind = MessageKind.Video;
                    if (anchor.ClassName?.Contains("audio", StringComparison.Ordinal) == true || anchor.ClassName?.Contains("voice", StringComparison.Ordinal) == true) kind = MessageKind.Audio;
                    attachments.Add(new(relative, kind, Path.GetFileName(relative)));
                }
                foreach (var omitted in node.QuerySelectorAll(".media_wrap .media_video, .media_wrap .media_audio_file, .media_wrap .media_voice_message, .media_wrap .media_file"))
                {
                    if (omitted.LocalName == "a" || omitted.Closest("a[href]") is not null) continue;
                    var k = omitted.ClassList.Contains("media_video") ? MessageKind.Video : omitted.ClassList.Contains("media_file") ? MessageKind.File : MessageKind.Audio;
                    attachments.Add(new("(not exported)", k, omitted.QuerySelector(".title")?.TextContent.Trim() ?? "Файл не включён в экспорт"));
                }
                ChatPoll? poll = null;
                var pollNode = node.QuerySelector(".media_poll");
                if (pollNode is not null)
                {
                    var answers = pollNode.QuerySelectorAll(".answer").Select(a => new PollAnswer(
                        string.Concat(a.ChildNodes.Where(n => n is not IElement e || !e.ClassList.Contains("details")).Select(PlainText)).Trim().TrimStart('-', ' '),
                        FirstInteger(a.QuerySelector(".details")?.TextContent))).ToArray();
                    poll = new(pollNode.QuerySelector(".question")?.TextContent.Trim() ?? "Опрос", FirstInteger(pollNode.QuerySelector(".total")?.TextContent),
                        pollNode.QuerySelector(".details")?.TextContent.Contains("Final results", StringComparison.OrdinalIgnoreCase) == true, answers);
                }
                var kindFlags = service ? MessageKind.Service : MessageKind.Text;
                foreach (var a in attachments) kindFlags |= a.Kind;
                if (links.Count > 0) kindFlags |= MessageKind.Link;
                if (poll is not null) kindFlags |= MessageKind.Poll;
                var replyHref = node.QuerySelector(".reply_to a")?.GetAttribute("href") ?? "";
                var reply = Regex.Match(replyHref, @"#go_to_message(\d+)|#message(\d+)");
                long? replyId = reply.Success ? long.Parse(reply.Groups[1].Success ? reply.Groups[1].Value : reply.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                messages.Add(new(id, date, service ? "service" : "html:" + sender, service ? "Система" : sender, text, kindFlags,
                    attachments.DistinctBy(a => a.RelativePath).ToArray(), links.Distinct().ToArray(), poll, replyId));
                if (messages.Count > MaxMessages) throw new ChatFormatException("Больше 200 000 сообщений. Разделите экспорт.");
            }
            progress?.Report(new(messages.Count, Path.GetFileName(file)));
        }
        // В пустом экспорте Telegram всё равно присутствует контейнер history.
        if (messages.Count == 0 && !File.ReadAllText(files[0]).Contains("history", StringComparison.Ordinal))
            throw new ChatFormatException("HTML не похож на экспорт Telegram Desktop.");
        return Finish(name, "Telegram HTML", directory, messages);
    }

    [GeneratedRegex(@"^messages(\d*)\.html$", RegexOptions.IgnoreCase)] private static partial Regex PageName();
    [GeneratedRegex(@"\s{2,}via @[A-Za-z0-9_]+$")] private static partial Regex ViaBotSuffix();
    private static int PageNumber(string file) => int.TryParse(PageName().Match(Path.GetFileName(file)).Groups[1].Value, out var n) ? n : 1;
    private static int FirstInteger(string? text) => int.TryParse(Regex.Match(text ?? "", @"\d+").Value, out var n) ? n : 0;
    private static bool TryDate(string? text, out DateTime date)
    {
        // Отбрасываем обозначение пояса, сохраняя местное время из экспорта, как в JSON.
        text = Regex.Replace(text ?? "", @"\s+(UTC|GMT).*$", "");
        return DateTime.TryParseExact(text, ["dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
    private static string PlainText(INode node) => node is IElement { LocalName: "br" } ? "\n" :
        node.HasChildNodes ? string.Concat(node.ChildNodes.Select(PlainText)) : node.TextContent;
}
