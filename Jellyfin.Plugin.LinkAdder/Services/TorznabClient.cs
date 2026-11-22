using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.LinkAdder.Configuration;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.LinkAdder.Services;

internal static partial class TorznabClient
{
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    internal static async Task TestAsync(TorrentProvider provider, CancellationToken ct)
    {
        var xml = await ReadAsync(provider, new() { ["t"] = "caps" }, ct);
        if (xml.Name.LocalName != "caps" || xml.Element("searching")?.Element("search")?.Attribute("available")?.Value != "yes")
        {
            throw new InvalidDataException("The endpoint does not advertise Torznab text search.");
        }
        var search = await ReadAsync(provider, new() { ["t"] = "search", ["q"] = "Sintel", ["limit"] = "1" }, ct);
        if (search.Element("channel") is null)
        {
            throw new InvalidDataException("The provider did not return a Torznab search feed.");
        }
    }

    internal static async Task<TorrentRelease[]> SearchAsync(TorrentProvider provider, string query, string filter, CancellationToken ct)
    {
        var categories = filter switch
        {
            "movie" => "2000",
            "series" => "5000",
            "anime" => "5070",
            "other" => "8000,8010,8020",
            "adult" => "6000,6010,6020,6030,6040,6045,6050,6060,6070,6080,6090",
            _ => "2000,5000"
        };
        var xml = await ReadAsync(provider, new()
        {
            ["t"] = "search", ["q"] = query, ["cat"] = categories, ["extended"] = "1", ["limit"] = "50"
        }, ct);
        var channel = xml.Element("channel") ?? throw new InvalidDataException("The provider did not return a search feed.");
        return channel.Elements("item").Take(50).Select(item => Parse(item, provider.Name, filter))
            .OfType<TorrentRelease>().ToArray();
    }

    private static async Task<XElement> ReadAsync(TorrentProvider provider, Dictionary<string, string?> parameters, CancellationToken ct)
    {
        if (provider.ApiKey.Length > 0) parameters["apikey"] = provider.ApiKey;
        using var response = await Http.GetAsync(QueryHelpers.AddQueryString(provider.Endpoint, parameters), ct);
        var root = await ReadXmlAsync(response, ct);
        if (root.Name.LocalName == "error")
        {
            throw new InvalidDataException("The provider rejected the Torznab request. Check the endpoint and API key.");
        }
        return root;
    }

    internal static async Task<XElement> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2 * 1024 * 1024
        });
        return (await XDocument.LoadAsync(reader, LoadOptions.None, ct)).Root
            ?? throw new InvalidDataException("The provider returned an empty XML document.");
    }

    private static TorrentRelease? Parse(XElement item, string provider, string filter)
    {
        var release = Limit(item.Element("title")?.Value, 350);
        if (release.Length == 0) return null;
        var attributes = item.Elements().Where(x => x.Name.LocalName == "attr").ToArray();
        string? Attribute(string key) => attributes.FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), key, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
        var categories = attributes.Where(x => (string?)x.Attribute("name") == "category").Select(x => (string?)x.Attribute("value"))
            .Concat(item.Elements("category").Select(x => x.Value));
        var categoryIds = categories.Select(x => int.TryParse(x, out var id) ? id : 0).ToArray();
        var adult = categoryIds.Any(id => id is >= 6000 and < 7000);
        if (adult != (filter == "adult")) return null;
        var other = categoryIds.Any(id => id is >= 8000 and < 9000);
        if (filter == "other" && !other) return null;
        var type = filter == "other" ? "other" : adult ? "adult" : categoryIds.Contains(5070) ? "anime" : categoryIds.Any(x => x is >= 2000 and < 3000) ? "movie"
            : categoryIds.Any(x => x is >= 5000 and < 6000) ? "series" : filter;
        if (!string.IsNullOrEmpty(filter) && filter != type) return null;

        var sources = new[] { Attribute("magneturl"), item.Element("link")?.Value, (string?)item.Element("enclosure")?.Attribute("url") };
        var hash = sources.Select(ReadMagnetHash).FirstOrDefault(x => x is not null);
        var poster = Attribute("poster");
        var posterUrl = Uri.TryCreate(poster, UriKind.Absolute, out var image) && image.Scheme == "https" ? image.AbsoluteUri : null;
        var size = long.TryParse(Attribute("size") ?? item.Element("size")?.Value, out var bytes) && bytes > 0 ? (long?)bytes : null;
        var seeders = int.TryParse(Attribute("seeders"), out var peers) && peers >= 0 ? (int?)peers : null;
        return BuildRelease(release, type, provider, size, seeders, hash ?? Attribute("infohash"), Attribute("title"), Attribute("year"), posterUrl);
    }

    internal static TorrentRelease BuildRelease(string release, string type, string provider, long? size, int? seeders,
        string? hash, string? title = null, string? year = null, string? posterUrl = null)
    {
        release = Limit(release, 350);
        var titleSource = type == "anime" ? AnimeGroup().Replace(release, string.Empty) : release;
        var suffix = ReleaseSuffix().Match(titleSource);
        title = Limit(title, 200);
        if (title.Length == 0)
        {
            title = (suffix.Success && suffix.Index > 0 ? titleSource[..suffix.Index] : titleSource)
                .Replace('.', ' ').Replace('_', ' ').Trim();
        }
        var releaseYear = int.TryParse(year ?? Year().Match(suffix.Value).Value, out var parsedYear) ? (int?)parsedYear : null;
        return new TorrentRelease(title.Length > 0 ? title : release, releaseYear, type, release, provider,
            Resolution().Match(release).Value, size is > 0 ? size : null, seeders is >= 0 ? seeders : null,
            hash is not null && InfoHash().IsMatch(hash) ? "magnet:?xt=urn:btih:" + hash.ToLowerInvariant() : null, posterUrl);
    }

    internal static string? ReadMagnetHash(string? magnet)
    {
        if (magnet is null || magnet.Length > 8192 || !magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return null;
        var topics = QueryHelpers.ParseQuery(magnet[7..])["xt"];
        return topics.FirstOrDefault(x => x is not null && x.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)
            && InfoHash().IsMatch(x[9..]))?[9..];
    }

    private static string Limit(string? value, int length)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= length ? value : value[..length];
    }

    [GeneratedRegex(@"\A(?:[a-f0-9]{40}|[a-z2-7]{32})\z", RegexOptions.IgnoreCase)]
    private static partial Regex InfoHash();
    [GeneratedRegex(@"(?<!\d)(?:19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex Year();
    [GeneratedRegex(@"[. _(-]+(?:(?:19\d{2}|20\d{2})|S\d{1,2}(?:E\d{1,3})?|(?:480|576|720|1080|2160)p|4K|WEB[- .]?(?:DL|Rip)|BluRay)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseSuffix();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:480|576|720|1080|2160)p|4K)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex Resolution();
    [GeneratedRegex(@"^(?:\[[^\]]+\]\s*)+")]
    private static partial Regex AnimeGroup();
}

internal sealed record TorrentRelease(string Title, int? Year, string Type, string Release, string Provider,
    string Resolution, long? Size, int? Seeders, string? Magnet, string? PosterUrl);
