using Jellyfin.Plugin.LinkAdder.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LinkAdder;

/// <summary>
/// Registers the web-client header integration without modifying Jellyfin's files.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IStartupFilter, HeaderButtonStartupFilter>();
        serviceCollection.AddSingleton<DiscoveryService>();
        serviceCollection.AddSingleton<SourcePlayback>();
        serviceCollection.AddHttpClient("LinkAdderTorrentBridge").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
        serviceCollection.AddSingleton<AutomationService>();
        serviceCollection.AddSingleton<DownloadService>();
        serviceCollection.AddHostedService(services => services.GetRequiredService<DownloadService>());
        serviceCollection.AddHostedService<AutomationBackgroundService>();
        serviceCollection.AddHostedService<TorrentBridgeBackgroundService>();
    }
}
