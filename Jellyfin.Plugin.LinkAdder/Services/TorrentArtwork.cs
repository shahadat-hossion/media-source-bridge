using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.LinkAdder.Services;

internal static partial class TorrentArtwork
{
    internal static async Task<TorrentRelease[]> EnrichAsync(TorrentRelease[] releases, string query, string filter,
        IProviderManager metadata, IMemoryCache cache, CancellationToken ct)
    {
        var missing = releases.Where(item => string.IsNullOrEmpty(item.PosterUrl) && item.Type != "adult").ToArray();
        if (missing.Length == 0) return releases;

        var title = SearchTitle(query, missing);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var types = missing.Select(item => item.Type).ToHashSet();
        var lookups = new List<Task<Cover[]>>();
        if (types.Contains("movie"))
            lookups.Add(LookupAsync("movie", title, () => SearchMoviesAsync(metadata, title, timeout.Token), cache, ct));
        if (types.Contains("series"))
            lookups.Add(LookupAsync("series", title, () => SearchShowsAsync(metadata, title, timeout.Token), cache, ct));
        if (types.Contains("anime") || types.Contains("series"))
            lookups.Add(LookupAsync("anime", title, () => SearchAnimeAsync(title, timeout.Token), cache, ct));

        var covers = (await Task.WhenAll(lookups)).SelectMany(result => result).ToArray();
        var enriched = releases.Select(release => ApplyCover(release, covers, filter)).ToArray();
        var byMagnet = enriched.Where(release => release.Magnet is not null && !string.IsNullOrEmpty(release.PosterUrl))
            .ToLookup(release => release.Magnet);
        return enriched.Select(release =>
        {
            if (release.Magnet is null || !string.IsNullOrEmpty(release.PosterUrl) || release.Type is not ("series" or "anime"))
                return release;
            var shared = byMagnet[release.Magnet].FirstOrDefault(item => item.Type is "series" or "anime"
                && ReleaseKey(item.Title) == ReleaseKey(release.Title) && (!release.Year.HasValue || release.Year == item.Year));
            return shared is null ? release : release with { Title = shared.Title, Year = shared.Year, PosterUrl = shared.PosterUrl,
                Type = filter.Length == 0 ? shared.Type : release.Type };
        }).ToArray();
    }

    private static async Task<Cover[]> LookupAsync(string type, string title, Func<Task<Cover[]>> fetch, IMemoryCache cache, CancellationToken ct)
    {
        var key = (typeof(TorrentArtwork), type, title.ToUpperInvariant());
        if (cache.TryGetValue<Cover[]>(key, out var cached) && cached is not null) return cached;
        try
        {
            var results = await fetch();
            cache.Set(key, results, results.Length == 0 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(20));
            return results;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static async Task<Cover[]> SearchMoviesAsync(IProviderManager metadata, string title, CancellationToken ct)
    {
        var results = await metadata.GetRemoteSearchResults<Movie, MovieInfo>(
            new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { Name = title } }, ct);
        return results.Take(20).Select(item => new Cover(item.Name, item.Name, "movie", item.ProductionYear, item.ImageUrl)).ToArray();
    }

    private static async Task<Cover[]> SearchShowsAsync(IProviderManager metadata, string title, CancellationToken ct)
    {
        var results = await metadata.GetRemoteSearchResults<Series, SeriesInfo>(
            new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { Name = title } }, ct);
        return results.Take(20).Select(item => new Cover(item.Name, item.Name, "series", item.ProductionYear, item.ImageUrl)).ToArray();
    }

    private static async Task<Cover[]> SearchAnimeAsync(string title, CancellationToken ct)
    {
        const string query = "query ($title: String!) { Page(perPage: 12) { media(search: $title, type: ANIME, isAdult: false) { title { english romaji } startDate { year } coverImage { large } } } }";
        using var response = await TorznabClient.Http.PostAsJsonAsync("https://graphql.anilist.co",
            new { query, variables = new { title } }, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return (json?["data"]?["Page"]?["media"]?.AsArray() ?? []).OfType<JsonObject>().Select(item =>
        {
            var romaji = item["title"]?["romaji"]?.GetValue<string>();
            var english = item["title"]?["english"]?.GetValue<string>();
            return new Cover(english ?? romaji, romaji, "anime", item["startDate"]?["year"]?.GetValue<int>(),
                item["coverImage"]?["large"]?.GetValue<string>());
        }).ToArray();
    }

    private static TorrentRelease ApplyCover(TorrentRelease release, Cover[] covers, string filter)
    {
        if (!string.IsNullOrEmpty(release.PosterUrl) || release.Type == "adult") return release;
        var key = ReleaseKey(release.Title);
        var exact = covers.Where(item => (release.Type == item.Type || (release.Type == "series" && item.Type == "anime"))
                && (!release.Year.HasValue || !item.Year.HasValue || release.Year == item.Year)
                && (Key(item.Name) == key || Key(item.Alternate) == key)
                && Uri.TryCreate(item.Url, UriKind.Absolute, out var url) && url.Scheme == "https")
            .DistinctBy(item => (Key(item.Name), item.Year, item.Type)).ToArray();
        if (exact.Length == 0 || exact.Select(item => item.Year).OfType<int>().Distinct().Skip(1).Any()) return release;
        var cover = exact.OrderByDescending(item => item.Type == "anime").First();
        return release with { Title = cover.Name ?? release.Title, Year = release.Year ?? cover.Year,
            Type = filter.Length == 0 ? cover.Type : release.Type, PosterUrl = cover.Url };
    }

    private static string SearchTitle(string query, TorrentRelease[] releases)
    {
        var target = Key(query);
        var words = CamelBoundary().Replace(query, " ");
        if (words.Contains(' ')) return words;
        foreach (var release in releases)
        {
            var title = ReleaseSuffix().Split(LeadingTags().Replace(release.Title, string.Empty), 2)[0];
            var spaced = CamelBoundary().Replace(title.Trim(), " ");
            if (spaced.Contains(' ') && Key(spaced) == target) return spaced;
        }
        return query;
    }

    private static string ReleaseKey(string title) => Key(ReleaseSuffix().Split(LeadingTags().Replace(title, string.Empty), 2)[0]);
    private static string Key(string? value) => new((value ?? string.Empty).Normalize().Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"(?<=\p{Ll})(?=\p{Lu})")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"^(?:\[[^\]]+\]\s*)+")]
    private static partial Regex LeadingTags();

    [GeneratedRegex(@"\s*[[(]|[\s._-]+(?:complete|all\s+episodes|episodes?|seasons?|batch|\d{3,4}p|dual[\s-]+audio)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseSuffix();

    private sealed record Cover(string? Name, string? Alternate, string Type, int? Year, string? Url);
}
