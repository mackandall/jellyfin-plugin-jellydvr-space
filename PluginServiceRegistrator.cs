using Jellyfin.Plugin.DvrSpace.Space;
using Jellyfin.Plugin.DvrSpace.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DvrSpace;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SpaceManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SpaceManager>());

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(SpaceController).Assembly);
    }
}
