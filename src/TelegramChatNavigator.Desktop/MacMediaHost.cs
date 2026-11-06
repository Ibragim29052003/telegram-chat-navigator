using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TelegramChatNavigator.Desktop;

// AVKit создаёт видео и штатные кнопки Play/пауза/перемотка. Мы только встраиваем NSView.
// Этот адаптер вызывается исключительно на macOS и в UI-потоке.
internal sealed class MacMediaHost : NativeControlHost
{
    private nint _view, _player;
    private DispatcherTimer? _timer;
    private int _ticks;
    private bool _ready;
    public event Action<string>? StatusChanged;
    public event Action<double, double, bool>? PlaybackChanged;

    public MacMediaHost(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!File.Exists(path)) throw new FileNotFoundException("Медиафайл отсутствует.");
        nint text = 0;
        try
        {
            NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            NativeLibrary.Load("/System/Library/Frameworks/AVKit.framework/AVKit");
            text = SendText(Send(GetClass("NSString"), Selector("alloc")), Selector("initWithUTF8String:"), path);
            var url = Send(GetClass("NSURL"), Selector("fileURLWithPath:"), text);
            _player = Send(Send(GetClass("AVPlayer"), Selector("alloc")), Selector("initWithURL:"), url);
            _view = Send(Send(GetClass("AVPlayerView"), Selector("alloc")), Selector("init"));
            if (_player == 0 || _view == 0) throw new IOException("Не удалось создать системный проигрыватель AVKit.");
            Send(_view, Selector("setPlayer:"), _player);
            // Inline = 1: панель управления видна внутри нашего окна, без QuickTime.
            Send(_view, Selector("setControlsStyle:"), 1);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => CheckStatus());
        }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            ReleasePlayer(); if (_view != 0) { Send(_view, Selector("release")); _view = 0; }
            throw new IOException("Не удалось подготовить системный проигрыватель AVKit.", e);
        }
        finally { Send(text, Selector("release")); }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _timer?.Start();
        return new PlatformHandle(_view, "NSView");
    }

    private void CheckStatus()
    {
        var item = Send(_player, Selector("currentItem"));
        var status = Send(item, Selector("status"));
        if (status == 1)
        {
            if (!_ready) { _ready = true; StatusChanged?.Invoke("Готово. Нажмите ▶. Шкала позволяет выбрать время."); }
            PlaybackChanged?.Invoke(SendTime(_player, Selector("currentTime")).Seconds,
                SendTime(item, Selector("duration")).Seconds, SendFloat(_player, Selector("rate")) > 0);
        }
        else if (status == 2 || ++_ticks >= 75)
        {
            _timer?.Stop();
            StatusChanged?.Invoke("Система не смогла подготовить файл. Возможно, AVKit не поддерживает его кодек. Можно открыть файл во внешнем приложении.");
        }
    }
    public void TogglePlayback()
    {
        if (!_ready) return;
        if (SendFloat(_player, Selector("rate")) > 0) StopPlayback();
        else
        {
            var item = Send(_player, Selector("currentItem"));
            var duration = SendTime(item, Selector("duration")).Seconds;
            if (duration > 0 && SendTime(_player, Selector("currentTime")).Seconds >= duration - .05) Seek(0);
            Send(_player, Selector("play"));
        }
        _timer?.Start();
        CheckStatus();
    }
    public void Seek(double seconds)
    {
        if (_ready && double.IsFinite(seconds) && seconds >= 0)
        {
            // Нулевые допуски дают точный переход, а не прыжок к ближайшему ключевому кадру.
            var zero = new NativeTime(0, 600, 1, 0);
            SendSeek(_player, Selector("seekToTime:toleranceBefore:toleranceAfter:"),
                new NativeTime((long)(seconds * 600), 600, 1, 0), zero, zero);
        }
    }
    private void StopPlayback() { if (_player != 0) Send(_player, Selector("pause")); }
    public void Stop()
    {
        _timer?.Stop();
        StopPlayback();
    }
    private void ReleasePlayer()
    {
        Stop();
        if (_view != 0) Send(_view, Selector("setPlayer:"), 0);
        if (_player != 0) { Send(_player, Selector("release")); _player = 0; }
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Закрытие окна прекращает звук и освобождает оба нативных объекта.
        ReleasePlayer();
        if (_view != 0) { Send(_view, Selector("release")); _view = 0; }
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint target, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendText(nint target, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern float SendFloat(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeTime SendTime(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSeek(nint target, nint selector, NativeTime time, NativeTime before, NativeTime after);

    // CMTime — системная дробь value/timescale. Флаги отделяют обычное время от неопределённого.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeTime(long value, int timescale, uint flags, long epoch)
    {
        public readonly long Value = value;
        public readonly int Timescale = timescale;
        public readonly uint Flags = flags;
        public readonly long Epoch = epoch;
        public double Seconds => Timescale > 0 && (Flags & 29) == 1 ? Math.Max(0, (double)Value / Timescale) : 0;
    }
}
