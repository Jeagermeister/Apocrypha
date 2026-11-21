using Microsoft.Extensions.DependencyInjection;
using Apocrypha.Abstractions.Games;
using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Sdk.Settings;
using Apocrypha.Games.RedEngine.Cyberpunk2077;
using Apocrypha.Games.RedEngine.Cyberpunk2077.Models;
using Apocrypha.Games.RedEngine.Cyberpunk2077.SortOrder;

namespace Apocrypha.Games.RedEngine;

public static class Services
{
    public static IServiceCollection AddRedEngineGames(this IServiceCollection services)
    {
        services.AddSingleton(_ => new Cyberpunk2077.Vr.CyberpunkVrLifecycle(
                System.IO.Path.Combine(NexusMods.Paths.FileSystem.Shared.GetKnownPath(NexusMods.Paths.KnownPath.XDG_DATA_HOME).ToString(), "Apocrypha", "CyberpunkVRRecovery")));
        services.AddGame<Cyberpunk2077Game>()
            .AddRedModInfoFileModel()
            .AddRedModSortOrderModel()
            .AddRedModLoadoutGroupModel()
            .AddRedModSortOrderItemModel()
            .AddRedModQueriesSql()
            .AddSingleton<RedModSortOrderVariety, RedModSortOrderVariety>()
            .AddSingleton<ITool, RunCyberpunk2077Game>()
            .AddSingleton<ITool, RedModDeployTool>()
            // Diagnostics
            
            
            .AddSettings<Cyberpunk2077Settings>();
        return services;
    }
}
