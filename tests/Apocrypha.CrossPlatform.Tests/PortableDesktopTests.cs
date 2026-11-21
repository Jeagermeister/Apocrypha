using Apocrypha.Backend.OS;
using Apocrypha.Sdk;
using ApplicationIdentity = Apocrypha.Sdk.ApplicationIdentity;
using System.Diagnostics;
using Xunit;

namespace Apocrypha.CrossPlatform.Tests;

public sealed class PortableDesktopTests
{
    private const string Template = "[Desktop Entry]\nType=Application\nName=Apocrypha\nIcon=custom-icon\nMimeType=x-scheme-handler/nxm;x-scheme-handler/ror2mm;\nComment=Keep me\nExec=old %u\nTryExec=old\n[Desktop Action Other]\nExec=untouched\n";
    [Theory]
    [InlineData("Apocrypha.AppImage")]
    [InlineData("moved folder/renamed AppImage")]
    [InlineData("quote'$`%image.AppImage")]
    public async Task RelocationRepairsOneEntryPreservesMetadataAndIsIdempotent(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "apocrypha-desktop-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var capture = Path.Combine(root, "received.txt");
        await File.WriteAllTextAsync(exe, "#!/bin/sh\nprintf '%s' \"$1\" > '" + capture + "'\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var desktop = Path.Combine(root, ApplicationIdentity.AppId + ".desktop");
        await File.WriteAllTextAsync(desktop, Template);
        Assert.True(await PortableDesktopRegistration.Repair(root, exe, Template, default));
        var timestamp = File.GetLastWriteTimeUtc(desktop);
        Assert.False(await PortableDesktopRegistration.Repair(root, exe, Template, default));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(desktop));
        var text = await File.ReadAllTextAsync(desktop);
        Assert.Contains("Icon=custom-icon", text); Assert.Contains("Comment=Keep me", text);
        Assert.Contains("MimeType=x-scheme-handler/nxm;x-scheme-handler/ror2mm;", text);
        Assert.Contains("[Desktop Action Other]\nExec=untouched", text);
        Assert.Single(Directory.GetFiles(root, "*.desktop"));
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, ApplicationIdentity.AppId + ".sh"))
            { ArgumentList = { "nxm://oauth/callback?state=test&code=fixture" }, UseShellExecute = false })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        Assert.Equal("nxm://oauth/callback?state=test&code=fixture", await File.ReadAllTextAsync(capture));
        // Second copy is the canonical target on a subsequent normal startup, not a second entry.
        var copy = Path.Combine(root, "another-copy"); File.Copy(exe, copy);
        Assert.True(await PortableDesktopRegistration.Repair(root, copy, Template, default));
        Assert.Single(Directory.GetFiles(root, "*.desktop"));
    }

    [Fact]
    public async Task MissingEntryIsCreatedButMalformedAndForeignEntriesArePreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "apocrypha-entry-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "image"); await File.WriteAllTextAsync(exe, "fixture");
        Assert.True(await PortableDesktopRegistration.Repair(root, exe, Template, default));
        var desktop = Path.Combine(root, ApplicationIdentity.AppId + ".desktop");
        foreach (var malformed in new[] { "invalid", "[Desktop Entry]\nType=Application\nName=AnotherApp\n", "[Desktop Entry]\nType=Application\nName=Apocrypha\nHidden=true\n" })
        {
            await File.WriteAllTextAsync(desktop, malformed);
            await Assert.ThrowsAsync<IOException>(() => PortableDesktopRegistration.Repair(root, exe, Template, default));
            Assert.Equal(malformed, await File.ReadAllTextAsync(desktop));
        }
    }

    [Fact]
    public async Task ReadOnlyAndLinkedEntriesAreNotReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "apocrypha-readonly-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "image"); await File.WriteAllTextAsync(exe,"fixture");
        var desktop = Path.Combine(root,ApplicationIdentity.AppId+".desktop"); await File.WriteAllTextAsync(desktop,Template);
        File.SetUnixFileMode(desktop,UnixFileMode.UserRead);
        await Assert.ThrowsAsync<IOException>(() => PortableDesktopRegistration.Repair(root,exe,Template,default));
        Assert.Equal(Template, await File.ReadAllTextAsync(desktop));
        File.SetUnixFileMode(desktop,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Delete(desktop);
        var target=Path.Combine(root,"external-entry");await File.WriteAllTextAsync(target,Template);
        File.CreateSymbolicLink(desktop,target);
        await Assert.ThrowsAsync<IOException>(() => PortableDesktopRegistration.Repair(root,exe,Template,default));
        Assert.Equal(Template, await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("other.desktop", false)]
    [InlineData("io.github.jeagermeister.apocrypha.desktop", false)]
    public void OnlyUnownedDefaultMayBeRegistered(string current, bool expected) => Assert.Equal(expected, PortableDesktopRegistration.ShouldSetDefault(current));
}
