using Jellyfin.Plugin.DvrSpace.Devices;
using Jellyfin.Plugin.DvrSpace.Space;
using Jellyfin.Plugin.DvrSpace.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DvrSpace;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<CategorySorter>();
        serviceCollection.AddSingleton<SpaceManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SpaceManager>());
        serviceCollection.AddHostedService<TranscodeSweeper>();

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(SpaceController).Assembly);

        // On every request to the server, not only this plugin's: what a JDVR client may do.
        serviceCollection.AddSingleton<DeviceRightsFilter>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.AddService<DeviceRightsFilter>());
    }
}
