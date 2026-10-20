using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.IntroDBOnlineWriter;

public sealed class PluginConfiguration : BasePluginConfiguration
{
}

public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "IntroDB Online Writer";
    public override Guid Id => Guid.Parse("2b6c7d9f-b8ee-4876-9e4d-a8094980d4c3");
    public override string Description => "Admin-only, native Media Segment importer for previously validated IntroDB JSONL batches.";
}
