using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using Apocrypha.Sdk;
using Apocrypha.Sdk.IO;

namespace Apocrypha.Backend.OS;

internal partial class LinuxInterop
{
    private const string ApplicationId = ApplicationIdentity.AppId;
    private const string DesktopFile = $"{ApplicationId}.desktop";
    private const string DesktopFileResourceName = $"Apocrypha.Backend.{DesktopFile}";
    private const string ExecuteParameterPlaceholder = "${INSTALL_EXEC}";
    private const string TryExecuteParameterPlaceholder = "${INSTALL_TRYEXEC}";

    public async ValueTask RegisterUriSchemeHandler(string scheme, bool setAsDefaultHandler, CancellationToken cancellationToken)
    {
        if (DesktopIntegration.IsDisabled) return;
        // A framework-dependent launch without an apphost (`dotnet <dll>`, test hosts) has no
        // executable a handler could point at: GetRunningExecutablePath falls back to the bare
        // dotnet host, and a `dotnet %u` Exec swallows every link. Registering that would
        // CLOBBER a previously working registration (running the test suite on a dev box used
        // to break nxm/ror2mm handling this way) — leave the existing registration alone.
        var executable = GetRunningExecutablePath(out _);
        if (executable.FileName == "dotnet")
        {
            _logger.LogWarning("Skipping URI scheme registration for `{Scheme}`: no apphost binary to point the handler at", scheme);
            return;
        }

        var canWriteDesktopFile = ApplicationConstants.InstallationMethod is InstallationMethod.AppImage or InstallationMethod.Manually;
        var canRegisterAsDefault = ApplicationConstants.InstallationMethod is not InstallationMethod.Flatpak and not InstallationMethod.PackageManager;

        if (canWriteDesktopFile)
        {
            var applicationsDirectory = _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("applications");
            _logger.LogInformation("Using applications directory `{Path}`", applicationsDirectory);

            try
            {
                var changed = await CreateDesktopFile(applicationsDirectory, cancellationToken);
                if (changed) await UpdateMIMECacheDatabase(applicationsDirectory, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception while creating desktop file, the handler for `{Scheme}` might not work", scheme);
                return;
            }

            try
            {
                await InstallIconThemeFiles(cancellationToken);
            }
            catch (Exception e)
            {
                // Icons are cosmetic: a failure here must not break protocol handling.
                _logger.LogWarning(e, "Exception while installing icon theme files; the desktop may show a generic icon");
            }


        }

        if (setAsDefaultHandler && canRegisterAsDefault)
        {
            try
            {
                await SetAsDefaultHandler(scheme, cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception while setting the default handler for `{Scheme}`, see the process logs for more details", scheme);
            }
        }
    }

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>
    /// Installs the app icons into the user's hicolor theme so the desktop file's themed
    /// <c>Icon=</c> name actually resolves.
    /// </summary>
    /// <remarks>
    /// The desktop file has always said <c>Icon=io.github.jeagermeister.apocrypha</c>, but a
    /// themed name only resolves if a matching file exists in an icon theme directory. The
    /// AppImage bundles the icons <em>inside</em> itself where no desktop environment looks
    /// unless the AppImage is "integrated" — which nothing does by default — so docks and
    /// window switchers fell back to the generic gear. Writing the icons into
    /// <c>$XDG_DATA_HOME/icons/hicolor</c> alongside the desktop file closes the loop for
    /// AppImage and zip installs alike; the sizes merge with the system hicolor theme, so no
    /// <c>index.theme</c> is needed. Files are rewritten on every registration pass, which
    /// keeps them current when the branding changes between versions.
    /// </remarks>
    private async Task InstallIconThemeFiles(CancellationToken cancellationToken)
    {
        var hicolorDirectory = _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("icons/hicolor");

        foreach (var size in IconSizes)
        {
            var directory = hicolorDirectory.Combine($"{size}x{size}/apps");
            if (!directory.DirectoryExists()) directory.CreateDirectory();

            var target = directory.Combine($"{ApplicationId}.png");
            await using var source = await new EmbeddedResourceStreamFactory<LinuxInterop>($"Apocrypha.Backend.apocrypha.{size}.png").GetStreamAsync();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            await PortableDesktopRegistration.WriteIfChanged(target.ToString(), buffer.ToArray(), cancellationToken);
        }

        _logger.LogInformation("Installed {Count} hicolor theme icons under `{Path}`", IconSizes.Length, hicolorDirectory);
    }

    /// <summary>
    /// Removes the pre-rebrand registration files — xdg re-registers the new id right after,
    /// which also supersedes the old id in mimeapps.list defaults.
    /// </summary>
    private void RemoveLegacyDesktopFiles(AbsolutePath applicationsDirectory)
    {
        foreach (var legacyName in (string[])[$"{ApplicationIdentity.LegacyAppId}.desktop", $"{ApplicationIdentity.LegacyAppId}.desktop.bak", $"{ApplicationIdentity.LegacyAppId}.sh"])
        {
            var legacyFile = applicationsDirectory.Combine(legacyName);
            if (!legacyFile.FileExists) continue;

            _logger.LogInformation("Removing pre-rebrand registration file `{Path}`", legacyFile);
            legacyFile.Delete();
        }
    }

    private async Task<bool> CreateDesktopFile(AbsolutePath applicationsDirectory, CancellationToken cancellationToken = default)
    {
        if (!applicationsDirectory.DirectoryExists()) applicationsDirectory.CreateDirectory();
        _ = GetRunningExecutablePath(out var processPath);
        await using var stream = await new EmbeddedResourceStreamFactory<LinuxInterop>(DesktopFileResourceName).GetStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await PortableDesktopRegistration.Repair(applicationsDirectory.ToString(), processPath,
            await reader.ReadToEndAsync(cancellationToken), cancellationToken);
    }

    private async Task UpdateMIMECacheDatabase(AbsolutePath applicationsDirectory, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating MIME cache database");

        var command = _updateDesktopDatabaseDependency.BuildUpdateCommand($"\"{applicationsDirectory}\"");
        await _processRunner.RunAsync(command, logOutput: true, cancellationToken: cancellationToken);
    }

    private async Task SetAsDefaultHandler(string uriScheme, CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        await _processRunner.RunAsync(Cli.Wrap("xdg-mime").WithArguments(["query", "default", $"x-scheme-handler/{uriScheme}"])
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output)), logOutput: false, cancellationToken: cancellationToken);
        var current = output.ToString().Trim();
        if (!PortableDesktopRegistration.ShouldSetDefault(current)) return;

        // Note(sewer): This adds `MimeType=x-scheme-handler/nxm;` to the protocol handler entry
        //              so if we supply this in the existing `.desktop` file running this would make
        //              a duplicate.
        var command = _xdgSettingsDependency.CreateSetDefaultUrlSchemeHandlerCommand(uriScheme, DesktopFile);
        await _processRunner.RunAsync(command, logOutput: true, cancellationToken: cancellationToken);
    }

