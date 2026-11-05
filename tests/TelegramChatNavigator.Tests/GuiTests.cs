using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Text.Json;
using TelegramChatNavigator.Core;
using TelegramChatNavigator.Desktop;
using TelegramChatNavigator.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(TelegramChatNavigator.Tests.TestAppBuilder))]

namespace TelegramChatNavigator.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class FakeServices : IDesktopServices, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tg-ui-" + Guid.NewGuid().ToString("N"));
    public FakeServices() => Directory.CreateDirectory(Root);
    public string PreferencesPath => Path.Combine(Root, "prefs.json");
    public string? ExportFolder { get; set; }
    public string? StatePath { get; set; }
    public string? SavePath { get; set; }
    public int HelpCalls { get; private set; }
    public string? OpenedAttachmentPath { get; private set; }
    public Exception? AttachmentError { get; set; }
    public string? OpenedLink { get; private set; }
    public Task<string?> PickExportFolderAsync() => Task.FromResult(ExportFolder);
    public Task<string?> PickStateAsync() => Task.FromResult(StatePath);
    public Task<string?> PickSaveAsync(bool csv) => Task.FromResult(SavePath);
    public Task ShowHelpAsync(string text) { HelpCalls++; return Task.CompletedTask; }
    public Task OpenAttachmentAsync(string path) { OpenedAttachmentPath = path; return AttachmentError is null ? Task.CompletedTask : Task.FromException(AttachmentError); }
    public Task OpenLinkAsync(string url) { OpenedLink = url; return Task.CompletedTask; }
    public void Dispose() => Directory.Delete(Root, true);
}

