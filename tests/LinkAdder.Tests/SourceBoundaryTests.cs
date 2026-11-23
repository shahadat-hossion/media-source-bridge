using System.Net;
using Jellyfin.Plugin.LinkAdder.Configuration;
using Jellyfin.Plugin.LinkAdder.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkAdder.Tests;

public sealed class SourceBoundaryTests
{
    [Fact]
    public async Task FindsAnimeUnderDecoratedAlphabeticalFolders()
    {
        const string Bucket = "/anime/Anime-TV Series ♦  N  —  S/";
        await using var server = CreateServer();
        server.MapGet("/anime/", (HttpContext context) => context.Request.Headers.UserAgent.Count == 0
            ? string.Empty
            : $"<a href=\"{Uri.EscapeDataString(Bucket.Split('/')[2])}/\">N–S</a>");
        server.MapGet(Bucket, () => "<a href=\"Solo%20Leveling/\">Solo Leveling</a>");
        var origin = await StartServerAsync(server);
        var media = new LinkAdderService([new CustomSource { Name = "Anime", MediaType = "anime", Url = origin + "/anime/" }]);

        var results = await media.SearchRemoteAsync("anime", "Solo Leveling", CancellationToken.None, includePosters: false);

        Assert.Equal("Solo Leveling", Assert.Single(results).Name);
    }

    [Theory]
    [InlineData("Better Call Saul", "Better Call Saul")]
    [InlineData("Beter Call Saul", "Better Call Saul")]
    [InlineData("Bettre Call Saul", "Better Call Saul")]
    [InlineData("Better Calk Saul", "Better Call Saul")]
    [InlineData("Better Cally Saul", "Better Call Saul")]
    [InlineData("Bettre Calk Saul", null)]
    public async Task FindsTvTitlesUnderAlphabeticalFoldersWithOneTypo(string query, string? expected)
    {
        const string BucketName = "TV Series ♥  A  —  L";
        await using var server = CreateServer();
        server.MapGet("/tv/", () => $"<a href=\"{Uri.EscapeDataString(BucketName)}/\">A–L</a>");
        server.MapGet($"/tv/{BucketName}/", () =>
            "<a href=\"Better%20Call%20Saul/\">Better Call Saul</a><a href=\"Better%20Call%20Paul/\">Better Call Paul</a>");
        var origin = await StartServerAsync(server);
        var media = new LinkAdderService([new CustomSource { Name = "TV", MediaType = "series", Url = origin + "/tv/" }]);

        var results = await media.SearchRemoteAsync("series", query, CancellationToken.None, includePosters: false);

        Assert.Equal(expected, results.FirstOrDefault()?.Name);
    }

    [Theory]
    [InlineData("http://127.0.0.1/media-other/video.mp4")]
    [InlineData("http://user:secret@127.0.0.1/media/video.mp4")]
    [InlineData("http://127.0.0.1/media/some%2F..%2Fvideo.mp4")]
    [InlineData("http://127.0.0.1/media/some%252fvideo.mp4")]
    [InlineData("http://127.0.0.1:9000/media/video.mp4")]
    public void RejectsUrlsOutsideTheConfiguredSource(string url)
    {
        var service = CreateMedia("http://127.0.0.1/media/");
        Assert.Throws<InvalidOperationException>(() => service.ValidateConfiguredSource(url));
    }

    [Fact]
    public async Task RejectsDirectoryDownloadAndPlaybackRedirects()
    {
        var redirectsFollowed = 0;
        await using var server = CreateServer();
        server.MapGet("/media/", (HttpContext context) => context.Response.Redirect("/private"));
        server.MapGet("/media/video.mp4", (HttpContext context) => context.Response.Redirect("/private"));
        server.MapGet("/private", () => { Interlocked.Increment(ref redirectsFollowed); return "private"; });
        var origin = await StartServerAsync(server);
        var media = CreateMedia(origin + "/media/");
        var ct = CancellationToken.None;

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => media.PreviewAsync(origin + "/media/", "movie", null, ct));
        Assert.Equal(HttpStatusCode.Found, failure.StatusCode);

        var plan = await media.PreviewAsync(origin + "/media/video.mp4", "movie", "Redirect", ct);
        var destination = Directory.CreateTempSubdirectory();
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => media.DownloadAsync(plan, destination.FullName, ct));
            Assert.Empty(destination.GetFiles("*", SearchOption.AllDirectories));
        }
        finally { destination.Delete(true); }

        var playback = new DefaultHttpContext();
        playback.Request.Method = HttpMethods.Get;
        await media.ProxyPlaybackAsync(origin + "/media/video.mp4", playback, ["Range"], ["Content-Length"], ct);
        Assert.Equal(StatusCodes.Status502BadGateway, playback.Response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref redirectsFollowed));
    }

    [Fact]
    public async Task ProxiesByteRangesForConfiguredVideo()
    {
        await using var server = CreateServer();
        server.MapGet("/media/video.mp4", (HttpContext context) =>
        {
            Assert.Equal("bytes=1-2", context.Request.Headers.Range.ToString());
            context.Response.Headers.ContentRange = "bytes 1-2/4";
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            return context.Response.WriteAsync("bc");
        });
        var origin = await StartServerAsync(server);
        var media = CreateMedia(origin + "/media/");
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Range = "bytes=1-2";
        await media.ProxyPlaybackAsync(origin + "/media/video.mp4", context, ["Range"], ["Content-Range"], CancellationToken.None);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 1-2/4", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("bc", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public void ReimportUpgradesOnlyTheMatchingLegacyPointer()
    {
        const string Original = "http://127.0.0.1/media/video.mp4";
        const string Other = "http://127.0.0.1/media/other.mp4";
        const string Signed = "http://127.0.0.1/Plugins/LinkAdder/Playback/signed-ticket";
        var service = CreateMedia("http://127.0.0.1/media/");
        var plan = new Jellyfin.Plugin.LinkAdder.Models.PreviewResult("Film",
            [new(Original, Path.Combine("Movies", "Film", "Film.strm"))]);
        var destination = Directory.CreateTempSubdirectory();
        try
        {
            var legacy = Assert.Single(service.Write(plan, destination.FullName).Created);
            Assert.Equal(Original, File.ReadAllText(legacy).TrimEnd());
            Assert.Single(service.Write(plan, destination.FullName, _ => Signed).Created);
            Assert.Equal(Signed, File.ReadAllText(legacy).TrimEnd());

            File.WriteAllText(legacy, Other);
            Assert.Single(service.Write(plan, destination.FullName, _ => Signed).Skipped);
            Assert.Equal(Other, File.ReadAllText(legacy));
        }
        finally { destination.Delete(true); }
    }

    private static LinkAdderService CreateMedia(string root) => new([new CustomSource
    {
        Name = "Media", Url = root, MediaType = "movie"
    }]);

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static async Task<string> StartServerAsync(WebApplication server)
    {
        await server.StartAsync(CancellationToken.None);
        return server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

}
