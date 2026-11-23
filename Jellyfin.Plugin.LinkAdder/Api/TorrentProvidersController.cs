using System.Xml;
using System.Text.Json;
using Jellyfin.Plugin.LinkAdder.Configuration;
using Jellyfin.Plugin.LinkAdder.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.LinkAdder.Api;

[ApiController]
[Authorize]
[Route("Plugins/LinkAdder/TorrentProviders")]
public sealed class TorrentProvidersController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult List()
    {
        lock (Plugin.ConfigurationSync)
        {
            return Ok(Plugin.Instance!.Configuration.TorrentProviders.Select(View).ToArray());
        }
    }

    [HttpGet("Catalog")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Catalog()
    {
        lock (Plugin.ConfigurationSync)
        {
            var configured = Plugin.Instance!.Configuration.TorrentProviders.Select(provider => provider.CatalogId).ToHashSet();
            return Ok(TorrentProviderCatalog.Entries.Select(entry => new
            {
                entry.Id, entry.Name, entry.Description, entry.Categories, Added = configured.Contains(entry.Id)
            }).ToArray());
        }
    }

    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Test([FromBody] ProviderInput input, CancellationToken ct)
    {
        if (Validate(input) is not { } provider) return BadRequest(new { error = ValidationMessage });
        try
        {
            await TorrentProviderCatalog.TestAsync(provider, ct);
            return Ok(new { message = "Provider search is available." });
        }
        catch (Exception error) when (ProviderFailed(error, ct))
        {
            return BadRequest(new { error = error is InvalidDataException ? error.Message : "The server could not complete a test search with the provider." });
        }
    }

    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Add([FromBody] ProviderInput input)
    {
        if (Validate(input) is not { } provider) return BadRequest(new { error = ValidationMessage });
        lock (Plugin.ConfigurationSync)
        {
            var plugin = Plugin.Instance!;
            var configuration = plugin.Configuration;
            if (configuration.TorrentProviders.Count >= 10)
                return BadRequest(new { error = "You can configure up to ten torrent providers." });
            if (configuration.TorrentProviders.Any(x => provider.CatalogId.Length > 0
                ? x.CatalogId == provider.CatalogId
                : string.IsNullOrEmpty(x.CatalogId) && x.Endpoint == provider.Endpoint))
                return Conflict(new { error = "That provider is already configured." });
            configuration.TorrentProviders.Add(provider);
            plugin.UpdateConfiguration(configuration);
        }
        return Ok(View(provider));
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Remove(string id)
    {
        lock (Plugin.ConfigurationSync)
        {
            var plugin = Plugin.Instance!;
            var configuration = plugin.Configuration;
            if (configuration.TorrentProviders.RemoveAll(x => x.Id == id) == 0) return NotFound();
            plugin.UpdateConfiguration(configuration);
        }
        return NoContent();
    }

    [HttpPost("Search")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Search([FromBody] SearchInput input, [FromServices] IAuthorizationService authorization,
        [FromServices] IProviderManager metadata, [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        var query = input.Query?.Trim();
        var filter = input.MediaType ?? string.Empty;
        if (query is null || query.Length is < 2 or > 200 || filter is not ("" or "movie" or "series" or "anime" or "other" or "adult"))
            return BadRequest(new { error = "Enter a search term between 2 and 200 characters and choose a supported media type." });
        if (filter == "adult" && !(await authorization.AuthorizeAsync(User, Policies.RequiresElevation)).Succeeded)
            return Forbid();

        TorrentProvider[] providers;
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance!.Configuration;
            if (filter == "adult" && !configuration.AdultTorrentModeEnabled)
                return BadRequest(new { error = "Enable 18+ torrent mode in Setup Media to search adult torrents." });
            if (filter == "adult" && !configuration.TorrentSourcesEnabled)
                return BadRequest(new { error = "Enable torrent sources in Setup Media to search adult torrents." });
            providers = configuration.TorrentSourcesEnabled
                ? configuration.TorrentProviders.Where(provider => TorrentProviderCatalog.SupportsFilter(provider, filter)).ToArray()
                : [];
        }
        if (providers.Length > 0 && !MediaOperationLimits.TrySearch(MediaOperationLimits.UserId(User)))
            return StatusCode(429, new { error = "Search request limit reached. Try again shortly." });

        var responses = await Task.WhenAll(providers.Select(async provider =>
        {
            try { return (Results: await TorrentProviderCatalog.SearchAsync(provider, query, filter, ct), Failed: false); }
            catch (Exception error) when (ProviderFailed(error, ct)) { return (Results: Array.Empty<TorrentRelease>(), Failed: true); }
        }));
        var releases = responses.SelectMany(x => x.Results).OrderByDescending(x => x.Seeders).ToArray();
        releases = await TorrentArtwork.EnrichAsync(releases, query, filter, metadata, cache, ct);
        return Ok(new { releases, providerCount = providers.Length, failedProviders = responses.Count(x => x.Failed) });
    }

    private const string ValidationMessage = "Select a built-in provider or enter a name and an HTTP or HTTPS Torznab endpoint.";

    private static TorrentProvider? Validate(ProviderInput input)
    {
        if (!string.IsNullOrEmpty(input.CatalogId))
        {
            var entry = TorrentProviderCatalog.Entries.FirstOrDefault(item => item.Id == input.CatalogId);
            return entry is null ? null : new TorrentProvider { CatalogId = entry.Id, Name = entry.Name, Endpoint = entry.Endpoint };
        }
        var name = input.Name?.Trim() ?? string.Empty;
        var key = input.ApiKey?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80 || key.Length > 512 || input.Endpoint is null || input.Endpoint.Length > 2048
            || !Uri.TryCreate(input.Endpoint.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.Host.Length == 0
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            return null;
        return new TorrentProvider { Name = name, Endpoint = endpoint.AbsoluteUri, ApiKey = key };
    }

    private static object View(TorrentProvider provider) => new
    {
        provider.Id, provider.Name, provider.Endpoint, provider.CatalogId, HasApiKey = provider.ApiKey.Length > 0
    };

    private static bool ProviderFailed(Exception error, CancellationToken ct) =>
        error is HttpRequestException or XmlException or JsonException or InvalidDataException or IOException
        || (error is OperationCanceledException && !ct.IsCancellationRequested);

    public sealed record ProviderInput(string? Name, string? Endpoint, string? ApiKey, string? CatalogId);
    public sealed record SearchInput(string? Query, string? MediaType);
}
