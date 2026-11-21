using Apocrypha.Games.TestFramework;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;
using Apocrypha.Sdk.Games;
using Apocrypha.StandardGameLocators.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using Xunit.Abstractions;

namespace Apocrypha.DataModel.Synchronizer.Tests;

public class AVrIsolatedGameTest<T> : ACyberpunkIsolatedGameTest<T>
{
    protected readonly AbsolutePath SettingsFile;
    protected TestVrRemovalReview ReviewChoice => ServiceProvider.GetRequiredService<TestVrRemovalReview>();
    protected CyberpunkVrLifecycle Lifecycle => ServiceProvider.GetRequiredService<CyberpunkVrLifecycle>();

    public AVrIsolatedGameTest(ITestOutputHelper output) : base(output)
    {
        var prefix = TemporaryFileManager.CreateFolder("vr-prefix-fixture").Path;
        SettingsFile = prefix / "drive_c/users/fixture/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json";
        SettingsFile.Parent.CreateDirectory();
        File.WriteAllText(SettingsFile.ToString(), "{\"graphics\":\"flat\",\"personal\":true}");
        File.WriteAllText((prefix / "user.reg").ToString(), "[Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Explorer\\\\Shell Folders]\n\"Local AppData\"=\"C:\\\\users\\\\fixture\\\\AppData\\\\Local\"\n");
        var compatibility = Substitute.For<ILinuxCompatabilityDataProvider>();
        compatibility.WinePrefixDirectoryPath.Returns(prefix);
        ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<Cyberpunk2077Game>>().Single().Compatibility = compatibility;
    }

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddSingleton<TestVrRemovalReview>()
        .AddSingleton<IVrRemovalReview>(provider => provider.GetRequiredService<TestVrRemovalReview>());

    public sealed class TestVrRemovalReview : IVrRemovalReview
    {
        public VrRemovalChoice Choice { get; set; } = VrRemovalChoice.RestoreSettings;
        public int Calls { get; private set; }
        public Task<VrRemovalChoice> Choose(GameInstallation installation, CyberpunkVrLifecycle.Review review)
        {
            Calls++;
            return Task.FromResult(Choice);
        }
    }
}
