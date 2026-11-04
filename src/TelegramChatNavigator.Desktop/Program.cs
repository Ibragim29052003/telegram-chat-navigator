using Avalonia;

namespace TelegramChatNavigator.Desktop;

internal static class Program
{
    public static string[] LaunchArguments { get; private set; } = [];
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--help")) { Console.WriteLine(ViewModels.MainViewModel.HelpText); return; }
        LaunchArguments = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
