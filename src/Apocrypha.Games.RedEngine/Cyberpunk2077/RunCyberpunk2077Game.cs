using Apocrypha.Abstractions.Games;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Vr;

namespace Apocrypha.Games.RedEngine.Cyberpunk2077;

public class RunCyberpunk2077Game : RunGameTool<Cyberpunk2077Game>
{
    private readonly IServiceProvider _provider;
    public RunCyberpunk2077Game(IServiceProvider provider, Cyberpunk2077Game game) : base(provider, game) => _provider = provider;
    public override async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        if (HasVrPayload(loadout))
            await _provider.GetRequiredService<CyberpunkVrLifecycle>().Prepare(loadout.InstallationInstance, cancellationToken);
        if (commandLineArgs == null)
        {
            commandLineArgs = ["-modded"];
        }
        else
        {
            commandLineArgs = commandLineArgs.Append("-modded").ToArray();
        }
        await base.Execute(loadout, cancellationToken, commandLineArgs);
    }
    public static bool HasVrPayload(Loadout.ReadOnly loadout) => LoadoutItem.FindByLoadout(loadout.Db, loadout)
        .OfTypeLoadoutItemWithTargetPath().Any(f => CyberpunkVrLifecycle.IsVrPlugin(f.TargetPath.Item3.ToString()) && IsEnabled(f.AsLoadoutItem()));

    private static bool IsEnabled(LoadoutItem.ReadOnly item)
    {
        if (item.IsDisabled) return false;
        return !item.HasParent() || IsEnabled(item.Parent.AsLoadoutItem());
    }
}