public sealed class GuiTests
{
    private static string Demo => Path.Combine(AppContext.BaseDirectory, "examples", "demo");
    private static async Task WaitReady(MainViewModel vm)
    {
        for (int i = 0; i < 500 && vm.IsBusy; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.False(vm.IsBusy);
    }
    private static void Capture(MainWindow window, string name)
    {
        var root = Environment.GetEnvironmentVariable("TG_TEST_ARTIFACTS");
        if (root is null) return;
        Directory.CreateDirectory(root); Dispatcher.UIThread.RunJobs();
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap); bitmap.Save(Path.Combine(root, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact] public async Task RealBindingsSearchSelectionAndHelpWork()
    {
        using var services = new FakeServices(); using var vm = new MainViewModel(services);
        var window = new MainWindow(false) { DataContext = vm }; window.Show();
        try
        {
            await vm.OpenPathAsync(Demo); Dispatcher.UIThread.RunJobs();
            var list = window.FindControl<ListBox>("MessageList")!;
            Assert.Equal(14, list.ItemCount); Assert.NotNull(list.SelectedItem);
            var input = window.FindControl<TextBox>("SearchBox")!; input.Text = "маршрут";
            Dispatcher.UIThread.RunJobs();
            var apply = window.FindControl<Button>("ApplyButton")!;
            var point = apply.TranslatePoint(new Point(apply.Bounds.Width / 2, apply.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await WaitReady(vm); Assert.Equal("маршрут", vm.SearchText); Assert.Equal(4, list.ItemCount);
            input.Focus(); window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null); Assert.Equal(1, services.HelpCalls);
            vm.SearchText = ""; await vm.ApplyAsync();
            list.SelectedItem = vm.Messages.Single(m => m.Id == 3); Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.SelectedMessage!.Id); Assert.Equal("lake.png", vm.SelectedAttachment!.Name);
            for (int i = 0; i < 200 && vm.PreviewImage is null; i++) await Task.Delay(10);
            Assert.NotNull(vm.PreviewImage);
            Assert.Contains("Эскиз", window.FindControl<SelectableTextBlock>("FullMessageText")!.Text);
            vm.SelectedTheme = "Light"; Capture(window, "desktop-light");
            window.Width = 1060; window.Height = 680; Dispatcher.UIThread.RunJobs(); Capture(window, "desktop-minimum");
            vm.SelectedTheme = "Dark"; Capture(window, "desktop-dark");
            Assert.True(input.Bounds.Width > 100); Assert.True(list.Bounds.Width > 250);
            input.Focus();
            window.KeyPress(Key.Q, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.Q, "q");
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task SaveLoadExportAndCanceledDialogsPreserveWork()
    {
        using var services = new FakeServices(); using var vm = new MainViewModel(services);
        await vm.OpenPathAsync(Demo);
        vm.SearchText = "маршрут"; vm.SelectedKind = vm.Kinds.Single(k => k.Value == MessageKind.Link); await vm.ApplyAsync();
        Assert.Equal(2, vm.Messages.Count);
        vm.SelectedMessage = vm.Messages.Single(m => m.Id == 12);
        var selected = vm.SelectedMessage!.Id;
        vm.OpenCommand.Execute(null); await WaitReady(vm); Assert.Equal(2, vm.Messages.Count);
        vm.LoadCommand.Execute(null); await WaitReady(vm); Assert.Equal(selected, vm.SelectedMessage!.Id);
        vm.SaveCommand.Execute(null); await WaitReady(vm); Assert.Empty(Directory.GetFiles(services.Root));
        services.SavePath = Path.Combine(services.Root, "chat.tgnav.json"); vm.SaveCommand.Execute(null); await WaitReady(vm);
        var statePath = services.SavePath; Assert.True(File.Exists(statePath));
        vm.SearchText = ""; vm.SelectedKind = vm.Kinds[0]; await vm.ApplyAsync(); Assert.Equal(14, vm.Messages.Count);
        await vm.OpenStatePathAsync(statePath); Assert.Equal(2, vm.Messages.Count); Assert.Equal(selected, vm.SelectedMessage!.Id);
        Assert.Equal("маршрут", vm.SearchText);
        services.SavePath = Path.Combine(services.Root, "out.csv"); vm.ExportCommand.Execute(null); await WaitReady(vm);
        Assert.Contains("https://www.openstreetmap.org", await File.ReadAllTextAsync(services.SavePath));
        services.StatePath = Path.Combine(services.Root, "broken.tgnav.json"); await File.WriteAllTextAsync(services.StatePath, "{");
        vm.LoadCommand.Execute(null); await WaitReady(vm); Assert.StartsWith("Ошибка:", vm.Status); Assert.Equal(2, vm.Messages.Count);
        await vm.OpenPathAsync(Path.Combine(services.Root, "absent")); Assert.StartsWith("Ошибка:", vm.Status); Assert.Equal(2, vm.Messages.Count);
    }

    [AvaloniaFact] public async Task NavigationReplyMissingMediaAndInvalidDates()
    {
        using var services = new FakeServices(); using var vm = new MainViewModel(services);
        await vm.OpenPathAsync(Demo);
        vm.JumpText = "2026-10-01"; vm.JumpCommand.Execute(null); await WaitReady(vm); Assert.Equal(11, vm.SelectedMessage!.Id);
        vm.SelectedMessage = vm.Messages.Single(m => m.Id == 10);
        vm.ReplyCommand.Execute(null); await WaitReady(vm); Assert.Equal(6, vm.SelectedMessage!.Id);
        vm.SelectedMessage = vm.Messages.Single(m => m.Id == 9); Assert.Contains("не включён", vm.PreviewNote);
        var before = vm.Messages.Select(m => m.Id).ToArray(); vm.FromText = "не дата"; await vm.ApplyAsync();
        Assert.StartsWith("Ошибка:", vm.Status); Assert.Equal(before, vm.Messages.Select(m => m.Id));
        vm.FromText = ""; vm.SearchText = "видеотест"; await vm.ApplyAsync();
        vm.OpenAttachmentCommand.Execute(null); await WaitReady(vm); Assert.EndsWith("demo.mp4", services.OpenedAttachmentPath);
        services.AttachmentError = new IOException("Назначьте приложение по умолчанию для видео.");
        vm.OpenAttachmentCommand.Execute(null); await WaitReady(vm);
        Assert.Contains("Назначьте приложение", vm.Status); Assert.Equal(8, vm.SelectedMessage!.Id); Assert.True(vm.IsReady);
        vm.SearchText = "рекомендации"; await vm.ApplyAsync(); vm.LinkCommand.Execute(null); await WaitReady(vm);
        Assert.Equal("https://www.mchs.gov.ru/", services.OpenedLink);
    }
    [AvaloniaFact] public async Task PagingBoundariesAndRestoredSelectionChooseCorrectPage()
    {
        using var services = new FakeServices(); using var vm = new MainViewModel(services);
        var messages = Enumerable.Range(1, 101).Select(i => new
        {
            id = i, type = "message", date = new DateTime(2026, 9, 1).AddMinutes(i).ToString("s"),
            from = "Алина", from_id = "user100", text = "Сообщение " + i
        });
        await File.WriteAllTextAsync(Path.Combine(services.Root, "result.json"), JsonSerializer.Serialize(new { name = "Большой чат", messages }));
        await vm.OpenPathAsync(services.Root); Assert.Equal(50, vm.Messages.Count); Assert.Equal(1, vm.Messages[0].Id);
        vm.NextPageCommand.Execute(null); Assert.Equal(51, vm.Messages[0].Id); Assert.Equal(100, vm.Messages[^1].Id);
        vm.NextPageCommand.Execute(null); Assert.Single(vm.Messages); Assert.Equal(101, vm.SelectedMessage!.Id);
        vm.NextPageCommand.Execute(null); Assert.Equal(101, vm.SelectedMessage!.Id);
        services.SavePath = Path.Combine(services.Root, "page.tgnav.json"); vm.SaveCommand.Execute(null); await WaitReady(vm);
        await vm.OpenStatePathAsync(services.SavePath); Assert.Single(vm.Messages); Assert.Equal(101, vm.SelectedMessage!.Id);
        vm.PreviousPageCommand.Execute(null); Assert.Equal(51, vm.SelectedMessage!.Id);
    }
}
