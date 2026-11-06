using TelegramChatNavigator.Core;

namespace TelegramChatNavigator.Desktop;

public interface IDesktopServices
{
    Task<string?> PickExportFolderAsync();
    Task<string?> PickStateAsync();
    Task<string?> PickSaveAsync(bool csv);
    Task ShowHelpAsync(string text);
    Task OpenAttachmentAsync(string path, MessageKind kind = MessageKind.File);
    Task ShowPhotoAsync(ChatArchive archive, long messageId, Attachment attachment);
    Task OpenLinkAsync(string url);
    string PreferencesPath { get; }
}
