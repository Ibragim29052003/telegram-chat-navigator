using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TelegramChatNavigator.Core;

namespace TelegramChatNavigator.Desktop;

public sealed class DesktopServices(Window owner) : IDesktopServices
{
    public string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TelegramChatNavigator", "preferences.json");
    public async Task<string?> PickExportFolderAsync() => (await owner.StorageProvider.OpenFolderPickerAsync(new() { Title = "Папка экспорта Telegram Desktop", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> PickStateAsync() => (await owner.StorageProvider.OpenFilePickerAsync(new()
    {
        Title = "Открыть состояние навигатора", AllowMultiple = false,
        FileTypeFilter = [new("Состояние навигатора") { Patterns = ["*.tgnav.json"] }]
    })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> PickSaveAsync(bool csv)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = csv ? "Экспорт результатов поиска в CSV" : "Сохранить состояние навигатора",
            SuggestedFileName = csv ? "messages.csv" : "chat.tgnav.json",
            DefaultExtension = csv ? "csv" : "tgnav.json",
            FileTypeChoices = [new(csv ? "CSV" : "Состояние навигатора") { Patterns = [csv ? "*.csv" : "*.tgnav.json"] }]
        });
        return file?.TryGetLocalPath();
    }
    public async Task ShowHelpAsync(string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(18) };
        var dialog = new Window { Title = "Справка · Telegram Chat Navigator", Width = 720, Height = 620, MinWidth = 450, MinHeight = 350, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        await dialog.ShowDialog(owner);
    }
    public Task OpenAttachmentAsync(string path) => SystemAttachmentOpener.OpenAsync(path);
    public Task OpenLinkAsync(string url)
    {
        if (!SafePaths.IsWebLink(url)) throw new IOException("Разрешены только HTTP/HTTPS-ссылки.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
