using System.Text.Json;
using static TelegramChatNavigator.Core.ImportSupport;

namespace TelegramChatNavigator.Core;

public sealed class TelegramJsonImporter : IChatImporter
{
    public bool CanRead(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public async Task<ChatArchive> ReadAsync(string path, CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null)
    {
        CheckSize(path);
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new ChatFormatException("Нужен JSON экспорта одного чата с массивом messages. Общий экспорт аккаунта не поддерживается.");
            if (list.GetArrayLength() > MaxMessages) throw new ChatFormatException("Больше 200 000 сообщений. Разделите экспорт по датам.");
            var messages = new List<ChatMessage>(list.GetArrayLength());
            foreach (var item in list.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object) throw new ChatFormatException("Сообщение должно быть JSON-объектом.");
                var id = Long(item, "id", -1);
                if (id <= 0) throw new ChatFormatException("Сообщение без положительного id.");
                var text = item.TryGetProperty("text", out var value) ? RichText(value) : "";
                var links = ExtractLinks(text).ToList();
                if (item.TryGetProperty("text_entities", out var entities)) AddEntityLinks(entities, links);
                if (item.TryGetProperty("text", out var rich)) AddEntityLinks(rich, links);
                var attachments = new List<Attachment>();
                var photo = String(item, "photo");
                if (photo.Length > 0) attachments.Add(new(photo, MessageKind.Photo, Path.GetFileName(photo)));
                var file = String(item, "file");
                if (file.Length > 0) attachments.Add(new(file, FileKind(String(item, "media_type"), file), String(item, "file_name", Path.GetFileName(file))));
                ChatPoll? poll = null;
                if (item.TryGetProperty("poll", out var p))
                {
                    var answers = p.TryGetProperty("answers", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => new PollAnswer(x.TryGetProperty("text", out var t) ? RichText(t) : "", checked((int)Long(x, "voters")))).ToArray() : [];
                    poll = new(p.TryGetProperty("question", out var q) ? RichText(q) : "Опрос", checked((int)Long(p, "total_voters")), String(p, "closed").Equals("True", StringComparison.OrdinalIgnoreCase), answers);
                }
                var service = String(item, "type") == "service";
                if (service && text.Length == 0) text = $"Служебное событие: {String(item, "action", "неизвестное")}";
                var sender = String(item, "from", String(item, "actor", "Неизвестный участник"));
                var senderId = String(item, "from_id", String(item, "actor_id", "name:" + sender));
                var kind = service ? MessageKind.Service : MessageKind.Text;
                foreach (var attachment in attachments) kind |= attachment.Kind;
                if (links.Count > 0) kind |= MessageKind.Link;
                if (poll is not null) kind |= MessageKind.Poll;
                messages.Add(new(id, Date(String(item, "date")), senderId, sender, text, kind, attachments,
                    links.Where(SafePaths.IsWebLink).Distinct().ToArray(), poll,
                    item.TryGetProperty("reply_to_message_id", out _) ? Long(item, "reply_to_message_id") : null));
                if (messages.Count % 1000 == 0) progress?.Report(new(messages.Count, "Чтение JSON"));
            }
            return Finish(String(root, "name"), "Telegram JSON", Path.GetDirectoryName(path)!, messages);
        }
        catch (JsonException e) { throw new ChatFormatException("JSON повреждён или имеет неверную структуру.", e); }
        catch (InvalidOperationException e) { throw new ChatFormatException("Некорректные типы полей JSON.", e); }
        catch (OverflowException e) { throw new ChatFormatException("Число в экспорте выходит за допустимые границы.", e); }
    }

    private static void AddEntityLinks(JsonElement value, List<string> links)
    {
        if (value.ValueKind != JsonValueKind.Array) return;
        foreach (var entity in value.EnumerateArray())
        {
            if (entity.ValueKind != JsonValueKind.Object) continue;
            var href = String(entity, "href");
            if (SafePaths.IsWebLink(href)) links.Add(href);
        }
    }
}
