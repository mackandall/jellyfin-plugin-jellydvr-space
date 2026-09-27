using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DvrSpace.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DvrSpace;

/// <summary>
/// JellyDVR Space: keeps the DVR's recordings drive from filling up by moving recordings to
/// archive storage (or deleting them) when free space runs low. The dashboard icon comes from
/// meta.json's <c>imagePath</c> (a plain icon.png next to the DLL).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("c709ebd6-431f-493c-93cf-1c345db59d49");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "JellyDVR Space";

    public override string Description =>
        "Keeps the recordings drive from filling up: when free space runs low, moves recordings " +
        "to archive storage (or deletes them) in the order you choose.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
