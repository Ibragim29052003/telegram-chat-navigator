using Avalonia.Media.Imaging;
using System.Windows.Input;
using TelegramChatNavigator.Core;

namespace TelegramChatNavigator.Desktop.ViewModels;

public sealed record PhotoItem(long MessageId, string Sender, string DateLabel, Attachment Attachment);

public sealed class PhotoViewerViewModel : ObservableObject, IDisposable
{
    private readonly string _root;
    private readonly PhotoItem[] _photos;
    private int _index, _version;
    private Bitmap? _image;
    private string _note = "";
    private bool _fit = true, _loading;
    private double _zoom = 1;

    public PhotoViewerViewModel(ChatArchive archive, long messageId, Attachment attachment)
    {
        _root = archive.RootPath;
        _photos = archive.Messages.SelectMany(m => m.Attachments
            .Where(a => a.Kind == MessageKind.Photo).Select(a => new PhotoItem(m.Id, m.Sender, m.DateLabel, a))).ToArray();
        _index = Array.FindIndex(_photos, p => p.MessageId == messageId && p.Attachment == attachment);
        if (_index < 0) throw new ArgumentException("Фотография отсутствует в чате.");
        PreviousCommand = new AsyncCommand(() => MoveAsync(-1));
        NextCommand = new AsyncCommand(() => MoveAsync(1));
        ZoomInCommand = new AsyncCommand(() => { SetZoom(_zoom + .25); return Task.CompletedTask; });
        ZoomOutCommand = new AsyncCommand(() => { SetZoom(_zoom - .25); return Task.CompletedTask; });
        FitCommand = new AsyncCommand(() => { Fit = true; return Task.CompletedTask; });
    }

    public string Heading => $"{_index + 1} / {_photos.Length} · {_photos[_index].Attachment.Name}";
    public string Caption => $"#{_photos[_index].MessageId} · {_photos[_index].Sender} · {_photos[_index].DateLabel}";
    public string Note { get => _note; private set => Set(ref _note, value); }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public bool CanPrevious => _index > 0;
    public bool CanNext => _index + 1 < _photos.Length;
    public bool Fit { get => _fit; private set { Set(ref _fit, value); Raise(nameof(ZoomLabel)); } }
    public string ZoomLabel => Fit ? "Вписать в окно" : $"{_zoom:P0}";
    public double ImageWidth => (_image?.PixelSize.Width ?? 0) * _zoom;
    public double ImageHeight => (_image?.PixelSize.Height ?? 0) * _zoom;
    public Bitmap? Image
    {
        get => _image;
        private set { var old = _image; Set(ref _image, value); old?.Dispose(); Raise(nameof(ImageWidth)); Raise(nameof(ImageHeight)); }
    }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand FitCommand { get; }

    public async Task LoadAsync()
    {
        int version = ++_version;
        Image = null; IsLoading = true; Note = "Загрузка…"; Fit = true; _zoom = 1;
        try
        {
            var path = SafePaths.ResolveMedia(_root, _photos[_index].Attachment.RelativePath);
            if (!File.Exists(path)) throw new IOException("Файл не включён в экспорт или перемещён.");
            if (new FileInfo(path).Length > 20 * 1024 * 1024)
                throw new IOException("Фотография больше 20 МиБ. Для просмотра откройте её из папки экспорта.");
            // Ограничиваем декодирование: в памяти находится только один снимок.
            var image = await Task.Run(() => { using var stream = File.OpenRead(path); return Bitmap.DecodeToWidth(stream, 1920); });
            if (version != _version) { image.Dispose(); return; }
            Image = image; Note = "← / → — фотографии · + / − — масштаб · 0 — вписать · Esc — закрыть";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { if (version == _version) Note = "Просмотр недоступен: " + e.Message; }
        finally { if (version == _version) IsLoading = false; }
    }
    private Task MoveAsync(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _photos.Length) return Task.CompletedTask;
        _index = next;
        Raise(nameof(Heading)); Raise(nameof(Caption)); Raise(nameof(CanPrevious)); Raise(nameof(CanNext));
        return LoadAsync();
    }
    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, .25, 3); Fit = false;
        Raise(nameof(ImageWidth)); Raise(nameof(ImageHeight)); Raise(nameof(ZoomLabel));
    }
    public void Dispose() { ++_version; Image = null; }
}
