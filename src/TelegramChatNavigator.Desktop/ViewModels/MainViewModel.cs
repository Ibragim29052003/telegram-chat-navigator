using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using TelegramChatNavigator.Core;

namespace TelegramChatNavigator.Desktop.ViewModels;

public sealed record KindOption(MessageKind Value, string Name)
{
    public override string ToString() => Name;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopServices _services;
    private readonly NavigatorSession _session = new();
    private IReadOnlyList<ChatMessage> _matches = [];
    private int _page;
    private const int PageSize = 50;
    private int _previewVersion;
    private string _searchText = "", _fromText = "", _toText = "", _jumpText = "2026-09-01";
    private string _title = "Telegram Chat Navigator", _summary = "Откройте экспорт или демонстрационный чат";
    private string _status = "Готово", _activity = "", _previewNote = "Выберите вложение";
    private bool _isBusy;
    private ChatMessage? _selectedMessage;
    private Attachment? _selectedAttachment;
    private string? _selectedLink, _selectedMonth;
    private Bitmap? _previewImage;
    private Participant? _selectedParticipant;
    private KindOption _selectedKind;
    private string _selectedTheme = "System";
    private double _fontSize = 15;

    public MainViewModel(IDesktopServices services)
    {
        _services = services;
        _selectedKind = Kinds[0];
        _session.Changed += OnSessionChanged;
        OpenCommand = new AsyncCommand(() => Guard(async () => { var path = await services.PickExportFolderAsync(); if (path is not null) await ImportCore(path); }));
        DemoCommand = new AsyncCommand(() => OpenPathAsync(Path.Combine(AppContext.BaseDirectory, "examples", "demo")));
        LoadCommand = new AsyncCommand(() => Guard(async () => { var path = await services.PickStateAsync(); if (path is not null) await LoadCore(path); }));
        SaveCommand = new AsyncCommand(() => Guard(async () =>
        {
            var path = await services.PickSaveAsync(false); if (path is null) return;
            await ApplyCore(true); await Task.Run(() => StateStore.SaveAsync(path, _session)); Status = "Состояние сохранено: " + path;
            if (Path.IsPathRooted(Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, _session.Archive.RootPath)))
                Status += ". Медиа на другом диске: при переносе выберите новую папку медиа.";
        }));
        ExportCommand = new AsyncCommand(() => Guard(async () =>
        {
            var path = await services.PickSaveAsync(true); if (path is null) return;
            await ApplyCore(true); await Task.Run(() => StateStore.ExportCsvAsync(path, _matches)); Status = $"CSV: {_matches.Count} сообщений · {path}";
        }));
        ApplyCommand = new AsyncCommand(() => Guard(ApplyCore));
        ResetCommand = new AsyncCommand(() => Guard(async () => { ResetInputs(); await ApplyCore(); }));
        NextPageCommand = new AsyncCommand(() => { if (!IsBusy && (_page + 1) * PageSize < _matches.Count) { _page++; ShowPage(); } return Task.CompletedTask; });
        PreviousPageCommand = new AsyncCommand(() => { if (!IsBusy && _page > 0) { _page--; ShowPage(); } return Task.CompletedTask; });
        JumpCommand = new AsyncCommand(() => Guard(() => JumpCore(ParseDate(JumpText, "Дата перехода"))));
        MonthCommand = new AsyncCommand(() => Guard(() => JumpCore(ParseDate((SelectedMonth ?? "") + "-01", "Месяц"))));
        ContextCommand = new AsyncCommand(() => Guard(async () =>
        {
            var id = SelectedMessage?.Id; ResetInputs(); await ApplyCore(); SelectId(id);
        }));
        ReplyCommand = new AsyncCommand(() => Guard(async () =>
        {
            var id = SelectedMessage?.ReplyToId;
            if (id is null) { Status = "У сообщения нет ссылки на ответ."; return; }
            if (!_session.Archive.Messages.Any(m => m.Id == id)) { Status = "Исходное сообщение не вошло в экспорт."; return; }
            ResetInputs(); await ApplyCore(); SelectId(id);
        }));
        OpenAttachmentCommand = new AsyncCommand(() => Guard(async () =>
        {
            if (SelectedAttachment is null) { Status = "Сначала выберите вложение."; return; }
            var path = SafePaths.ResolveMedia(_session.Archive.RootPath, SelectedAttachment.RelativePath);
            await services.OpenAttachmentAsync(path); Status = "Вложение передано системному просмотрщику. При необходимости нажмите Play в его окне.";
        }));
        LinkCommand = new AsyncCommand(() => Guard(async () =>
        {
            if (SelectedLink is null) { Status = "Сначала выберите ссылку."; return; }
            await services.OpenLinkAsync(SelectedLink); Status = "Ссылка открыта в браузере.";
        }));
        RelocateCommand = new AsyncCommand(() => Guard(async () =>
        {
            var path = await services.PickExportFolderAsync(); if (path is null) return;
            _session.Replace(_session.Archive with { RootPath = path }, _session.Query, _session.SelectedMessageId);
            await UpdatePreview(); Status = "Новая папка медиа: " + path + ". Сохраните состояние.";
        }));
        HelpCommand = new AsyncCommand(() => services.ShowHelpAsync(HelpText));
        PreferencesCommand = new AsyncCommand(() => Guard(async () =>
        {
            ApplyTheme(); await StateStore.SavePreferencesAsync(services.PreferencesPath, new(1, SelectedTheme, MessageFontSize)); Status = "Тема и размер текста сохранены.";
        }));
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<Participant> Participants { get; } = [];
    public ObservableCollection<string> Months { get; } = [];
    public IReadOnlyList<KindOption> Kinds { get; } = [new(MessageKind.All, "Все сообщения"), new(MessageKind.Text, "Все обычные сообщения"), new(MessageKind.Photo, "Фотографии"), new(MessageKind.Video, "Видео"), new(MessageKind.File, "Файлы"), new(MessageKind.Audio, "Аудио и голосовые"), new(MessageKind.Link, "Ссылки"), new(MessageKind.Poll, "Опросы"), new(MessageKind.Service, "Служебные события")];
    public string[] Themes { get; } = ["System", "Light", "Dark"];
    public double[] FontSizes { get; } = [12, 15, 18, 21];
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public string FromText { get => _fromText; set => Set(ref _fromText, value); }
    public string ToText { get => _toText; set => Set(ref _toText, value); }
    public string JumpText { get => _jumpText; set => Set(ref _jumpText, value); }
    public KindOption SelectedKind { get => _selectedKind; set => Set(ref _selectedKind, value ?? Kinds[0]); }
    public Participant? SelectedParticipant { get => _selectedParticipant; set => Set(ref _selectedParticipant, value); }
    public string? SelectedMonth { get => _selectedMonth; set => Set(ref _selectedMonth, value); }
    public string SelectedTheme { get => _selectedTheme; set { if (Set(ref _selectedTheme, value)) ApplyTheme(); } }
    public double MessageFontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Activity { get => _activity; private set => Set(ref _activity, value); }
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsReady)); } }
    public bool IsReady => !IsBusy;
    public string PageLabel => _matches.Count == 0 ? "Нет результатов" : $"{_page * PageSize + 1}–{Math.Min((_page + 1) * PageSize, _matches.Count)} из {_matches.Count}";
    public string SelectedHeading => SelectedMessage is null ? "Выберите сообщение" : $"#{SelectedMessage.Id} · {SelectedMessage.Sender}";
    public ChatMessage? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!Set(ref _selectedMessage, value)) return;
            _session.Select(value?.Id);
            Raise(nameof(SelectedHeading)); Raise(nameof(Attachments)); Raise(nameof(Links));
            SelectedAttachment = value?.Attachments.FirstOrDefault();
            SelectedLink = value?.Links.FirstOrDefault();
        }
    }
    public IReadOnlyList<Attachment> Attachments => SelectedMessage?.Attachments ?? [];
    public IReadOnlyList<string> Links => SelectedMessage?.Links ?? [];
    public Attachment? SelectedAttachment
    {
        get => _selectedAttachment;
        set { Set(ref _selectedAttachment, value); _ = UpdatePreview(); }
    }
    public string? SelectedLink { get => _selectedLink; set => Set(ref _selectedLink, value); }
    public Bitmap? PreviewImage { get => _previewImage; private set { var old = _previewImage; Set(ref _previewImage, value); old?.Dispose(); } }
    public string PreviewNote { get => _previewNote; private set => Set(ref _previewNote, value); }
    public ICommand OpenCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand JumpCommand { get; }
    public ICommand MonthCommand { get; }
    public ICommand ContextCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand OpenAttachmentCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand RelocateCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand PreferencesCommand { get; }

    public async Task InitializeAsync(string[] args)
    {
        var preferences = await StateStore.LoadPreferencesAsync(_services.PreferencesPath);
        SelectedTheme = preferences.Theme; MessageFontSize = preferences.FontSize; ApplyTheme();
        if (args.Length == 0) await OpenPathAsync(Path.Combine(AppContext.BaseDirectory, "examples", "demo"));
        else if (args.Length == 2 && args[0] == "--open") await OpenPathAsync(args[1]);
        else if (args.Length == 2 && args[0] == "--state") await OpenStatePathAsync(args[1]);
        else Status = "Параметры: --open ПАПКА или --state ФАЙЛ.tgnav.json. Справка: F1.";
    }

    public Task OpenPathAsync(string path) => Guard(() => ImportCore(path));
    public Task OpenStatePathAsync(string path) => Guard(() => LoadCore(path));
    public Task ApplyAsync() => Guard(ApplyCore);

    private async Task Guard(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception e) when (e is ChatFormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Status = "Ошибка: " + e.Message; }
        finally { IsBusy = false; }
    }

    private async Task ImportCore(string path)
    {
        // Чтение большого архива выполняется в фоне, чтобы окно оставалось отзывчивым.
        Status = "Чтение экспорта…";
        var progress = new Progress<ImportProgress>(p => Status = $"{p.Stage}: {p.Messages} сообщений…");
        var archive = await Task.Run(() => new ChatImporter().ReadAsync(path, progress: progress));
        var matches = await Task.Run(() => ChatSearch.Find(archive, new()));
        CommitArchive(archive, new(), null, matches);
        Status = $"Открыт {archive.SourceFormat}. Оригиналы файлов не изменяются.";
    }
    private async Task LoadCore(string path)
    {
        var state = await Task.Run(() => StateStore.LoadAsync(path));
        var matches = await Task.Run(() => ChatSearch.Find(state.Archive, state.Query));
        // Старую работу заменяем после успешного чтения, проверки и построения результата.
        CommitArchive(state.Archive, state.Query, state.SelectedMessageId, matches);
        Status = Directory.Exists(state.Archive.RootPath) ? "Состояние восстановлено." : "Тексты восстановлены. Папка медиа отсутствует: нажмите «Папка медиа».";
    }
    private void CommitArchive(ChatArchive archive, ChatQuery query, long? selected, IReadOnlyList<ChatMessage> matches)
    {
        _session.Replace(archive, query, selected);
        Title = archive.Name;
        Summary = $"{archive.Messages.Count} сообщений · {archive.Participants.Count} авторов · {archive.SourceFormat}";
        Participants.Clear(); Participants.Add(new("", "Все участники", archive.Messages.Count));
        foreach (var p in archive.Participants) Participants.Add(p);
        Months.Clear(); foreach (var month in archive.Messages.Select(m => m.Date.ToString("yyyy-MM")).Distinct()) Months.Add(month);
        SelectedMonth = Months.FirstOrDefault();
        JumpText = archive.Messages.FirstOrDefault()?.Date.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
        SearchText = query.Text; FromText = query.From?.ToString("yyyy-MM-dd") ?? ""; ToText = query.To?.ToString("yyyy-MM-dd") ?? "";
        SelectedKind = Kinds.FirstOrDefault(k => k.Value == query.Kind) ?? Kinds[0];
        SelectedParticipant = Participants.FirstOrDefault(p => p.Id == (query.SenderId ?? "")) ?? Participants[0];
        _matches = matches; _page = 0; ShowPage(); SelectId(selected);
    }
    private ChatQuery BuildQuery() => new(SearchText, SelectedKind.Value, string.IsNullOrEmpty(SelectedParticipant?.Id) ? null : SelectedParticipant.Id,
        string.IsNullOrWhiteSpace(FromText) ? null : ParseDate(FromText, "Начало периода"),
        string.IsNullOrWhiteSpace(ToText) ? null : ParseDate(ToText, "Конец периода"));
    private Task ApplyCore() => ApplyCore(false);
    private async Task ApplyCore(bool preserveSelection)
    {
        var selectedId = preserveSelection ? SelectedMessage?.Id : null;
        var query = BuildQuery();
        var matches = await Task.Run(() => ChatSearch.Find(_session.Archive, query));
        _session.SetQuery(query); _matches = matches; _page = 0; ShowPage();
        if (preserveSelection) SelectId(selectedId);
        Status = $"Найдено: {matches.Count}. Поиск включает текст, автора, названия файлов, ссылки и опросы.";
    }
    private void ResetInputs() { SearchText = ""; FromText = ""; ToText = ""; SelectedKind = Kinds[0]; SelectedParticipant = Participants.FirstOrDefault(); }
    private void ShowPage()
    {
        // В ленту передаём только одну страницу, полный архив остаётся в Core.
        SelectedMessage = null;
        Messages.Clear(); foreach (var m in _matches.Skip(_page * PageSize).Take(PageSize)) Messages.Add(m);
        SelectedMessage = Messages.FirstOrDefault(); Raise(nameof(PageLabel));
    }
    private void SelectId(long? id)
    {
        if (id is null) return;
        var index = _matches.ToList().FindIndex(m => m.Id == id);
        if (index < 0) return;
        _page = index / PageSize; ShowPage(); SelectedMessage = Messages.FirstOrDefault(m => m.Id == id);
    }
    private async Task JumpCore(DateOnly day)
    {
        ResetInputs(); await ApplyCore();
        int index = ChatSearch.FindDate(_matches, day);
        if (index >= _matches.Count) { Status = "После этой даты сообщений нет."; return; }
        SelectId(_matches[index].Id); Status = "Первое сообщение не раньше " + day.ToString("dd.MM.yyyy");
    }
    private async Task UpdatePreview()
    {
        int version = ++_previewVersion;
        PreviewImage = null;
        var attachment = SelectedAttachment;
        if (attachment is null) { PreviewNote = "У этого сообщения нет вложений."; return; }
        try
        {
            var path = SafePaths.ResolveMedia(_session.Archive.RootPath, attachment.RelativePath);
            if (!File.Exists(path)) { PreviewNote = "Файл не включён в экспорт или перемещён. Текст переписки доступен."; return; }
            PreviewNote = $"{attachment.Name} · {new FileInfo(path).Length / 1024.0:F1} КиБ";
            if (attachment.Kind != MessageKind.Photo) { PreviewNote += "\nНажмите «Открыть вложение». Нужен системный просмотрщик, поддерживающий этот формат."; return; }
            if (new FileInfo(path).Length > 20 * 1024 * 1024) { PreviewNote += "\nИзображения свыше 20 МиБ: нажмите «Открыть вложение»."; return; }
            var bitmap = await Task.Run(() => { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 640); });
            // Пока фото читалось, пользователь мог выбрать другое вложение.
            if (version == _previewVersion) PreviewImage = bitmap; else bitmap.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { if (version == _previewVersion) PreviewNote = "Предпросмотр недоступен: " + e.Message; }
    }
    private static DateOnly ParseDate(string text, string label) => DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d : throw new ArgumentException(label + ": введите yyyy-MM-dd, например 2026-09-01.");
    private void ApplyTheme()
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = SelectedTheme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e) => Activity = $"{e.Reason} · в чате {e.MessageCount}";
    public void Dispose() { _session.Changed -= OnSessionChanged; ++_previewVersion; PreviewImage = null; }

    public const string HelpText = """
    Telegram Chat Navigator

    1. «Демо» открывает вымышленный чат. «Открыть экспорт» — папку одного чата Telegram Desktop с result.json или messages.html.
    2. Введите подстроку, выберите автора/категорию/период, нажмите «Найти» или Enter в поле поиска. Период: yyyy-MM-dd, обе границы включены.
    3. Выберите сообщение. Справа — полный текст, опрос, список вложений и ссылок. «Контекст» снимает фильтры, сохраняя выбранное сообщение; «К ответу» находит исходное сообщение.
    4. «К дате»/«К месяцу» сбрасывает фильтры и выбирает первое сообщение не раньше даты. Лента показывает по 50 сообщений: «Назад»/«Далее».
    5. Фото — внутри окна. «Открыть вложение»: macOS — QuickTime Player для MP4/WAV и других распространённых аудио/видеоформатов, приложение по умолчанию для документов, Windows — приложение по умолчанию, Linux — xdg-open в графическом сеансе. Нужен просмотрщик с поддержкой формата; при ошибке назначьте его в системе. Отсутствующее медиа повторно экспортируйте из Telegram.
    6. «Сохранить» записывает тексты, фильтры, выбор и путь к медиа в *.tgnav.json. «Загрузить» продолжает работу. Переносите рядом всю папку экспорта; при смене пути нажмите «Папка медиа». CSV сохраняет текущие найденные сообщения и не заменяет состояние.
    7. System/Light/Dark — системная/светлая/тёмная тема. Размер текста задаётся рядом. «Сохранить настройки» сохраняет их между запусками. При выходе чат автоматически не сохраняется: сначала нажмите «Сохранить».

    Клавиши: F1 — справка; Cmd+? — справка; Cmd+O — экспорт; Cmd+S — состояние; Cmd+L — загрузка состояния; Cmd+F — фокус поиска; Enter — поиск; стрелки — выбор в ленте; Tab/Shift+Tab — переход по полям.
    На Mac верхний ряд может управлять яркостью: Fn+F1 отправляет F1. Кнопка «Справка» и Cmd+? доступны без изменения настроек клавиатуры.

    GUI: dotnet run --project src/TelegramChatNavigator.Desktop
         ... -- --open examples/demo
         ... -- --state /путь/chat.tgnav.json
    CLI: dotnet run --project src/TelegramChatNavigator.Cli -- --help
         ... -- inspect examples/demo
         ... -- participants examples/demo
         ... -- search examples/demo --text маршрут --kind Link
         ... -- search examples/demo --from 2026-09-01 --to 2026-09-30
         ... -- save-state examples/demo --out /tmp/chat.tgnav.json
         ... -- export /tmp/chat.tgnav.json --out /tmp/messages.csv
    Категории CLI: All, Text, Photo, Video, File, Audio, Link, Poll, Service.
    Дополнительно: --sender ID, --limit 1..10000. Остановить CLI: Ctrl+C; окно: Cmd+Q или закрытие окна.
    """;
}
