using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TelegramChatNavigator.Desktop;

internal sealed class MediaViewerWindow : Window
{
    public MediaViewerWindow(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Title = "Проигрыватель · " + Path.GetFileName(path);
        Width = 900; Height = 640; MinWidth = 550; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var host = new MacMediaHost(path) { MinHeight = 180 };
        var note = new TextBlock { Text = "Подготовка файла системным проигрывателем…", TextWrapping = TextWrapping.Wrap };
        host.StatusChanged += text => note.Text = text;
        var play = new Button { Content = "▶ Воспроизвести", Name = "MediaPlayButton", IsEnabled = false };
        var time = new TextBlock { Text = "00:00 / 00:00", Name = "MediaTime", VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 1, Name = "MediaTimeline", IsEnabled = false };
        bool updating = false;
        play.Click += (_, _) => host.TogglePlayback();
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !updating) host.Seek(slider.Value);
        };
        host.PlaybackChanged += (position, duration, playing) =>
        {
            updating = true;
            slider.Maximum = Math.Max(1, duration); slider.Value = position; slider.IsEnabled = duration > 0;
            updating = false;
            play.IsEnabled = true; play.Content = playing ? "Ⅱ Пауза" : "▶ Воспроизвести";
            static string Stamp(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
            time.Text = $"{Stamp(position)} / {Stamp(duration)}";
        };
        var controls = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12 };
        controls.Children.Add(play); Grid.SetColumn(slider, 1); controls.Children.Add(slider); Grid.SetColumn(time, 2); controls.Children.Add(time);
        var external = new Button { Content = "Открыть внешним приложением", HorizontalAlignment = HorizontalAlignment.Left };
        external.Click += async (_, _) =>
        {
            host.Stop();
            try { await SystemAttachmentOpener.OpenAsync(path); }
            catch (IOException e) { note.Text = e.Message; }
        };
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        grid.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetRow(host, 1); grid.Children.Add(host);
        Grid.SetRow(controls, 2); grid.Children.Add(controls);
        Grid.SetRow(note, 3); grid.Children.Add(note);
        Grid.SetRow(external, 4); grid.Children.Add(external);
        Content = grid;
        Closed += (_, _) => host.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}
