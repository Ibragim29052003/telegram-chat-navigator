using System.Text.Json;
using System.Text.Json.Nodes;
using TelegramChatNavigator.Core;

#pragma warning disable xUnit1051 // Отмену проверяем отдельными тестами с собственным CancellationToken.

namespace TelegramChatNavigator.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "tg-navigator-tests-" + Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(_temp);
    public void Dispose() => Directory.Delete(_temp, true);
    private static string Demo => Path.Combine(AppContext.BaseDirectory, "examples", "demo");
    private static Task<ChatArchive> Archive() => new ChatImporter().ReadAsync(Demo);
    private async Task<string> Input(string text, string name = "result.json")
    { var file = Path.Combine(_temp, name); await File.WriteAllTextAsync(file, text); return file; }

    [Fact] public async Task JsonReadsKnownRichTextMediaAndPoll()
    {
        var archive = await Archive();
        Assert.Equal(14, archive.Messages.Count);
        Assert.Equal(3, archive.Participants.Count);
        Assert.Equal("Маршрут — полезные материалы: рекомендации МЧС", archive.Messages.Single(m => m.Id == 4).Text);
        Assert.Equal("https://www.mchs.gov.ru/", Assert.Single(archive.Messages.Single(m => m.Id == 4).Links));
        var poll = archive.Messages.Single(m => m.Id == 6).Poll!;
        Assert.Equal(3, poll.TotalVoters); Assert.False(poll.Closed);
        Assert.Equal(new[] { 2, 1 }, poll.Answers.Select(a => a.Voters));
        Assert.Equal(6, archive.Messages.Single(m => m.Id == 10).ReplyToId);
        Assert.Equal(new long[] { 3, 9 }, ChatSearch.Find(archive, new(Kind: MessageKind.Photo)).Select(m => m.Id));
    }
    [Fact] public async Task SearchCombinesFiltersAndInclusiveDates()
    {
        var a = await Archive();
        Assert.Equal(new long[] { 4 }, ChatSearch.Find(a, new("МАРШРУТ", MessageKind.Link, "user300", new(2026, 9, 2), new(2026, 9, 2))).Select(m => m.Id));
        Assert.Equal(new long[] { 6 }, ChatSearch.Find(a, new("Суббота", MessageKind.Poll)).Select(m => m.Id));
        Assert.Equal(new long[] { 5 }, ChatSearch.Find(a, new("Маршрут.txt")).Select(m => m.Id));
        Assert.Empty(ChatSearch.Find(a, new("несуществующаястрока")));
        Assert.Throws<ArgumentException>(() => ChatSearch.Find(a, new(From: new(2027, 1, 1), To: new(2026, 1, 1))));
    }

    [Fact] public async Task JsonMentionEntitiesBecomeTelegramLinksWithoutDuplicating()
    {
        var path = await Input("""
        {"messages":[{"id":1,"date":"2026-09-01T10:00:00","text":[{"type":"mention","text":"@ExampleBot"}],
          "text_entities":[{"type":"mention","text":"@ExampleBot"},{"type":"mention","text":"@bad/name"}]}]}
        """);
        var archive = await new ChatImporter().ReadAsync(path);
        var message = Assert.Single(ChatSearch.Find(archive, new(Kind: MessageKind.Link)));
        Assert.Equal("https://t.me/ExampleBot", Assert.Single(message.Links));
    }

    [Fact] public async Task JsonBareLinkEntitiesAndUrlButtonsPreserveWebAddresses()
    {
        var path = await Input("""
        {"messages":[{"id":1,"date":"2026-09-01T10:00:00","text":"example.org/path",
          "text_entities":[{"type":"link","text":"example.org/path"},{"type":"link","text":"ftp://example.org/"}],
          "inline_bot_buttons":[[{"type":"url","text":"Книга","data":"https://example.org/book"},
            {"type":"callback","data":"https://example.org/callback"},{"type":"url","data":"javascript:alert(1)"}]]}]}
        """);
        var message = Assert.Single((await new ChatImporter().ReadAsync(path)).Messages);
        Assert.Equal(new[] { "https://example.org/path", "https://example.org/book" }, message.Links);
    }
    [Fact] public async Task DateLowerBoundAgreesWithIndependentLinearScan()
    {
        var a = await Archive();
        for (var date = new DateOnly(2026, 8, 1); date < new DateOnly(2026, 12, 1); date = date.AddDays(1))
        {
            var expected = a.Messages.TakeWhile(m => DateOnly.FromDateTime(m.Date) < date).Count();
            Assert.Equal(expected, ChatSearch.FindDate(a.Messages, date));
        }
        Assert.Equal(0, ChatSearch.FindDate([], new(2026, 1, 1)));
    }
    [Fact] public async Task HtmlMergesPagesCarriesSenderAndPreservesLineBreaks()
    {
        var a = await new ChatImporter().ReadAsync(Path.Combine(AppContext.BaseDirectory, "examples", "html-demo"));
        Assert.Equal(3, a.Messages.Count); Assert.Equal(2, a.Participants.Count);
        Assert.Contains("\nВторая строка.", a.Messages[0].Text);
        Assert.Equal("Алина", a.Messages[1].Sender);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0), a.Messages[0].Date);
        Assert.Equal("https://telegram.org/", Assert.Single(a.Messages[1].Links));
        Assert.Equal("Да", a.Messages[2].Poll!.Answers[0].Text);
        Assert.Equal(2, a.Messages[2].Poll!.Answers[0].Voters); Assert.True(a.Messages[2].Poll!.Closed);
    }

    [Fact] public async Task HtmlForwardedBodyKeepsChatAuthorAndReadsForwardedText()
    {
        var path = await Input("""
        <div class="history">
          <div class="message default" id="message1"><div class="body">
            <div class="date" title="01.09.2026 10:00:00"></div><div class="from_name">Канал  via @ExampleBot</div>
            <div class="text">Первая запись</div>
          </div></div>
          <div class="message default joined" id="message2"><div class="body">
            <div class="date" title="01.09.2026 10:01:00"></div>
            <div class="forwarded body"><div class="from_name">Другой автор <span>31.08.2026</span></div>
              <div class="text">Пересланный текст<br>Вторая строка <a href="https://example.com/">источник</a></div>
            </div>
          </div></div>
          <div class="message default joined" id="message3"><div class="body">
            <div class="date" title="01.09.2026 10:02:00"></div><div class="text">После пересылки</div>
          </div></div>
        </div>
        """, "messages.html");
        var archive = await new ChatImporter().ReadAsync(path);
        Assert.Equal("Канал", Assert.Single(archive.Participants).Name);
        Assert.All(archive.Messages, m => Assert.Equal("Канал", m.Sender));
        Assert.Equal("Пересланный текст\nВторая строка источник", archive.Messages[1].Text);
        Assert.Equal("https://example.com/", Assert.Single(archive.Messages[1].Links));
    }

    [Fact] public async Task HtmlPollEmojiLinkIsNotAnAttachment()
    {
        var path = await Input("""
        <div class="history"><div class="message default" id="message1"><div class="body">
          <div class="date" title="01.09.2026 10:00:00"></div><div class="from_name">Автор</div>
          <div class="media_wrap"><div class="media_poll"><div class="question">Вопрос</div>
            <div class="answer">- Другое <a href="5395444784611480792">✏️</a><span class="details">2 votes</span></div>
            <div class="total">2 votes</div>
          </div></div>
        </div></div></div>
        """, "messages.html");
        var message = Assert.Single((await new ChatImporter().ReadAsync(path)).Messages);
        Assert.Empty(message.Attachments); Assert.False(message.Kind.HasFlag(MessageKind.File));
        Assert.Equal("Другое ✏️", message.Poll!.Answers[0].Text); Assert.Equal(2, message.Poll.Answers[0].Voters);
    }
    [Theory]
    [InlineData("{")]
    [InlineData("{\"chats\":{\"list\":[]}}")]
    [InlineData("{\"messages\":[12]}")]
    [InlineData("{\"messages\":[{\"id\":1,\"date\":\"wrong\"}]}")]
    [InlineData("{\"messages\":[{\"id\":1,\"date\":\"2026-01-01\"},{\"id\":1,\"date\":\"2026-01-01\"}]}")]
    public async Task InvalidJsonIsRejected(string json)
    {
        var path = await Input(json);
        await Assert.ThrowsAsync<ChatFormatException>(() => new ChatImporter().ReadAsync(path));
    }

    [Fact] public async Task EmptyAndUnsortedExportsHaveDefinedBehavior()
    {
        var a = await new ChatImporter().ReadAsync(await Input("{\"name\":\"Пусто\",\"messages\":[]}"));
        Assert.Empty(a.Messages); Assert.Empty(a.Participants);
        var b = await new ChatImporter().ReadAsync(await Input("{\"messages\":[{\"id\":2,\"date\":\"2026-02-01\"},{\"id\":1,\"date\":\"2026-01-01\"}]}"));
        Assert.Equal(new long[] { 1, 2 }, b.Messages.Select(m => m.Id));
    }
    [Fact] public async Task UnsupportedFolderAndMalformedHtmlFailClearly()
    {
        await Assert.ThrowsAsync<ChatFormatException>(() => new ChatImporter().ReadAsync(_temp));
        await Input("<div class='message default' id='message1'><div class='body'>no date</div></div>", "messages.html");
        await Assert.ThrowsAsync<ChatFormatException>(() => new ChatImporter().ReadAsync(_temp));
    }
    [Fact] public async Task CanceledImportAndSizeLimitAreEnforced()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChatImporter().ReadAsync(Demo, canceled.Token));
        var path = Path.Combine(_temp, "result.json");
        using (var stream = File.Create(path)) stream.SetLength(128L * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<ChatFormatException>(() => new ChatImporter().ReadAsync(path));
    }
    [Fact] public async Task StateRoundTripRestoresFiltersSelectionAndRelativeMedia()
    {
        var session = new NavigatorSession(); var a = await Archive();
        session.Replace(a, new("маршрут", MessageKind.Link, "user300", new(2026, 9, 1), new(2026, 9, 30)), 4);
        var path = Path.Combine(_temp, "saved.tgnav.json"); await StateStore.SaveAsync(path, session);
        var state = await StateStore.LoadAsync(path);
        Assert.Equal(session.Query, state.Query); Assert.Equal(4, state.SelectedMessageId);
        Assert.Equal(a.RootPath, state.Archive.RootPath);
        var differentVolumes = !string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(a.RootPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal(differentVolumes, Path.IsPathRooted(state.MediaRoot));
        Assert.Equal(a.Messages.Select(m => m.Text), state.Archive.Messages.Select(m => m.Text));
        Assert.Single(ChatSearch.Find(state.Archive, state.Query));
    }
    [Theory]
    [InlineData("Version", "99")]
    [InlineData("Archive", "null")]
    [InlineData("Query", "null")]
    [InlineData("SelectedMessageId", "999")]
    public async Task DamagedStateCannotReplaceSession(string property, string json)
    {
        var session = new NavigatorSession(); session.Replace(await Archive());
        var path = Path.Combine(_temp, "state.tgnav.json"); await StateStore.SaveAsync(path, session);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!; node[property] = JsonNode.Parse(json);
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Assert.ThrowsAsync<ChatFormatException>(() => StateStore.LoadAsync(path));
        Assert.Equal(14, session.Archive.Messages.Count);
    }
    [Fact] public async Task CanceledAtomicSaveKeepsPreviousFile()
    {
        var session = new NavigatorSession(); session.Replace(await Archive());
        var path = Path.Combine(_temp, "state.tgnav.json"); await File.WriteAllTextAsync(path, "previous");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StateStore.SaveAsync(path, session, canceled.Token));
        Assert.Equal("previous", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_temp));
    }
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("../../photos/a.jpg")]
    public void MediaCannotEscapeRoot(string relative) => Assert.Throws<IOException>(() => SafePaths.ResolveMedia(_temp, relative));
    [Fact] public void SymlinkCannotEscapeRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "tg-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(_temp, "photos"), outside); Assert.Throws<IOException>(() => SafePaths.ResolveMedia(_temp, "photos/a.jpg")); }
        finally { Directory.Delete(Path.Combine(_temp, "photos")); Directory.Delete(outside); }
    }
    [Theory]
    [InlineData("https://www.openstreetmap.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    public void LinkSchemesAreExplicit(string link, bool expected) => Assert.Equal(expected, SafePaths.IsWebLink(link));
    [Fact] public async Task CsvEscapesQuotesNewlinesAndFormulaPrefixes()
    {
        var m = new ChatMessage(1, new(2026, 1, 1), "1", "=1+1", "a,\"b\"\nsecond", MessageKind.Text, [], []);
        var path = Path.Combine(_temp, "out.csv"); await StateStore.ExportCsvAsync(path, [m]);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"'=1+1\"", text); Assert.Contains("\"a,\"\"b\"\"\nsecond\"", text);
    }
    [Fact] public async Task PreferencesFallbackAndRoundTrip()
    {
        var path = await Input("broken", "prefs.json");
        Assert.Equal(new UserPreferences(), await StateStore.LoadPreferencesAsync(path));
        await StateStore.SavePreferencesAsync(path, new(1, "Dark", 18));
        Assert.Equal(new(1, "Dark", 18), await StateStore.LoadPreferencesAsync(path));
    }
    [Fact] public async Task StateAndMediaRemainPortableAndTextSurvivesMissingSource()
    {
        var source = Path.Combine(_temp, "before"); var export = Path.Combine(source, "export");
        Directory.CreateDirectory(Path.Combine(export, "photos"));
        File.Copy(Path.Combine(Demo, "photos", "lake.png"), Path.Combine(export, "photos", "lake.png"));
        var session = new NavigatorSession(); session.Replace((await Archive()) with { RootPath = export });
        await StateStore.SaveAsync(Path.Combine(source, "chat.tgnav.json"), session);
        var moved = Path.Combine(_temp, "after"); Directory.Move(source, moved);
        var state = await StateStore.LoadAsync(Path.Combine(moved, "chat.tgnav.json"));
        Assert.Equal(Path.Combine(moved, "export"), state.Archive.RootPath);
        Assert.True(File.Exists(SafePaths.ResolveMedia(state.Archive.RootPath, "photos/lake.png")));
        Directory.Delete(Path.Combine(moved, "export"), true);
        var withoutMedia = await StateStore.LoadAsync(Path.Combine(moved, "chat.tgnav.json"));
        Assert.Equal(14, withoutMedia.Archive.Messages.Count);
    }
    [Fact] public async Task SessionPublishesDomainChanges()
    {
        var session = new NavigatorSession(); var reasons = new List<string>();
        session.Changed += (_, e) => { Assert.Equal(14, e.MessageCount); reasons.Add(e.Reason); };
        session.Replace(await Archive()); session.SetQuery(new("маршрут")); session.Select(4);
        Assert.Equal(new[] { "Загружен чат", "Изменён фильтр", "Выбрано сообщение" }, reasons);
    }

    [Fact] public async Task FavoritesCombineWithAuthorPeriodCategoryAndOrder()
    {
        var archive = await Archive();
        var favorites = new HashSet<long> { 3, 4, 5, 12 };
        var query = new ChatQuery("маршрут", MessageKind.Link, From: new(2026, 9, 1), To: new(2026, 10, 31),
            FavoritesOnly: true, Order: MessageOrder.NewestFirst);
        Assert.Equal(new long[] { 12, 4 }, ChatSearch.Find(archive, query, favoriteMessageIds: favorites).Select(m => m.Id));
        Assert.Equal(new long[] { 12 }, ChatSearch.Find(archive, query with { SenderId = "user200" }, favoriteMessageIds: favorites).Select(m => m.Id));
        Assert.Empty(ChatSearch.Find(archive, query));
        Assert.Equal(14, ChatSearch.Find(archive, new(Order: MessageOrder.NewestFirst))[0].Id);
    }

    [Fact] public async Task FavoritesRoundTripAndOldStateRemainsReadable()
    {
        var session = new NavigatorSession(); session.Replace(await Archive());
        session.ToggleFavorite(4); session.ToggleFavorite(12); session.ToggleFavorite(3); session.ToggleFavorite(3);
        Assert.Throws<ArgumentException>(() => session.ToggleFavorite(999));
        session.SetQuery(new(FavoritesOnly: true, Order: MessageOrder.NewestFirst)); session.Select(12);
        var path = Path.Combine(_temp, "favorites.tgnav.json"); await StateStore.SaveAsync(path, session);
        var state = await StateStore.LoadAsync(path);
        Assert.Equal(new long[] { 4, 12 }, state.FavoriteMessageIds); Assert.Equal(session.Query, state.Query);
        var restored = new NavigatorSession(); restored.Replace(state.Archive, state.Query, state.SelectedMessageId, state.FavoriteMessageIds);
        Assert.True(restored.FavoriteMessageIds.SetEquals(new long[] { 4, 12 }));
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node.AsObject().Remove("FavoriteMessageIds");
        node["Query"]!.AsObject().Remove("FavoritesOnly"); node["Query"]!.AsObject().Remove("Order");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var old = await StateStore.LoadAsync(path);
        Assert.Null(old.FavoriteMessageIds); Assert.False(old.Query.FavoritesOnly); Assert.Equal(MessageOrder.OldestFirst, old.Query.Order);
        restored.Replace(old.Archive); Assert.Empty(restored.FavoriteMessageIds);
    }

    [Theory]
    [InlineData("[999]")]
    [InlineData("[4,4]")]
    public async Task InvalidFavoritesAreRejectedBeforeRestoring(string ids)
    {
        var session = new NavigatorSession(); session.Replace(await Archive());
        var path = Path.Combine(_temp, "bad-favorites.tgnav.json"); await StateStore.SaveAsync(path, session);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!; node["FavoriteMessageIds"] = JsonNode.Parse(ids);
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Assert.ThrowsAsync<ChatFormatException>(() => StateStore.LoadAsync(path));
    }
}
