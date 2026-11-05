using System.Text.Json.Serialization;

namespace TelegramChatNavigator.Core;

// Одно сообщение может содержать сразу текст, фотографию и ссылку.
[Flags]
public enum MessageKind { All = 0, Text = 1, Photo = 2, Video = 4, File = 8, Audio = 16, Link = 32, Poll = 64, Service = 128 }

public sealed record Attachment(string RelativePath, MessageKind Kind, string Name);
public sealed record PollAnswer(string Text, int Voters);
public sealed record ChatPoll(string Question, int TotalVoters, bool Closed, IReadOnlyList<PollAnswer> Answers);

public sealed record ChatMessage(long Id, DateTime Date, string SenderId, string Sender,
    string Text, MessageKind Kind, IReadOnlyList<Attachment> Attachments,
    IReadOnlyList<string> Links, ChatPoll? Poll = null, long? ReplyToId = null)
{
    [JsonIgnore] public string DateLabel => Date.ToString("dd.MM.yyyy HH:mm");
    [JsonIgnore] public string KindLabel => Kind.ToString();
    [JsonIgnore] public bool HasAttachments => Attachments.Count > 0;
    [JsonIgnore] public bool HasPoll => Poll is not null;
    [JsonIgnore] public bool HasText => Text.Length > 0;
    [JsonIgnore] public string PollText => Poll is null ? "" :
        $"Опрос: {Poll.Question}\n" + string.Join("\n", Poll.Answers.Select(a => $"• {a.Text} — {a.Voters}")) +
        $"\nУчастников: {Poll.TotalVoters}" + (Poll.Closed ? " · завершён" : " · открыт на момент экспорта");
    [JsonIgnore] public string AttachmentLabel => string.Join("\n", Attachments.Select(a => a.Name));
    [JsonIgnore] public string SearchText => $"{Text}\n{Sender}\n{AttachmentLabel}\n{string.Join(' ', Links)}\n{PollText}";
}

public sealed record Participant(string Id, string Name, int MessageCount)
{
    public override string ToString() => $"{Name} ({MessageCount})";
}

public sealed record ChatArchive(string Name, string SourceFormat, IReadOnlyList<ChatMessage> Messages)
{
    [JsonIgnore] public string RootPath { get; init; } = "";
    [JsonIgnore] public IReadOnlyList<Participant> Participants => Messages
        .Where(m => !m.Kind.HasFlag(MessageKind.Service))
        .GroupBy(m => m.SenderId).Select(g => new Participant(g.Key, g.Last().Sender, g.Count()))
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record ChatQuery(string Text = "", MessageKind Kind = MessageKind.All,
    string? SenderId = null, DateOnly? From = null, DateOnly? To = null);

public sealed record ImportProgress(int Messages, string Stage);

public interface IChatImporter
{
    bool CanRead(string path);
    Task<ChatArchive> ReadAsync(string path, CancellationToken cancellationToken = default,
        IProgress<ImportProgress>? progress = null);
}

public sealed class ChatFormatException(string message, Exception? inner = null) : Exception(message, inner);

public static class ChatSearch
{
    public static IReadOnlyList<ChatMessage> Find(ChatArchive archive, ChatQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.From > query.To) throw new ArgumentException("Начало периода позже его конца.");
        var result = new List<ChatMessage>();
        foreach (var message in archive.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = DateOnly.FromDateTime(message.Date);
            if (query.Kind != MessageKind.All && !message.Kind.HasFlag(query.Kind)) continue;
            if (query.SenderId is not null && message.SenderId != query.SenderId) continue;
            if (query.From is not null && date < query.From || query.To is not null && date > query.To) continue;
            if (!string.IsNullOrWhiteSpace(query.Text) &&
                !message.SearchText.Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(message);
        }
        return result;
    }

    // Нижняя граница: первое сообщение указанного дня или ближайшего следующего дня.
    public static int FindDate(IReadOnlyList<ChatMessage> sorted, DateOnly day)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (DateOnly.FromDateTime(sorted[middle].Date) < day) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
