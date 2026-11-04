using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using TelegramChatNavigator.Desktop.ViewModels;

namespace TelegramChatNavigator.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(true) { }
    public MainWindow(bool initializeOnOpen)
    {
        AvaloniaXamlLoader.Load(this);
        var vm = new MainViewModel(new DesktopServices(this));
        DataContext = vm;
        if (initializeOnOpen) Opened += async (_, _) => await ((MainViewModel)DataContext!).InitializeAsync(Program.LaunchArguments);
        Closed += (_, _) => ((MainViewModel?)DataContext)?.Dispose();
        KeyDown += OnKeyDown;
        this.FindControl<ListBox>("MessageList")!.SelectionChanged += (_, _) =>
        {
            var list = this.FindControl<ListBox>("MessageList")!;
            if (list.SelectedItem is { } item) list.ScrollIntoView(item);
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        bool command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        System.Windows.Input.ICommand? action = null;
        if (e.Key == Key.F1 || command && e.Key == Key.OemQuestion) action = vm.HelpCommand;
        else if (command && e.Key == Key.O) action = vm.OpenCommand;
        else if (command && e.Key == Key.S) action = vm.SaveCommand;
        else if (command && e.Key == Key.L) action = vm.LoadCommand;
        else if (command && e.Key == Key.F) { this.FindControl<TextBox>("SearchBox")!.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && this.FindControl<TextBox>("SearchBox")!.IsFocused) action = vm.ApplyCommand;
        if (action is not null) { action.Execute(null); e.Handled = true; }
    }
}
