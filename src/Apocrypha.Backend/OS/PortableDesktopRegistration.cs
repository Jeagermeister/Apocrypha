using System.Text;
using Apocrypha.Sdk;

namespace Apocrypha.Backend.OS;

/// <summary>Repairs only our canonical user entry. No alternate development entries or default takeover.</summary>
internal static class PortableDesktopRegistration
{
    private const string Id = ApplicationIdentity.AppId;
    internal static bool ShouldSetDefault(string current) => string.IsNullOrWhiteSpace(current);

    internal static async Task<bool> Repair(string applications, string executable, string template, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new IOException("The portable executable is unavailable.");
        var desktop = Path.Combine(applications, Id + ".desktop");
        var wrapper = Path.Combine(applications, Id + ".sh");
        RejectLink(desktop);
        RejectLink(wrapper);
        if (File.Exists(desktop) && !OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(desktop) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            throw new IOException("Desktop entry is read-only; registration was left unchanged.");
        var text = File.Exists(desktop) ? await File.ReadAllTextAsync(desktop, token) : template;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var section = lines.FindIndex(line => line == "[Desktop Entry]");
        if (section < 0) throw new IOException("Malformed desktop entry; registration was left unchanged.");
        var end = lines.FindIndex(section + 1, line => line.StartsWith('['));
        if (end < 0) end = lines.Count;
        var fields = lines.Skip(section + 1).Take(end - section - 1).ToArray();
        if (!fields.Contains("Name=Apocrypha") || !fields.Contains("Type=Application") || fields.Contains("Hidden=true") || fields.Any(field => field.StartsWith("X-Flatpak=") || field.StartsWith("X-SnapInstanceName=")))
            throw new IOException("Desktop entry is externally managed; registration was left unchanged.");
        var mimeTypes = (fields.FirstOrDefault(field => field.StartsWith("MimeType=")) ?? "MimeType=")[9..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var mime in new[] { "x-scheme-handler/nxm", "x-scheme-handler/ror2mm" })
            if (!mimeTypes.Contains(mime)) mimeTypes.Add(mime);
        var values = new Dictionary<string, string>
        {
            ["Exec"] = LinuxInterop.EscapeDesktopExecFilePath(wrapper).Replace("%", "%%") + " %u",
            ["TryExec"] = LinuxInterop.EscapeDesktopFilePath(executable),
            ["MimeType"] = string.Join(";", mimeTypes) + ";",
        };
        foreach (var (key, value) in values)
        {
            var indices = Enumerable.Range(section + 1, end - section - 1).Where(i => lines[i].StartsWith(key + "=")).ToArray();
            if (indices.Length > 1) throw new IOException("Duplicate desktop keys; registration was left unchanged.");
            if (indices.Length == 1) lines[indices[0]] = key + "=" + value;
            else { lines.Insert(end++, key + "=" + value); }
        }
        // A canonical wrapper also avoids xdg-utils' generic quoted-Exec parsing bug. Shell
        // quoting is independent of desktop escaping and never expands executable path text.
        var script = "#!/bin/sh\nexec '" + executable.Replace("'", "'\"'\"'") + "' \"$@\"\n";
        var changed = await WriteIfChanged(wrapper, Encoding.UTF8.GetBytes(script), token);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(wrapper) != mode) File.SetUnixFileMode(wrapper, mode);
        changed |= await WriteIfChanged(desktop, Encoding.UTF8.GetBytes(string.Join("\n", lines)), token);
        return changed;
    }

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Linked registration is externally managed.");
    }

    internal static async Task<bool> WriteIfChanged(string path, byte[] bytes, CancellationToken token)
    {
        RejectLink(path);
        if (File.Exists(path) && (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(bytes)) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }
}
