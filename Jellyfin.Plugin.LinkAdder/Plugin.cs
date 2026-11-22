using System.Security.Cryptography;
using Jellyfin.Plugin.LinkAdder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LinkAdder;
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    internal static object ConfigurationSync { get; } = new();

    internal static string GetPlaybackSigningKey()
    {
        lock (ConfigurationSync)
        {
            var plugin = Instance ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            var configuration = plugin.Configuration;
            if (string.IsNullOrEmpty(configuration.PlaybackSigningKey))
            {
                configuration.PlaybackSigningKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                plugin.UpdateConfiguration(configuration);
            }
            return configuration.PlaybackSigningKey;
        }
    }

    internal static string GetBridgeSecret() => Environment.GetEnvironmentVariable("TORRENT_BRIDGE_SECRET")
        ?? Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(GetPlaybackSigningKey()), "torrent-bridge"u8));

    internal static CustomSource[] GetSourceSnapshot(bool includeDisabled = false)
    {
        lock (ConfigurationSync)
        {
            if (Instance?.Configuration is not { } configuration) return [];
            return includeDisabled || configuration.FtpSourcesEnabled ? configuration.CustomSources.ToArray() : [];
        }
    }

    internal static bool MediaServerSourcesEnabled
    {
        get
        {
            lock (ConfigurationSync) return Instance?.Configuration.FtpSourcesEnabled ?? true;
        }
    }

    internal static bool TorrentSourcesEnabled
    {
        get
        {
            lock (ConfigurationSync) return Instance?.Configuration.TorrentSourcesEnabled ?? true;
        }
    }

    public static Plugin? Instance { get; private set; }
    public override string Name => "Media Source Bridge";
    public override string Description => "Search private media servers, import links, and add movies, shows, or anime to Jellyfin as streams or local files.";
    public override Guid Id => Guid.Parse("9399b942-4b6d-46a7-9a5d-a01764f7ee6a");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new()
        {
            Name = "localispplugins",
            EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.pluginsPage.html",
            EnableInMainMenu = true,
            DisplayName = "Media Source Bridge",
            MenuSection = "main",
            MenuIcon = "extension"
        },
        new() { Name = "localispdiscovery", EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.discoveryPage.html" },
        new() { Name = "localispsearch", EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.searchPage.html" },
        new() { Name = "localispmediaadd", EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.configPage.html" },
        new() { Name = "localispservers", EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.serverPage.html" },
        new() { Name = "localispimportlink", EmbeddedResourcePath = "Jellyfin.Plugin.LinkAdder.Configuration.importLinkPage.html" }
    ];
}
