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
    private HashSet<long> _favoriteMessageIds = [];
    public IReadOnlySet<long> FavoriteMessageIds => _favoriteMessageIds;
    // Окно подписывается на событие и обновляет строку состояния при изменении работы.
    public event EventHandler<SessionChangedEventArgs>? Changed;

    public void Replace(ChatArchive archive, ChatQuery? query = null, long? selectedMessageId = null,
        IEnumerable<long>? favoriteMessageIds = null)
    {
        var favorites = favoriteMessageIds?.ToHashSet() ?? [];
        var ids = archive.Messages.Select(m => m.Id).ToHashSet();
        if (!favorites.IsSubsetOf(ids)) throw new ArgumentException("Избранное содержит отсутствующее сообщение.");
        Archive = archive;
        Query = query ?? new();
        SelectedMessageId = selectedMessageId;
        _favoriteMessageIds = favorites;
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

    public void ToggleFavorite(long messageId)
    {
        if (!Archive.Messages.Any(m => m.Id == messageId))
            throw new ArgumentException("Сообщение отсутствует в чате.");
        if (!_favoriteMessageIds.Add(messageId)) _favoriteMessageIds.Remove(messageId);
        Changed?.Invoke(this, new("Изменено избранное", Archive.Messages.Count));
    }
}
