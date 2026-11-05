namespace TelegramChatNavigator.Core;

public sealed class SessionChangedEventArgs(string reason, int messageCount) : EventArgs
{
    public string Reason { get; } = reason;
    public int MessageCount { get; } = messageCount;
}

public sealed class NavigatorSession
{
    public ChatArchive Archive { get; private set; } = new("Новый чат", "", []);
    public ChatQuery Query { get; private set; } = new();
    public long? SelectedMessageId { get; private set; }
    // Окно подписывается на событие и обновляет строку состояния при изменении работы.
    public event EventHandler<SessionChangedEventArgs>? Changed;

    public void Replace(ChatArchive archive, ChatQuery? query = null, long? selectedMessageId = null)
    {
        Archive = archive;
        Query = query ?? new();
        SelectedMessageId = selectedMessageId;
        Changed?.Invoke(this, new("Загружен чат", archive.Messages.Count));
    }

    public void SetQuery(ChatQuery query)
    {
        Query = query;
        Changed?.Invoke(this, new("Изменён фильтр", Archive.Messages.Count));
    }

    public void Select(long? messageId)
    {
        SelectedMessageId = messageId;
        Changed?.Invoke(this, new("Выбрано сообщение", Archive.Messages.Count));
    }
}
