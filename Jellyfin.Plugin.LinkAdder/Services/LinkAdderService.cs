using Microsoft.AspNetCore.Http;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LinkAdder.Models;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LinkAdder.Configuration;

namespace Jellyfin.Plugin.LinkAdder.Services;
public sealed partial class LinkAdderService
{
    private sealed record IndexedTitle(string Name, string SearchName, string Url, string Source, RemoteRoot Root);
    private static readonly MemoryCache SearchLinkCache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private static readonly MemoryCache SearchIndexes = new(new MemoryCacheOptions { SizeLimit = 12 });
    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SearchIndexDuration = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".webm", ".wmv", ".mpg", ".mpeg" };
    private const int MaxListingBytes = 2 * 1024 * 1024;
    private const int MaxListingEntries = 4096;
    private const int MaxImportFiles = 1000;
    private const int MaxIndexedTitlesPerRoot = 10_000;
    private const int MaxIndexedTitles = 50_000;
    private static readonly SemaphoreSlim ListingSlots = new(32);
    private const long MaxDownloadBytes = 50L * 1024 * 1024 * 1024;
    private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient SourceHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CustomSource[] _customSources;
    private sealed record RemoteRoot(string Name, Uri Url, string Username = "", string Password = "");
    public LinkAdderService(IEnumerable<CustomSource>? customSources = null) =>
        _customSources = (customSources ?? []).ToArray();

    private static HttpClientHandler SourceHandler(RemoteRoot root) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = root.Username.Length == 0 ? null : new CredentialCache
        {
            { root.Url, "Basic", new NetworkCredential(root.Username, root.Password) },
            { root.Url, "Digest", new NetworkCredential(root.Username, root.Password) },
            { root.Url, "NTLM", new NetworkCredential(root.Username, root.Password) },
            { root.Url, "Negotiate", new NetworkCredential(root.Username, root.Password) }
        }
    };

    public async Task<IReadOnlyList<RemoteSearchResult>> SearchRemoteAsync(string mediaType,string query,CancellationToken ct,bool includePosters=true)
    {
        query=(query??string.Empty).Trim();
        if(query.Length is < 2 or > 200)throw new InvalidOperationException("Enter between 2 and 200 characters.");
        var roots=RemoteRoots(mediaType);
        if(roots.Length==0)throw new InvalidOperationException("Add a local ISP media source for this media type first.");
        var normalizedQuery=NormalizeSearchText(query);
        if (normalizedQuery.Length == 0) return [];
        var queryTokens=normalizedQuery.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var index=await GetSearchIndexAsync(mediaType,roots,ct).ConfigureAwait(false);
        var matches=index.Select(item=>new { Item=item, Score=SearchScore(item.SearchName,normalizedQuery,queryTokens) })
            .Where(x=>x.Score>0)
            .OrderByDescending(x=>x.Score)
            .ThenBy(x=>x.Item.Name,StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select(x=>x.Item)
            .ToArray();
        if(!includePosters)return matches.Select(item=>new RemoteSearchResult(item.Name,item.Url,item.Source,string.Empty)).ToArray();
        return await AddPostersAsync(matches,ct).ConfigureAwait(false);
    }

    public async Task WarmSearchIndexesAsync(CancellationToken ct)
    {
        var warmups=new[]{"movie","series","anime"}.Select(async mediaType=>
        {
            var roots=RemoteRoots(mediaType);
            if(roots.Length==0)return;
            try{await GetSearchIndexAsync(mediaType,roots,ct).ConfigureAwait(false);}
            catch when(!ct.IsCancellationRequested){}
        });
        await Task.WhenAll(warmups).ConfigureAwait(false);
    }

    public async Task<int> TestConnectionAsync(string sourceUrl,string? username,string? password,CancellationToken ct)
    {
        if(!Uri.TryCreate(sourceUrl?.Trim(),UriKind.Absolute,out var uri) || !IsSafeSourceUri(uri))throw new InvalidOperationException("Use a complete HTTP, HTTPS, or FTP URL.");
        var root=new RemoteRoot("Connection test",uri.AbsolutePath.EndsWith('/')?uri:new Uri(uri.AbsoluteUri+"/"),(username??string.Empty).Trim(),password??string.Empty);
        return (await ListLinksAsync(root.Url,root,ct).ConfigureAwait(false)).Count;
    }
    public async Task<PreviewResult> PreviewAsync(string sourceUrl,string mediaType,string? requestedTitle,CancellationToken ct)
    {
        if (requestedTitle?.Length > 200) throw new InvalidOperationException("Keep the title under 201 characters.");
        if(mediaType=="anime")mediaType="series";
        var source=ValidateConfiguredSource(sourceUrl); IReadOnlyList<Uri> videos=IsVideo(source)?[source]:await CrawlVideosAsync(source.AbsolutePath.EndsWith('/') ? source : new UriBuilder(source) { Path = source.AbsolutePath + "/" }.Uri,ct).ConfigureAwait(false); if(videos.Count==0) throw new InvalidOperationException("No supported video files were found."); var sourceTitle = string.IsNullOrWhiteSpace(requestedTitle) ? InferTitle(source, mediaType) : requestedTitle;
        var title = NormalizeImportTitle(sourceTitle, mediaType);
        return new(title, MakePlan(videos, mediaType, title, GetDefaultSeason(sourceTitle)));
    }

    public AddResult Write(PreviewResult preview, string outputRoot, Func<string, string>? playbackUrl = null,
        Action<int, int>? progress = null)
    {
        var created = new List<string>();
        var skipped = new List<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        foreach (var item in preview.Files)
        {
            var parts = item.RelativePath.Split(Path.DirectorySeparatorChar, 2);
            if (parts.Length != 2) throw new InvalidOperationException("Invalid planned path.");
            var destination = Path.GetFullPath(Path.Combine(root, parts[1]));
            if (!destination.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe output path rejected.");
            var target = playbackUrl?.Invoke(item.Url) ?? item.Url;
            var exists = File.Exists(destination);
            // Re-importing upgrades an exact legacy pointer without replacing another source.
            var oldUrl = playbackUrl is not null ? item.Url
                : target.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && target.Contains("?ticket=", StringComparison.Ordinal)
                    ? target[..target.IndexOf("?ticket=", StringComparison.Ordinal)] : target;
            var upgrade = exists && oldUrl != target && new FileInfo(destination).Length <= 4096
                && string.Equals(File.ReadAllText(destination).TrimEnd('\r', '\n'), oldUrl, StringComparison.Ordinal);
            if (exists && !upgrade)
            {
                skipped.Add(destination);
                progress?.Invoke(created.Count, skipped.Count);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                File.WriteAllText(temporary, target + Environment.NewLine);
                File.Move(temporary, destination, overwrite: upgrade);
                created.Add(destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                skipped.Add(destination);
            }
            finally
            {
                File.Delete(temporary);
            }
            progress?.Invoke(created.Count, skipped.Count);
        }
        return new(created,skipped,"Library scan requested.")
        {
            Title = preview.Title,
            TitlePath = Path.Combine(root, preview.Title)
        };
    }

    internal static PreviewResult PlanTorrentStreams(string title, string mediaType, IReadOnlyList<Uri> videos)
    {
        var name = NormalizeImportTitle(title, mediaType);
        return new PreviewResult(name, MakePlan(videos, mediaType == "anime" ? "series" : mediaType, name, GetDefaultSeason(title)));
    }

    public async Task<AddResult> DownloadAsync(PreviewResult preview,string outputRoot,CancellationToken ct,Action<double,string>? progress=null)
    {
        var created=new List<string>(); var skipped=new List<string>();
        var remainingBytes = MaxDownloadBytes;
        for(var index=0;index<preview.Files.Count;index++)
        {
            var item=preview.Files[index];
            ct.ThrowIfCancellationRequested();
            var source=ValidateConfiguredSource(item.Url);
            if(!IsVideo(source))throw new InvalidOperationException("The selected source contains an unsupported video file.");
            var parts=item.RelativePath.Split(Path.DirectorySeparatorChar,2);
            if(parts.Length!=2)throw new InvalidOperationException("Invalid planned path.");
            var root=Path.GetFullPath(outputRoot);
            var relative=Path.ChangeExtension(parts[1],Path.GetExtension(WebUtility.UrlDecode(source.AbsolutePath)));
            var destination=Path.GetFullPath(Path.Combine(root,relative));
            if(!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe output path rejected.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if(File.Exists(destination)){skipped.Add(destination);progress?.Invoke(100d*(index+1)/preview.Files.Count,$"Skipped existing file {index+1} of {preview.Files.Count}.");continue;}
            var name = Path.GetFileName(destination);
            foreach (var orphan in Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.linkadder-part"))
            {
                var orphanName = Path.GetFileName(orphan);
                if (orphanName.Length == name.Length + 48 && orphanName.StartsWith(name + ".", StringComparison.Ordinal)
                    && Guid.TryParseExact(orphanName.AsSpan(name.Length + 1, 32), "N", out _)
                    && File.GetLastWriteTimeUtc(orphan) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(orphan);
            }

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".linkadder-part";
            try
            {
                var fileNumber=index+1;
                remainingBytes -= await DownloadFileAsync(source,temporary,remainingBytes,ct,(fraction,fileName)=>progress?.Invoke(100d*(index+fraction)/preview.Files.Count,$"Downloading {fileNumber} of {preview.Files.Count}: {fileName}")).ConfigureAwait(false);
                File.Move(temporary,destination);
                created.Add(destination);
                progress?.Invoke(100d*(index+1)/preview.Files.Count,$"Downloaded {fileNumber} of {preview.Files.Count}.");
            }
            finally
            {
                if(File.Exists(temporary))File.Delete(temporary);
            }
        }
        return new(created,skipped,"Library scan requested.");
    }

    internal async Task ProxyPlaybackAsync(string value, HttpContext context, string[] forwardedHeaders,
        string[] responseHeaders, CancellationToken ct)
    {
        var source = ValidateConfiguredSource(value);
        if (source.Scheme == Uri.UriSchemeFtp || !IsVideo(source)) throw new InvalidOperationException("Invalid stream.");
        var root = FindRoot(source);
        using var client = root.Username.Length == 0 ? null : new HttpClient(SourceHandler(root));
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), source);
        foreach (var name in forwardedHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var values))
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(SourceHttp.Timeout);
        using var response = await (client ?? SourceHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            headerTimeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is not (200 or 206 or 416))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        foreach (var name in responseHeaders)
        {
            if (response.Content.Headers.TryGetValues(name, out var values) || response.Headers.TryGetValues(name, out values))
                context.Response.Headers[name] = values.ToArray();
        }
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await CopyWithProgressAsync(stream, context.Response.Body, -1, string.Empty, ct, null).ConfigureAwait(false);
    }

    private async Task<long> DownloadFileAsync(Uri source, string destination, long maxBytes, CancellationToken ct, Action<double,string>? progress)
    {
        var root=FindRoot(source);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*128,useAsync:true);
        if(source.Scheme=="ftp")
        {
#pragma warning disable SYSLIB0014
            var request=(FtpWebRequest)WebRequest.Create(source);request.Method=WebRequestMethods.Ftp.DownloadFile;request.Proxy=null;request.UsePassive=true;request.UseBinary=true;request.KeepAlive=false;
            request.Credentials=root.Username.Length>0?new NetworkCredential(root.Username,root.Password):new NetworkCredential("anonymous","anonymous@");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TransferIdleTimeout);
            using var registration = timeout.Token.Register(request.Abort);
            using var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            return await CopyWithProgressAsync(response.GetResponseStream(), output, response.ContentLength,
                Path.GetFileName(WebUtility.UrlDecode(source.AbsolutePath)), timeout.Token, progress, maxBytes).ConfigureAwait(false);
#pragma warning restore SYSLIB0014
        }
        using var client = new HttpClient(SourceHandler(root)) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Media-Source-Bridge/2.6");
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(TransferIdleTimeout);
        using var responseMessage = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead,
            headerTimeout.Token).ConfigureAwait(false);
        responseMessage.EnsureSuccessStatusCode();
        await using var input=await responseMessage.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await CopyWithProgressAsync(input, output, responseMessage.Content.Headers.ContentLength ?? -1,
            Path.GetFileName(WebUtility.UrlDecode(source.AbsolutePath)), ct, progress, maxBytes).ConfigureAwait(false);
    }

    private static async Task<long> CopyWithProgressAsync(Stream input, Stream output, long length, string fileName,
        CancellationToken ct, Action<double, string>? progress, long maxBytes = long.MaxValue)
    {
        const string LimitMessage = "The transfer exceeds the download size limit.";
        if (length > maxBytes) throw new InvalidOperationException(LimitMessage);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            idle.CancelAfter(TransferIdleTimeout);
            var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > maxBytes - copied) throw new InvalidOperationException(LimitMessage);
            idle.CancelAfter(TransferIdleTimeout);
            await output.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
            copied += count;
            if (length > 0) progress?.Invoke(Math.Min(1d, (double)copied / length), fileName);
        }
        if (length >= 0 && copied != length) throw new IOException("The transfer ended before the advertised length.");
        return copied;
    }

    private static async Task<T> GetCachedAsync<T>(MemoryCache cache, object key, TimeSpan lifetime,
        Func<Task<T>> load, CancellationToken ct)
    {
        Lazy<Task<T>> entry;
        lock (cache)
        {
            if (cache.Get<Lazy<Task<T>>>(key) is { } existing) entry = existing;
            else
            {
                entry = new Lazy<Task<T>>(load);
                cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
            }
        }
        try
        {
            return await entry.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            lock (cache)
            {
                if (ReferenceEquals(cache.Get<Lazy<Task<T>>>(key), entry)) cache.Remove(key);
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<IndexedTitle>> GetSearchIndexAsync(string mediaType,RemoteRoot[] roots,CancellationToken ct)
    {
        var signature = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(roots)));
        var key=$"{mediaType}\u001e{signature}";
        return await GetCachedAsync<IReadOnlyList<IndexedTitle>>(SearchIndexes, key, SearchIndexDuration, async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var titles = new Dictionary<string, IndexedTitle>(StringComparer.Ordinal);
            foreach (var batch in roots.Chunk(4))
            {
                if (deadline.IsCancellationRequested || titles.Count == MaxIndexedTitles) break;
                var groups = await Task.WhenAll(batch.Select(root => IndexRootWithTimeoutAsync(root, mediaType, deadline.Token)))
                    .ConfigureAwait(false);
                foreach (var item in groups.SelectMany(group => group))
                {
                    titles.TryAdd(item.Url, item);
                    if (titles.Count == MaxIndexedTitles) break;
                }
            }
            return titles.Values.ToArray();
        }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IndexedTitle>> IndexRootWithTimeoutAsync(RemoteRoot root,string mediaType,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await IndexRootAsync(root,mediaType,timeout.Token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IndexedTitle>> IndexRootAsync(RemoteRoot root,string mediaType,CancellationToken ct)
    {
        var results=new List<IndexedTitle>();
        var visited=new HashSet<string>(StringComparer.Ordinal);
        var pages=new[]{root.Url};
        var isFtp = root.Url.Scheme == Uri.UriSchemeFtp;
        using var concurrency=new SemaphoreSlim(8,8);
        for(var depth=0;depth<=2&&pages.Length>0&&visited.Count<300&&!ct.IsCancellationRequested;depth++)
        {
            var remaining=300-visited.Count;
            var current=pages.Where(page=>!visited.Contains(page.AbsoluteUri)).Take(remaining).ToArray();
            foreach (var page in current) visited.Add(page.AbsoluteUri);
            var loads=current.Select(async page=>
            {
                try
                {
                    await concurrency.WaitAsync(ct).ConfigureAwait(false);
                    try { return await ListSearchLinksAsync(page, root, ct).ConfigureAwait(false); }
                    finally { concurrency.Release(); }
                }
                catch (Exception ex) when (ex is WebException or HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                    if (ex is WebException webException) webException.Response?.Dispose();
                    return (IReadOnlyList<Uri>)[];
                }
            });
            var loaded=await Task.WhenAll(loads).ConfigureAwait(false);
            var next=new List<Uri>();
            var otherFtpFolders = new List<Uri>();
            foreach(var group in loaded)
            {
                foreach(var link in group)
                {
                    if (results.Count == MaxIndexedTitlesPerRoot) return results;
                    if (!IsWithin(root.Url, link)) continue;
                    var name = Uri.UnescapeDataString(Path.GetFileName(link.AbsolutePath.TrimEnd('/')));
                    if (name is "" or "." or ".." || ExtraRegex().IsMatch(name)) continue;
                    if (IsVideo(link))
                    {
                        var fileTitle = InferTitle(link, mediaType == "movie" ? "movie" : "series");
                        results.Add(new(fileTitle, NormalizeSearchText(fileTitle), link.AbsoluteUri, root.Name, root));
                        continue;
                    }
                    if (!link.AbsolutePath.EndsWith('/')) continue;
                    var structural=IsStructuralFolder(name);
                    if(!structural)results.Add(new(name,NormalizeSearchText(CleanName(name)),link.AbsoluteUri,root.Name,root));
                    if(depth<2&&structural)next.Add(link);
                    else if (depth < 2 && isFtp) otherFtpFolders.Add(link);
                }
            }
            pages=next.Concat(otherFtpFolders).DistinctBy(x=>x.AbsoluteUri,StringComparer.Ordinal).ToArray();
        }
        return results;
    }

    private async Task<IReadOnlyList<RemoteSearchResult>> AddPostersAsync(IReadOnlyList<IndexedTitle> matches,CancellationToken ct)
    {
        using var concurrency=new SemaphoreSlim(8,8);
        var tasks=matches.Select(async (item,index)=>
        {
            if(index>=24 || item.Root.Url.Scheme == Uri.UriSchemeFtp || IsVideo(new Uri(item.Url)))return new RemoteSearchResult(item.Name,item.Url,item.Source,string.Empty);
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            try{return new RemoteSearchResult(item.Name,item.Url,item.Source,await FindPosterAsync(new Uri(item.Url),item.Root,ct).ConfigureAwait(false));}
            finally{concurrency.Release();}
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static int SearchScore(string normalizedName,string normalizedQuery,IReadOnlyList<string> queryTokens)
    {
        if(normalizedName.Equals(normalizedQuery,StringComparison.OrdinalIgnoreCase))return 1000;
        if(normalizedName.StartsWith(normalizedQuery+" ",StringComparison.OrdinalIgnoreCase))return 900;
        if(normalizedName.Contains(" "+normalizedQuery+" ",StringComparison.OrdinalIgnoreCase)||normalizedName.EndsWith(" "+normalizedQuery,StringComparison.OrdinalIgnoreCase))return 800;
        var nameTokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryTokens.All(token => nameTokens.Contains(token))) return 600;
        if (queryTokens.All(token => nameTokens.Any(word => word == token || (token.Length >= 2 && word.StartsWith(token, StringComparison.Ordinal))))) return 500;
        if(normalizedName.Contains(normalizedQuery,StringComparison.OrdinalIgnoreCase))return 400;
        for (var start = 0; start <= nameTokens.Length - queryTokens.Count; start++)
        {
            var typoCount = 0;
            var offset = 0;
            for (; offset < queryTokens.Count; offset++)
            {
                var token = queryTokens[offset];
                var word = nameTokens[start + offset];
                if (token == word) continue;
                if (typoCount != 0 || token.Length < 4 || !IsSingleEdit(token, word)) break;
                typoCount++;
            }
            if (offset == queryTokens.Count) return start == 0 ? 300 : 250;
        }
        return 0;
    }

    private static bool IsSingleEdit(ReadOnlySpan<char> query, ReadOnlySpan<char> title)
    {
        if (Math.Abs(query.Length - title.Length) > 1) return false;
        if (query.Length > title.Length) return IsSingleEdit(title, query);

        var mismatch = 0;
        while (mismatch < query.Length && query[mismatch] == title[mismatch]) mismatch++;
        if (query.Length < title.Length) return query[mismatch..].SequenceEqual(title[(mismatch + 1)..]);
        if (mismatch == query.Length) return true;
        if (query[(mismatch + 1)..].SequenceEqual(title[(mismatch + 1)..])) return true;

        var next = mismatch + 1;
        return next < query.Length && query[mismatch] == title[next] && query[next] == title[mismatch]
            && query[(next + 1)..].SequenceEqual(title[(next + 1)..]);
    }

    private static string NormalizeSearchText(string value)
    {
        var text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text=SearchPunctuationRegex().Replace(text," ");
        return SearchSpaceRegex().Replace(text," ").Trim();
    }

    private async Task<string> FindPosterAsync(Uri folder,RemoteRoot root,CancellationToken ct)
    {
        try{var image=(await ListSearchLinksAsync(folder,root,ct).ConfigureAwait(false)).FirstOrDefault(link=>IsWithin(root.Url, link)&&!link.AbsolutePath.StartsWith("/_h5ai/",StringComparison.OrdinalIgnoreCase)&&PosterRegex().IsMatch(link.AbsolutePath));return image?.Scheme is "http" or "https"?image.AbsoluteUri:string.Empty;}catch(WebException){return string.Empty;}catch(HttpRequestException){return string.Empty;}catch(TaskCanceledException) when(!ct.IsCancellationRequested){return string.Empty;}
    }

    private static bool IsStructuralFolder(string name) =>
        StructuralRegex().IsMatch(name) || YearResolutionFolderRegex().IsMatch(name) || SeriesRangeFolderRegex().IsMatch(name);

    private RemoteRoot[] RemoteRoots(string mediaType)
    {
        if(mediaType is not("movie" or "series" or "anime"))throw new InvalidOperationException("Choose Movies, Shows, or Anime.");
        return _customSources
            .Where(source => source.MediaType == mediaType || (source.MediaType == "both" && mediaType is "movie" or "series"))
            .Select(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var url) && IsSafeSourceUri(url)
                && IPAddress.TryParse(url.Host, out var address) && IsPrivate(address)
                    ? new RemoteRoot(source.Name, url, source.Username, source.Password) : null)
            .OfType<RemoteRoot>().ToArray();
    }

    private async Task<IReadOnlyList<Uri>> CrawlVideosAsync(Uri root,CancellationToken ct)
    {
        var videos = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Uri Page, int Depth)>();
        var source = FindRoot(root);
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(entry.Page.AbsoluteUri)) continue;
            if (visited.Count > 500) throw new InvalidOperationException("This folder is too large. Select a more specific movie or series folder.");
            IReadOnlyList<Uri> links;
            try
            {
                links = await ListSearchLinksAsync(entry.Page, source, ct).ConfigureAwait(false);
            }
            catch (WebException ex) when (entry.Depth > 0 && ex.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable })
            {
                // FTP name-only listings can report sidecar files as potential folders.
                ex.Response?.Dispose();
                continue;
            }
            foreach (var link in links)
            {
                if (!IsWithin(root, link)) continue;
                var name = Uri.UnescapeDataString(Path.GetFileName(link.AbsolutePath.TrimEnd('/')));
                if (ExtraRegex().IsMatch(name)) continue;
                if (IsVideo(link))
                {
                    videos.Add(link.AbsoluteUri);
                    if (videos.Count > MaxImportFiles) throw new InvalidOperationException("Select a smaller folder to import.");
                }
                else if (entry.Depth < 6 && link.AbsolutePath.EndsWith('/'))
                {
                    if (queue.Count >= 500) throw new InvalidOperationException("Select a smaller folder to import.");
                    queue.Enqueue((link, entry.Depth + 1));
                }
            }
        }
        return videos.Order(StringComparer.Ordinal).Select(url => new Uri(url)).ToArray();
    }

    public Uri ValidateConfiguredSource(string value)
    {
        if(value is null || value.Length > 2048 || !Uri.TryCreate(value.Trim(),UriKind.Absolute,out var uri) || !IsSafeSourceUri(uri)) throw new InvalidOperationException("Use a complete HTTP, HTTPS, or FTP link.");
        if(!IPAddress.TryParse(uri.Host,out var address) || !IsPrivate(address)) throw new InvalidOperationException("Only local-server IP links are allowed.");
        if(!RemoteRoots("movie").Concat(RemoteRoots("series")).Concat(RemoteRoots("anime")).Any(x=>IsWithin(x.Url,uri)||x.Url.Equals(uri))) throw new InvalidOperationException("The link is not inside a configured media source.");return uri;
    }

    private RemoteRoot FindRoot(Uri value)=>RemoteRoots("movie").Concat(RemoteRoots("series")).Concat(RemoteRoots("anime")).OrderByDescending(x=>x.Url.AbsolutePath.Length).First(x=>IsWithin(x.Url,value)||x.Url.Equals(value));
    private async Task<IReadOnlyList<Uri>> ListLinksAsync(Uri folder,RemoteRoot root,CancellationToken ct)
    {
        if (folder.Scheme != "ftp")
        {
            using var httpTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            httpTimeout.CancelAfter(SourceHttp.Timeout);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, folder);
            httpRequest.Headers.UserAgent.ParseAdd("MediaSourceBridge/2.7");
            using var client = root.Username.Length == 0 ? null : new HttpClient(SourceHandler(root));
            using var httpResponse = await (client ?? SourceHttp).SendAsync(httpRequest,
                HttpCompletionOption.ResponseHeadersRead, httpTimeout.Token).ConfigureAwait(false);
            httpResponse.EnsureSuccessStatusCode();
            await httpResponse.Content.LoadIntoBufferAsync(MaxListingBytes, httpTimeout.Token).ConfigureAwait(false);
            var html = await httpResponse.Content.ReadAsStringAsync(httpTimeout.Token).ConfigureAwait(false);
            return ExtractLinks(folder, html).Take(MaxListingEntries).ToArray();
        }
#pragma warning disable SYSLIB0014
        var request=(FtpWebRequest)WebRequest.Create(folder);request.Method=WebRequestMethods.Ftp.ListDirectory;request.Proxy=null;request.UsePassive=true;request.UseBinary=true;request.KeepAlive=false;if(root.Username.Length>0)request.Credentials=new NetworkCredential(root.Username,root.Password);else request.Credentials=new NetworkCredential("anonymous","anonymous@");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourceHttp.Timeout);
        using var registration = timeout.Token.Register(request.Abort);
        using var response = (FtpWebResponse)await request.GetResponseAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(response.GetResponseStream());
        var listing = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (listing.Length + count > MaxListingBytes)
                throw new InvalidOperationException("The source directory listing is too large.");
            listing.Append(buffer, 0, count);
        }

        var links = new List<Uri>();
        using var lines = new StringReader(listing.ToString());
        while (lines.ReadLine() is { } raw)
        {
            if (links.Count >= MaxListingEntries)
                throw new InvalidOperationException("The source directory listing is too large.");
            var name = Path.GetFileName(raw.TrimEnd('/'));
            if (name is "" or "." or "..") continue;
            var extension = Path.GetExtension(name);
            if (SidecarExtensions.Contains(extension)) continue;
            var isFile = VideoExtensions.Contains(extension) || PosterRegex().IsMatch(name);
            links.Add(new Uri(folder, Uri.EscapeDataString(name) + (isFile ? string.Empty : "/")));
        }
        return links;