    // Note(sewer)
    // For the exec field. Quoted relevant parts below.
    // https://specifications.freedesktop.org/desktop-entry-spec/latest/exec-variables.html
    //
    // Arguments may be quoted in whole. If an argument contains a reserved character the argument must be quoted.
    // The rules for quoting of arguments is also applicable to the executable name or path of the executable
    // program as provided.
    //
    // Quoting must be done by enclosing the argument between double quotes and escaping the double quote
    // character, backtick character ("`"), dollar sign ("$") and backslash character ("\") by preceding
    // it with an additional backslash character.
    internal static string EscapeDesktopExecFilePath(string path)
    {
        var originalPath = path;

        // Note(sewer)
        //
        // Spec says"
        //
        // > Note that the general escape rule for values of type string states that the backslash
        // > character can be escaped as ("\\") as well and that this escape rule is applied before the quoting rule
        // 
        // BUT it wasn't clear if this was for decoding or encoding.
        // Turns out it's for decoding.
        //
        // You can verify this by putting the App in a folder with a space in it.
        // The correct output is '\s', not '\\s'. Former works, latter does not.
        var escapedPath = path
            .Replace(@"\", @"\\") // and backslash character ("\")                 \ -> \\
            .Replace("\"", @"\""") // 'and escaping the double quote character'    " -> \" 
            .Replace("`", @"\`") // backtick character ("`")                       ` -> \`
            .Replace("$", @"\$"); // dollar sign ("$")                             $ -> \$

        // First apply the base rules from `string` and `localstring`.
        var escapedFinalPath = EscapeDesktopFilePath(escapedPath);

        // Note(sewer): Quoting the spec
        // 
        // > Note that the general escape rule for values of type string states that the backslash
        //   character can be escaped as ("\\") as well and that this escape rule is applied before
        //   the quoting rule. As such, to unambiguously represent a literal backslash character in
        //   a quoted argument in a desktop entry file requires the use of four successive backslash
        //   characters ("\\\\").
        // 
        // So escaping `\` twice, leading to 4 backslashes as a result of applying both functions is by design,
        // even if it may 'feel weird'.
        //
        // > Likewise, a literal dollar sign in a quoted argument in a desktop
        //   entry file is unambiguously represented with ("\\$"). 
        //
        // So we go from `$` to `\$`.
        // And then from `\$` to `\\$`.
        // 
        // As per the example in the spec, this is not a bug, this is intended behaviour, even if weird.

        // Note(sewer):
        //
        // The docs say:
        // > Arguments may be quoted in whole. If an argument contains a reserved character the argument must be quoted.
        //
        // > Reserved characters are space (" "), tab, newline, double quote, single quote ("'"), backslash character ("\"),
        // > greater-than sign (">"), less-than sign ("<"), tilde ("~"), vertical bar ("|"), ampersand ("&"), semicolon (";"),
        // > dollar sign ("$"), asterisk ("*"), question mark ("?"), hash mark ("#"), parenthesis ("(") and (")") and backtick character ("`").
        //
        // In this case, we will quote if our path has changed, else we'll leave it unquoted.
        if (escapedFinalPath == originalPath && !path.Any(c => "'<>~|&;*?#()".Contains(c)))
            return originalPath; // No need to quote if the path hasn't changed
        else
            return $"\"{escapedFinalPath}\""; // Enclose the entire path in double quotes
    }

    // Note(sewer)
    // For other fields 'string' and 'localestring':
    //
    // https://specifications.freedesktop.org/desktop-entry-spec/1.0/value-types.html
    // The escape sequences \s, \n, \t, \r, and \\ are supported for values of type string and localestring,
    // meaning ASCII space, newline, tab, carriage return, and backslash, respectively. 
    //
    // Note that 'Exec' is a string https://specifications.freedesktop.org/desktop-entry-spec/1.0/recognized-keys.html
    internal static string EscapeDesktopFilePath(string path)
    {
        return path
            .Replace(@"\", @"\\") // Escape backslashes first to avoid double escaping
            .Replace(" ", @"\s")
            .Replace("\n", @"\n")
            .Replace("\t", @"\t")
            .Replace("\r", @"\r");
    }
}
