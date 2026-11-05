namespace TelegramChatNavigator.Desktop;

public interface IDesktopServices
{
    Task<string?> PickExportFolderAsync();
    Task<string?> PickStateAsync();
    Task<string?> PickSaveAsync(bool csv);
    Task ShowHelpAsync(string text);
    Task OpenAttachmentAsync(string path);
    Task OpenLinkAsync(string url);
    string PreferencesPath { get; }
}