#pragma warning restore SYSLIB0014
    }
    private Task<IReadOnlyList<Uri>> ListSearchLinksAsync(Uri folder, RemoteRoot root, CancellationToken ct) =>
        GetCachedAsync(SearchLinkCache, (root, folder.AbsoluteUri), SearchCacheDuration,
            async () =>
            {
                using var timeout = new CancellationTokenSource(SourceHttp.Timeout);
                await ListingSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
                try { return await ListLinksAsync(folder, root, timeout.Token).ConfigureAwait(false); }
                finally { ListingSlots.Release(); }
            }, ct);

    private static IEnumerable<Uri> ExtractLinks(Uri page,string body){foreach(Match m in HrefRegex().Matches(body)){var href=WebUtility.HtmlDecode(m.Groups[1].Value);if(Uri.TryCreate(page,href,out var link)&&(link.Scheme=="http"||link.Scheme=="https"))yield return new UriBuilder(link){Fragment=string.Empty}.Uri;}}
    private static bool IsWithin(Uri root, Uri value) => IsSafeSourceUri(value)
        && value.Scheme == root.Scheme && value.Host.Equals(root.Host, StringComparison.OrdinalIgnoreCase)
        && value.Port == root.Port && value.AbsolutePath.StartsWith(
            root.AbsolutePath.EndsWith('/') ? root.AbsolutePath : root.AbsolutePath + '/', StringComparison.Ordinal);

    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static bool IsSafeSourceUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https" or "ftp") || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 2048) return false;
        foreach (var segment in uri.AbsolutePath.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\')
                 || EncodedOctetRegex().IsMatch(decoded) || decoded.Any(char.IsControl)) return false;
        }
        return true;
    }
    private static bool IsVideo(Uri uri)=>!uri.AbsolutePath.EndsWith('/') && VideoExtensions.Contains(Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)));
    private static IReadOnlyList<PlannedFile> MakePlan(IReadOnlyList<Uri> videos,string mediaType,string title,int defaultSeason)
    {
        if (mediaType is not ("movie" or "series")) throw new InvalidOperationException("Choose Movie or Series.");
        var result = new List<PlannedFile>();
        foreach (var video in videos)
        {
            var path = Uri.UnescapeDataString(video.AbsolutePath);
            var raw = Path.GetFileNameWithoutExtension(path);
            if (mediaType == "movie")
            {
                var name = videos.Count == 1 ? title : CleanName(raw);
                result.Add(new(video.AbsoluteUri, Path.Combine("Movies", title, name + ".strm")));
                continue;
            }

            var seasonFolder = SeasonRegex().Matches(path).LastOrDefault();
            var season = seasonFolder is null ? defaultSeason : int.Parse(seasonFolder.Groups[1].Value);
            var match = EpisodeRegex().Match(raw);
            var code = CleanName(raw);
            if (match.Success)
            {
                season = int.Parse(match.Groups[1].Value);
                code = $"S{season:00}E{int.Parse(match.Groups[2].Value):00}";
                if (match.Groups[3].Success) code += $"-E{int.Parse(match.Groups[3].Value):00}";
            }
            else
            {
                var numbered = NumberedEpisodeRegex().Match(code);
                if (numbered.Success) code = $"S{season:00}E{int.Parse(numbered.Groups[1].Value):00}";
            }
            result.Add(new(video.AbsoluteUri, Path.Combine("Shows", title, $"Season {season:00}", $"{title} - {code}.strm")));
        }
        return result.DistinctBy(file => file.RelativePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }
    private static string InferTitle(Uri source,string mediaType){var path=Uri.UnescapeDataString(source.AbsolutePath).TrimEnd('/');var candidate=IsVideo(source)?Path.GetFileNameWithoutExtension(path):Path.GetFileName(path);if(mediaType=="series"&&candidate.StartsWith("Season",StringComparison.OrdinalIgnoreCase))candidate=Path.GetFileName(Path.GetDirectoryName(path)??path);candidate=CleanName(candidate??"Untitled");var m=EpisodeRegex().Match(candidate);return mediaType=="series"&&m.Success&&m.Index>0?CleanName(candidate[..m.Index]):candidate;}
    private static int GetDefaultSeason(string title)
    {
        var withoutIdentifiers = ProviderIdentifierRegex().Replace(title, string.Empty).Trim();
        var normalized = CatalogYearRegex().Replace(withoutIdentifiers, " (${year})");
        return int.TryParse(SeriesSeasonSuffixRegex().Match(normalized).Groups[1].Value, out var season) ? season : 1;
    }

    internal static string NormalizeImportTitle(string value, string mediaType)
    {
        var identifiers = ProviderIdentifierRegex().Matches(value).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase);
        var title = CleanName(ProviderIdentifierRegex().Replace(value, string.Empty));
        title = CatalogYearRegex().Replace(title, " (${year})");
        if (mediaType is "series" or "anime")
        {
            title = SeriesSeasonSuffixRegex().Replace(title, string.Empty).Trim();
        }

        return string.Join(' ', new[] { title }.Concat(identifiers));
    }

    [GeneratedRegex(@"\[(?:(?:tmdbid|tvdbid)-[1-9]\d*|imdbid-tt\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderIdentifierRegex();

    [GeneratedRegex(@"\s*\(\s*(?:(?:TV\s+(?:Cartoon|Series|Mini[- ]?Series|Movie|Special|Short)|Movie|Video)\s+)?(?<year>(?:19|20)\d{2})(?:\s*[-–—]\s*(?:(?:19|20)\d{2}|Present)?)?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogYearRegex();

    [GeneratedRegex(@"\s+(?:-\s+)?Season\s+(\d{1,2})(?=\s*(?:\(\d{4}\))?$)", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesSeasonSuffixRegex();

    private static string CleanName(string value){var x=DotRegex().Replace(value," ");x=QualityRegex().Replace(x,"");x=InvalidRegex().Replace(x," ");x=SpaceRegex().Replace(x," ").Trim(' ','.','-');return string.IsNullOrWhiteSpace(x)?"Untitled":x;}
    [GeneratedRegex(@"%[0-9a-fA-F]{2}")]
    private static partial Regex EncodedOctetRegex();
    [GeneratedRegex("href\\s*=\\s*[\\\"']([^\\\"']+)[\\\"']",RegexOptions.IgnoreCase)]private static partial Regex HrefRegex();
    [GeneratedRegex(@"(?:^|[. _-])S?(\d{1,2})(?:[. _-]*E|x)(\d{1,3})(?:(?:[. _-]*E|\s*-\s*)(\d{1,3})(?!\d))?(?!\d)",RegexOptions.IgnoreCase)]private static partial Regex EpisodeRegex();
    [GeneratedRegex(@"/(?:Season[ ._-]*|S)(\d{1,2})(?=/)",RegexOptions.IgnoreCase)]private static partial Regex SeasonRegex();
    [GeneratedRegex(@"(?:^|\s+-\s+|\bEP(?:ISODE)?[ ._-]*)(\d{1,3})(?:v\d+)?(?=\s*(?:[\[(]|$))",RegexOptions.IgnoreCase)]private static partial Regex NumberedEpisodeRegex();
    [GeneratedRegex("[._]+") ]private static partial Regex DotRegex();
    [GeneratedRegex("\\b(2160p|1080p|720p|480p|web[- ]?dl|webrip|bluray|brrip|hdrip|dvdrip|x26[45]|hevc|avc|aac|esub|msubs?).*$",RegexOptions.IgnoreCase)]private static partial Regex QualityRegex();
    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1f]""")]private static partial Regex InvalidRegex();
    [GeneratedRegex("\\s+")]private static partial Regex SpaceRegex();
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".nfo", ".srt", ".ass", ".ssa", ".sub", ".vtt", ".txt", ".sfv", ".xml", ".json" };
    [GeneratedRegex(@"^(?:\(?\d{4}\)?(?:\s*(?:[-–—&/,]|to)\s*\d{4})*|\d{4}\s+and\s+(?:before|earlier|older)|[a-z]\s*[-–]\s*[a-z]|(?:tv[- ]?series|anime(?:[- ]?tv)?|movies|shows))$", RegexOptions.IgnoreCase)]
    private static partial Regex StructuralRegex();
    [GeneratedRegex(@"^(?:anime[- ])?tv\s+series\s+[\p{S}\s]*[a-z0-9]\s*[-–—]\s*[a-z0-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRangeFolderRegex();
    [GeneratedRegex(@"^\(?\d{4}\)?\s+(?:2160p|1080p|720p|480p)(?:\s*&\s*(?:before|earlier|older))?$", RegexOptions.IgnoreCase)]
    private static partial Regex YearResolutionFolderRegex();
    [GeneratedRegex(@"^(?:samples?|trailers?|featurettes)(?:\.[^.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraRegex();
    [GeneratedRegex("\\.(?:jpe?g|png|webp)$",RegexOptions.IgnoreCase)]private static partial Regex PosterRegex();
    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}]+") ]private static partial Regex SearchPunctuationRegex();
    [GeneratedRegex(@"\s+") ]private static partial Regex SearchSpaceRegex();
}
