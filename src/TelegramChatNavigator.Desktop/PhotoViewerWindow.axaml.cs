using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.Windows.Input;
using TelegramChatNavigator.Core;
using TelegramChatNavigator.Desktop.ViewModels;

namespace TelegramChatNavigator.Desktop;

public sealed partial class PhotoViewerWindow : Window
{
    public PhotoViewerWindow() => AvaloniaXamlLoader.Load(this);
    public PhotoViewerWindow(ChatArchive archive, long messageId, Attachment attachment) : this()
    {
        var vm = new PhotoViewerViewModel(archive, messageId, attachment);
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadAsync();
        Closed += (_, _) => vm.Dispose();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            ICommand? command = e.Key switch
            {
                Key.Left => vm.PreviousCommand, Key.Right => vm.NextCommand,
                Key.Add or Key.OemPlus => vm.ZoomInCommand,
                Key.Subtract or Key.OemMinus => vm.ZoomOutCommand,
                Key.D0 or Key.NumPad0 => vm.FitCommand, _ => null
            };
            if (command is not null) { command.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }
}
