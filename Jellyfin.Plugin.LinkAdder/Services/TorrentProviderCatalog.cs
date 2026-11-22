using System.Globalization;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Jellyfin.Plugin.LinkAdder.Configuration;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.LinkAdder.Services;

internal static class TorrentProviderCatalog
{
    private const string KnabenEndpoint = "https://api.knaben.org/v2/search";
    private const string Nyaa = "nyaa";
    private const string ZeroMagnet = "0magnet";
    private const string NyaaNamespace = "https://nyaa.si/xmlns/nyaa";
    private const int KnabenMovies = 3_000_000;
    private const int KnabenShows = 2_000_000;
    private static readonly int[] KnabenAnime = [6_001_000, 6_002_000, 6_003_000, 6_004_000];

    internal static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        new("knaben", "Knaben", "Searches multiple public torrent indexes.", KnabenEndpoint,
            ["movie", "series", "anime"]),
        new(Nyaa, "Nyaa", "Searches public anime releases.", "https://nyaa.si/", ["anime"]),
        new("1337x", "1337x", "Movie, show, and anime releases from 1337x via Knaben.", KnabenEndpoint,
            ["movie", "series", "anime"], "1337x"),
        new("thepiratebay", "The Pirate Bay", "Movie, show, and anime releases via Knaben.", KnabenEndpoint,
            ["movie", "series", "anime"], "thepiratebay"),
        new("yts", "YTS", "Movie releases from YTS via Knaben.", KnabenEndpoint, ["movie"], "yts"),
        new(ZeroMagnet, "0Magnet", "Adult video releases. Searches are available to administrators.", "https://16mag.net/", ["adult"])
    ];

    internal static async Task TestAsync(TorrentProvider provider, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(provider.CatalogId))
        {
            await TorznabClient.TestAsync(provider, ct);
            return;
        }
        var filter = provider.CatalogId == ZeroMagnet ? "adult" : string.Empty;
        var query = provider.CatalogId is Nyaa or ZeroMagnet ? "Sintel" : "Night of the Living Dead";
        await SearchAsync(provider, query, filter, ct);
    }

    internal static bool SupportsFilter(TorrentProvider provider, string filter) =>
        string.IsNullOrEmpty(provider.CatalogId)
        || Entries.Any(entry => entry.Id == provider.CatalogId
            && (filter.Length == 0 ? entry.Id != ZeroMagnet : entry.Categories.Contains(filter)));

    internal static Task<TorrentRelease[]> SearchAsync(TorrentProvider provider, string query, string filter, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(provider.CatalogId))
            return TorznabClient.SearchAsync(provider, query, filter, ct);
        var entry = Entries.FirstOrDefault(item => item.Id == provider.CatalogId)
            ?? throw new InvalidDataException("This built-in provider is unknown.");
        return entry.Id switch
        {
            Nyaa => SearchNyaaAsync(entry, query, ct),
            ZeroMagnet => SearchZeroMagnetAsync(entry, query, ct),
            _ => SearchKnabenAsync(entry, query, filter, ct)
        };
    }

    private static async Task<TorrentRelease[]> SearchKnabenAsync(CatalogEntry entry, string query, string filter, CancellationToken ct)
    {
        int[] categories = filter switch
        {
            "movie" => [KnabenMovies],
            "series" => [KnabenShows],
            "anime" => KnabenAnime,
            _ when entry.Categories.Length == 1 => [KnabenMovies],
            _ => [KnabenMovies, KnabenShows, .. KnabenAnime]
        };
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query, ["c"] = string.Join(',', categories), ["s"] = "50", ["o"] = "seeders"
        };
        if (entry.TrackerId is not null) parameters["t"] = entry.TrackerId;
        var url = QueryHelpers.AddQueryString(entry.Endpoint, parameters) + "&dead";
        using var response = await TorznabClient.Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<KnabenResult>(cancellationToken: ct);
        if (result?.Hits is null) throw new InvalidDataException("Knaben did not return a search result.");
        return result.Hits.Take(50)
            .Where(hit => !string.IsNullOrWhiteSpace(hit.Title)
                && (entry.TrackerId is null || hit.TrackerId == entry.TrackerId))
            .Select(hit => (Hit: hit, Type: KnabenType(hit.Category)))
            .Where(item => item.Type is not null && (filter.Length == 0 || item.Type == filter))
            .Select(item => TorznabClient.BuildRelease(item.Hit.Title!, item.Type!, entry.Name, item.Hit.Bytes, item.Hit.Seeders,
                TorznabClient.ReadMagnetHash(item.Hit.MagnetUrl) ?? item.Hit.Hash))
            .ToArray();
    }

    private static async Task<TorrentRelease[]> SearchZeroMagnetAsync(CatalogEntry entry, string query, CancellationToken ct)
    {
        var site = new Uri(entry.Endpoint);
        var search = QueryHelpers.AddQueryString(new Uri(site, "search").AbsoluteUri, "q", query);
        using var response = await TorznabClient.Http.GetAsync(search, ct);
        response.EnsureSuccessStatusCode();
        var parser = new HtmlParser();
        using var document = await parser.ParseDocumentAsync(await response.Content.ReadAsStringAsync(ct), ct);
        var results = document.QuerySelector("table > tbody") ?? throw new InvalidDataException("0Magnet did not return a search table.");
        var tasks = results.QuerySelectorAll("td.result-title a[href]").Take(8).Select(async link =>
        {
            var title = link.TextContent.Trim();
            var path = link.GetAttribute("href");
            if (title.Length == 0 || path is null || !path.StartsWith("/!", StringComparison.Ordinal)
                || !Uri.TryCreate(site, path, out var detailUrl) || detailUrl.GetLeftPart(UriPartial.Authority) != site.GetLeftPart(UriPartial.Authority))
                return null;
            var size = link.ParentElement?.ParentElement?.QuerySelector("td.result-meta > div")?.TextContent.Trim();
            string? hash = null;
            try
            {
                using var detail = await TorznabClient.Http.GetAsync(detailUrl, ct);
                if (detail.IsSuccessStatusCode)
                {
                    using var page = await new HtmlParser().ParseDocumentAsync(await detail.Content.ReadAsStringAsync(ct), ct);
                    hash = TorznabClient.ReadMagnetHash(page.QuerySelector("a[href^='magnet:?xt']")?.GetAttribute("href"));
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return TorznabClient.BuildRelease(title, "adult", entry.Name, ReadSize(size), null, hash, title);
        });
        return (await Task.WhenAll(tasks)).OfType<TorrentRelease>().ToArray();
    }

    private static string? KnabenType(string? category) => category?.Split('/', 2)[0].Trim().ToLowerInvariant() switch
    {
        "movies" => "movie",
        "tv" => "series",
        "anime" => "anime",
        _ => null
    };

    private static async Task<TorrentRelease[]> SearchNyaaAsync(CatalogEntry entry, string query, CancellationToken ct)
    {
        var url = QueryHelpers.AddQueryString(entry.Endpoint, new Dictionary<string, string?>
        {
            ["page"] = "rss", ["q"] = query, ["c"] = "1_0", ["f"] = "0"
        });
        using var response = await TorznabClient.Http.GetAsync(url, ct);
        var root = await TorznabClient.ReadXmlAsync(response, ct);
        var channel = root.Element("channel") ?? throw new InvalidDataException("Nyaa did not return an RSS feed.");
        return channel.Elements("item").Take(50).Select(item =>
        {
            string? Field(string name) => item.Element(System.Xml.Linq.XName.Get(name, NyaaNamespace))?.Value;
            var title = item.Element("title")?.Value;
            if (string.IsNullOrWhiteSpace(title) || Field("categoryId") is not ("1_2" or "1_3" or "1_4")) return null;
            return TorznabClient.BuildRelease(title, "anime", entry.Name, ReadSize(Field("size")),
                int.TryParse(Field("seeders"), out var peers) ? peers : null, Field("infoHash"));
        }).OfType<TorrentRelease>().ToArray();
    }

    private static long? ReadSize(string? value)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts?.Length != 2 || !double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return null;
        var multiplier = parts[1] switch { "Bytes" or "B" => 1d, "KiB" => 1024d, "MiB" => 1048576d, "GiB" => 1073741824d, "TiB" => 1099511627776d, _ => 0d };
        var bytes = number * multiplier;
        return bytes is > 0 and < long.MaxValue ? (long)bytes : null;
    }

    internal sealed record CatalogEntry(string Id, string Name, string Description, string Endpoint, string[] Categories, string? TrackerId = null);
    private sealed record KnabenResult(KnabenHit[]? Hits);
    private sealed record KnabenHit(string? Title, string? Category, long? Bytes, int? Seeders, string? MagnetUrl, string? Hash, string? TrackerId);
}
