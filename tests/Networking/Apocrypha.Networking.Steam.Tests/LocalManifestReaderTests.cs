using FluentAssertions;
using Apocrypha.Abstractions.Steam.Values;
using Apocrypha.Networking.Steam.Local;
using NexusMods.Paths;
using Xunit;

namespace Apocrypha.Networking.Steam.Tests;

/// <summary>
/// Tests for the login-free local recognition file-name resolution: given only a manifest id (as the
/// Steam locator exposes at runtime), the cached <c>&lt;depotId&gt;_&lt;manifestId&gt;.manifest</c> file is located
/// and its depot id recovered from the file name.
/// </summary>
public class LocalManifestReaderTests(IFileSystem fileSystem)
{
    [Fact]
    public void TryFindManifestFile_RecoversDepotId_FromCachedFileName()
    {
        var dir = CreateTempDir();
        try
        {
            Touch(dir / "413153_6005910083361727734.manifest");
            Touch(dir / "261551_813532240231811649.manifest");
            Touch(dir / "not-a-manifest.txt");

            var found = LocalManifestReader.TryFindManifestFile(
                dir,
                ManifestId.From(6005910083361727734UL),
                out var manifestPath,
                out var depotId);

            found.Should().BeTrue();
            depotId.Should().Be(DepotId.From(413153));
            manifestPath.FileName.ToString().Should().Be("413153_6005910083361727734.manifest");
        }
        finally
        {
            dir.DeleteDirectory(recursive: true);
        }
    }

    [Fact]
    public void TryFindManifestFile_ReturnsFalse_WhenNoMatchingManifest()
    {
        var dir = CreateTempDir();
        try
        {
            // A different manifest id is present; the requested one is not.
            Touch(dir / "261551_813532240231811649.manifest");

            LocalManifestReader.TryFindManifestFile(
                    dir,
                    ManifestId.From(6005910083361727734UL),
                    out _,
                    out _)
                .Should().BeFalse();
        }
        finally
        {
            dir.DeleteDirectory(recursive: true);
        }
    }

    [Fact]
    public void TryFindManifestFile_DoesNotMatch_ManifestIdSuffixWithoutUnderscore()
    {
        var dir = CreateTempDir();
        try
        {
            // "999_9123.manifest" ends in "9123.manifest" but NOT in "_123.manifest" — the underscore
            // in the suffix is what prevents manifest id 123 from matching it.
            Touch(dir / "999_9123.manifest");

            LocalManifestReader.TryFindManifestFile(
                    dir,
                    ManifestId.From(123UL),
                    out _,
                    out _)
                .Should().BeFalse();
        }
        finally
        {
            dir.DeleteDirectory(recursive: true);
        }
    }

    [Fact]
    public void TryFindManifestFile_ReturnsFalse_WhenDepotPrefixIsNotNumeric()
    {
        var dir = CreateTempDir();
        try
        {
            Touch(dir / "abc_123.manifest");
            Touch(dir / "_123.manifest");

            LocalManifestReader.TryFindManifestFile(
                    dir,
                    ManifestId.From(123UL),
                    out _,
                    out _)
                .Should().BeFalse();
        }
        finally
        {
            dir.DeleteDirectory(recursive: true);
        }
    }

    [Fact]
    public void TryFindManifestFile_ReturnsFalse_WhenDirectoryMissing()
    {
        var dir = CreateTempDir();
        dir.DeleteDirectory(recursive: true);

        LocalManifestReader.TryFindManifestFile(
                dir,
                ManifestId.From(1UL),
                out _,
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryReadManifest_ReadOnlyCache_IsParsedWithoutWriteAccess()
    {
        if (Environment.IsPrivilegedProcess || !OperatingSystem.IsLinux()) return;
        var dir = CreateTempDir();
        var path = dir / "42_123.manifest";
        try
        {
            // A minimal plaintext Steam manifest, serialized through SteamKit's public API.
            var manifest = (SteamKit2.DepotManifest)Activator.CreateInstance(typeof(SteamKit2.DepotManifest), nonPublic: true)!;
            typeof(SteamKit2.DepotManifest).GetProperty("DepotID")!.SetValue(manifest, 42u);
            typeof(SteamKit2.DepotManifest).GetProperty("ManifestGID")!.SetValue(manifest, 123UL);
            typeof(SteamKit2.DepotManifest).GetProperty("Files")!.SetValue(manifest, new List<SteamKit2.DepotManifest.FileData>());
            typeof(SteamKit2.DepotManifest).GetProperty("CreationTime")!.SetValue(manifest, DateTime.UnixEpoch);
            manifest.SaveToFile(path.ToString());
            File.SetUnixFileMode(path.ToString(), UnixFileMode.UserRead);
            var before = File.ReadAllBytes(path.ToString());
            var reader = new LocalManifestReader(Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalManifestReader>.Instance);
            var parsed = reader.TryReadManifestByManifestId(dir, ManifestId.From(123));
            parsed.Should().NotBeNull();
            parsed!.DepotId.Should().Be(DepotId.From(42));
            parsed.ManifestId.Should().Be(ManifestId.From(123));
            File.ReadAllBytes(path.ToString()).Should().Equal(before);
        }
        finally
        {
            if (path.FileExists) File.SetUnixFileMode(path.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            dir.DeleteDirectory(recursive: true);
        }
    }

    [Theory]
    [InlineData(43u, 123UL)]
    [InlineData(42u, 124UL)]
    public void CachedManifestMustMatchItsRequestedIdentity(uint depot, ulong manifestId)
    {
        var dir = CreateTempDir();
        try
        {
            var manifest = (SteamKit2.DepotManifest)Activator.CreateInstance(typeof(SteamKit2.DepotManifest), nonPublic: true)!;
            typeof(SteamKit2.DepotManifest).GetProperty("DepotID")!.SetValue(manifest, depot);
            typeof(SteamKit2.DepotManifest).GetProperty("ManifestGID")!.SetValue(manifest, manifestId);
            typeof(SteamKit2.DepotManifest).GetProperty("Files")!.SetValue(manifest, new List<SteamKit2.DepotManifest.FileData>());
            typeof(SteamKit2.DepotManifest).GetProperty("CreationTime")!.SetValue(manifest, DateTime.UnixEpoch);
            manifest.SaveToFile((dir / "42_123.manifest").ToString());
            var reader = new LocalManifestReader(Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalManifestReader>.Instance);
            reader.TryReadManifest(dir, DepotId.From(42), ManifestId.From(123)).Should().BeNull();
        }
        finally { dir.DeleteDirectory(recursive: true); }
    }

    private AbsolutePath CreateTempDir()
    {
        var dir = fileSystem.FromUnsanitizedFullPath(Path.Combine(Path.GetTempPath(), "nm-manifest-test-" + Guid.NewGuid().ToString("N")));
        dir.CreateDirectory();
        return dir;
    }

    private static void Touch(AbsolutePath path)
    {
        using var _ = path.Create();
    }
}
