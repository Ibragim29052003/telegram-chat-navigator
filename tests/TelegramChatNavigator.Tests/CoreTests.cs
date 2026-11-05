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
        Assert.False(Path.IsPathRooted(state.MediaRoot));
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
}
