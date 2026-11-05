using System.Diagnostics;
using TelegramChatNavigator.Desktop;

namespace TelegramChatNavigator.Tests;

public sealed class AttachmentOpenerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tg-open-tests-" + Guid.NewGuid().ToString("N"));
    public AttachmentOpenerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string FilePath(string name)
    {
        var path = Path.Combine(_root, name); File.WriteAllText(path, "test"); return path;
    }

    [Fact] public void MacKeepsSpecialCharactersInOnePathArgument()
    {
        var path = FilePath("recording with spaces & quotes '.mp4");
        var start = SystemAttachmentOpener.CreateStartInfo(path, AttachmentPlatform.MacOS);
        Assert.Equal("/usr/bin/open", start.FileName); Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "-a", "QuickTime Player", path }, start.ArgumentList);
        Assert.Empty(start.Arguments);
    }
    [Fact] public void LinuxUsesAbsolutePathAsOneArgument()
    {
        var path = FilePath("-sample & notes.WAV");
        var start = SystemAttachmentOpener.CreateStartInfo(path, AttachmentPlatform.Linux);
        Assert.Equal("xdg-open", start.FileName); Assert.False(start.UseShellExecute);
        Assert.Equal(path, Assert.Single(start.ArgumentList)); Assert.True(Path.IsPathFullyQualified(start.ArgumentList[0]));
    }
    [Fact] public void MacDocumentsUseDefaultAppWithoutQuickLook()
    {
        var path = FilePath("notes.txt");
        var start = SystemAttachmentOpener.CreateStartInfo(path, AttachmentPlatform.MacOS);
        Assert.Equal("/usr/bin/open", start.FileName); Assert.Equal(path, Assert.Single(start.ArgumentList));
    }
    [Fact] public void WindowsOpensDocumentAssociationWithoutCommandInterpreter()
    {
        var path = FilePath("report & notes.pdf");
        var start = SystemAttachmentOpener.CreateStartInfo(path, AttachmentPlatform.Windows);
        Assert.Equal(path, start.FileName); Assert.True(start.UseShellExecute); Assert.Equal("open", start.Verb);
        Assert.Empty(start.Arguments); Assert.Empty(start.ArgumentList);
    }
    [Theory]
    [InlineData("tool.exe")]
    [InlineData("tool.bat")]
    [InlineData("photo.jpg.cmd")]
    [InlineData("script.ps1")]
    [InlineData("script.sh")]
    [InlineData("unknown.bin")]
    public void NonDocumentAttachmentsAreNotExecuted(string name) =>
        Assert.Throws<IOException>(() => SystemAttachmentOpener.CreateStartInfo(FilePath(name), AttachmentPlatform.Windows));
    [Fact] public async Task MissingAttachmentFailsBeforeOpeningSystemApp() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => SystemAttachmentOpener.OpenAsync(Path.Combine(_root, "missing.mp4")));
    [Fact] public void UnsupportedSystemHasReadableError() =>
        Assert.Contains("macOS, Windows и Linux", Assert.Throws<IOException>(() => SystemAttachmentOpener.CreateStartInfo(FilePath("a.wav"), AttachmentPlatform.Other)).Message);
    [Theory]
    [InlineData("Windows", "приложением по умолчанию")]
    [InlineData("Linux", "xdg-utils")]
    [InlineData("MacOS", "средствами macOS")]
    public async Task MissingDesktopToolHasInstructions(string platformName, string expected)
    {
        var platform = Enum.Parse<AttachmentPlatform>(platformName);
        var error = await Assert.ThrowsAsync<IOException>(() => SystemAttachmentOpener.StartAsync(new ProcessStartInfo(Path.Combine(_root, "absent-tool")) { UseShellExecute = false }, platform));
        Assert.Contains(expected, error.Message);
    }
    [Fact] public async Task FailedDesktopLaunchReturnsErrorInsteadOfSuccess()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/false") { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 4");
        }
        var error = await Assert.ThrowsAsync<IOException>(() => SystemAttachmentOpener.StartAsync(start, AttachmentPlatform.Linux));
        Assert.Contains("приложения по умолчанию", error.Message);
    }
}
