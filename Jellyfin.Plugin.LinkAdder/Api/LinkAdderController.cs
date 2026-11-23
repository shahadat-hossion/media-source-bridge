using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using Jellyfin.Plugin.LinkAdder.Configuration;
using Jellyfin.Plugin.LinkAdder.Models;
using Jellyfin.Plugin.LinkAdder.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LinkAdder.Api;

[ApiController]
[Authorize]
[Route("Plugins/LinkAdder")]
public sealed class LinkAdderController : ControllerBase
{
    private static readonly SemaphoreSlim[] AddLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private Guid UserId => MediaOperationLimits.UserId(User);

    private static SemaphoreSlim GetAddLock(string destination, string title) =>
        AddLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(destination + "\n" + title) % AddLocks.Length];

    private readonly LinkAdderService _service;
    private readonly IHttpClientFactory _clients;
    private readonly ILibraryManager _library;
    private readonly AutomationService _automation;
    private readonly DiscoveryService _discovery;
    private readonly DownloadService _downloads;
    private readonly SourcePlayback _playback;
    private static readonly int TorrentBridgePort =
        int.TryParse(Environment.GetEnvironmentVariable("TORRENT_BRIDGE_PORT"), out var port) && port is > 0 and <= 65535
            ? port : 18781;

    public LinkAdderController(IHttpClientFactory clients, ILibraryManager library, AutomationService automation, DiscoveryService discovery, DownloadService downloads, SourcePlayback playback)
    {
        _clients = clients;
        _service = new LinkAdderService(Plugin.GetSourceSnapshot());
        _library = library;
        _automation = automation;
        _discovery = discovery;
        _downloads = downloads;
        _playback = playback;
    }

    [AllowAnonymous]
    [AcceptVerbs("GET", "HEAD")]
    [Route("Playback/{ticket}")]
    public Task Playback(string ticket, CancellationToken ct) => _playback.StreamAsync(HttpContext, ticket, ct);

    [HttpGet("CanManage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult CanManage() => Ok(new { canManage = true });

    [HttpPost("TorrentPlaybackCheck")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> CheckTorrentPlayback([FromBody] TorrentPlaybackRequest input, CancellationToken cancellationToken)
    {
        if (!Plugin.TorrentSourcesEnabled)
            return StatusCode(503, new { error = "Torrent sources are disabled in Setup Media." });

        if (string.IsNullOrWhiteSpace(input.Magnet) || input.Magnet.Length > 4096
            || !input.Magnet.StartsWith("magnet:?", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Paste a BitTorrent magnet link." });
        }

        using var client = _clients.CreateClient("LinkAdderTorrentBridge");
        client.Timeout = TimeSpan.FromSeconds(40);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{TorrentBridgePort}/check");
        request.Headers.Add("X-Torrent-Bridge", Plugin.GetBridgeSecret());
        request.Content = JsonContent.Create(new { magnet = input.Magnet });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            return new ContentResult
            {
                Content = await response.Content.ReadAsStringAsync(cancellationToken),
                ContentType = "application/json",
                StatusCode = (int)response.StatusCode
            };
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { error = "Start the local torrent bridge on the Jellyfin server to run this check." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { error = "The torrent bridge did not respond in time." });
        }
    }

    public sealed record TorrentPlaybackRequest(string Magnet);

    [HttpPost("PrepareTorrent")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> PrepareTorrent([FromBody] PrepareTorrentRequest input, [FromServices] ILibraryMonitor monitor, CancellationToken ct)
    {
        if (!Plugin.TorrentSourcesEnabled)
            return StatusCode(503, new { error = "Torrent sources are disabled in Setup Media." });
        if (input.MediaType == "adult")
            return BadRequest(new { error = "Adult titles require a separately restricted library for automatic import." });
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200 || input.MediaType is not ("movie" or "series" or "anime")
            || input.Releases is null || input.Releases.Count is < 1 or > 50)
            return BadRequest(new { error = "Choose a movie, show, or anime result with a compatible torrent." });

        var destination = FindAddDestination(input.MediaType);
        if (destination is null)
            return BadRequest(new { error = $"Set the {input.MediaType} default save location on the Media Sources page." });

        try
        {
            using var client = _clients.CreateClient("LinkAdderTorrentBridge");
            client.BaseAddress = new Uri($"http://127.0.0.1:{TorrentBridgePort}");
            client.Timeout = TimeSpan.FromSeconds(28);
            client.DefaultRequestHeaders.Add("X-Torrent-Bridge", Plugin.GetBridgeSecret());
            var limits = await client.GetFromJsonAsync<TorrentBridgeLimits>("limits", ct);
            if (limits is null || limits.MaxBytes <= 0)
                return StatusCode(503, new { error = "The torrent bridge did not report its memory limit." });

            var ranked = input.Releases.Where(release => release?.Magnet?.StartsWith("magnet:?xt=urn:btih:", StringComparison.Ordinal) == true
                    && release.Magnet.Length <= 100 && (release.Size is null || release.Size <= limits.MaxBytes))
                .OrderByDescending(release => release.Seeders)
                .ThenBy(release => release.Resolution switch { "1080p" => 0, "720p" => 1, _ => 2 })
                .ThenBy(release => release.Size ?? long.MaxValue)
                .DistinctBy(release => release.Magnet, StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = ranked.Take(3).Concat(ranked.Skip(3).OrderBy(release => release.Size ?? long.MaxValue).Take(1)).ToArray();
            if (candidates.Length == 0)
                return BadRequest(new { error = $"No compatible public torrent with a magnet fits the bridge’s {limits.MaxBytes / 1048576} MiB RAM budget." });

            using var reply = await client.PostAsJsonAsync("select", new { magnets = candidates.Select(candidate => candidate.Magnet) }, ct);
            if (reply.StatusCode == HttpStatusCode.Conflict)
                return Conflict(new { error = "The torrent bridge is busy. Try again when the current stream or check finishes." });
            if (!reply.IsSuccessStatusCode)
                return StatusCode(503, new { error = "The torrent bridge could not check the available releases." });
            var check = await reply.Content.ReadFromJsonAsync<TorrentBridgeAssessment>(cancellationToken: ct);
            if (check?.Status is not ("likelySmooth" or "mayBuffer") || check.LibraryFiles is null)
                return StatusCode(503, new { error = check?.Message ?? "No tested torrent supplied playable video in time." });

            var files = input.MediaType == "movie"
                ? check.LibraryFiles.Where(file => file.Index == check.SelectedFile)
                : check.LibraryFiles.Where(file => file.Name?.Contains("sample", StringComparison.OrdinalIgnoreCase) != true);
            var urls = files.Select(file => ValidatedTorrentLibraryUrl(file.Url)).OfType<Uri>().ToArray();
            if (urls.Length == 0)
                return StatusCode(503, new { error = "The selected release did not return a playable Jellyfin link." });

            var displayTitle = input.Year is >= 1900 and <= 2100 ? $"{input.Title} ({input.Year})" : input.Title;
            var preview = LinkAdderService.PlanTorrentStreams(displayTitle, input.MediaType, urls);
            var addLock = GetAddLock(destination.Path, preview.Title);
            await addLock.WaitAsync(ct);
            AddResult result;
            try { result = _service.Write(preview, destination.Path); }
            finally { addLock.Release(); }
            if (result.TitlePath is { } titlePath) monitor.ReportFileSystemChanged(titlePath);
            _library.QueueLibraryScan();
            return Ok(new { added = result, status = check.Status, message = check.Message });
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
        {
            return StatusCode(503, new { error = "The torrent bridge is unavailable or timed out." });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return BadRequest(new { error = error.Message });
        }
    }

    private static Uri? ValidatedTorrentLibraryUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == "http" && url.Host == "127.0.0.1"
        && url.Port == TorrentBridgePort && url.AbsolutePath.StartsWith("/library/", StringComparison.Ordinal)
        && url.UserInfo.Length == 0 && url.Fragment.Length == 0
        && System.Text.RegularExpressions.Regex.IsMatch(url.Query, @"\A\?ticket=[A-Za-z0-9_-]{43}\z") ? url : null;

    public sealed record PrepareTorrentRequest(string Title, int? Year, string MediaType, IReadOnlyList<TorrentCandidate> Releases);
    public sealed record TorrentCandidate(string? Magnet, long? Size, int? Seeders, string? Resolution);
    private sealed record TorrentBridgeLimits(long MaxBytes);
    private sealed record TorrentBridgeAssessment(string Status, string? Message, int SelectedFile, TorrentBridgeFile[]? LibraryFiles);
    private sealed record TorrentBridgeFile(int Index, string? Name, string? Url);

    [HttpGet("SourceAvailability")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetSourceAvailability()
    {
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance!.Configuration;
            return Ok(new SourceAvailability(configuration.FtpSourcesEnabled, configuration.TorrentSourcesEnabled, configuration.AdultTorrentModeEnabled));
        }
    }

    [HttpPost("SourceAvailability")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult SaveSourceAvailability([FromBody] SourceAvailability settings)
    {
        lock (Plugin.ConfigurationSync)
        {
            var plugin = Plugin.Instance!;
            var configuration = plugin.Configuration;
            configuration.FtpSourcesEnabled = settings.FtpEnabled ?? configuration.FtpSourcesEnabled;
            configuration.TorrentSourcesEnabled = settings.TorrentEnabled ?? configuration.TorrentSourcesEnabled;
            configuration.AdultTorrentModeEnabled = settings.AdultTorrentModeEnabled ?? configuration.AdultTorrentModeEnabled;
            plugin.UpdateConfiguration(configuration);
            return Ok(new SourceAvailability(configuration.FtpSourcesEnabled, configuration.TorrentSourcesEnabled, configuration.AdultTorrentModeEnabled));
        }
    }

    public sealed record SourceAvailability(bool? FtpEnabled, bool? TorrentEnabled, bool? AdultTorrentModeEnabled);

    [HttpGet("SearchLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult GetSearchLimit()
    {
        lock (Plugin.ConfigurationSync)
            return Ok(new SearchLimit(Plugin.Instance!.Configuration.SearchRequestsPerMinute));
    }

    [HttpPost("SearchLimit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult SaveSearchLimit([FromBody] SearchLimit settings)
    {
        if (settings.RequestsPerMinute is not >= 0)
            return BadRequest(new { error = "Enter a whole number of zero or more." });

        lock (Plugin.ConfigurationSync)
        {
            var plugin = Plugin.Instance!;
            plugin.Configuration.SearchRequestsPerMinute = settings.RequestsPerMinute.Value;
            plugin.UpdateConfiguration(plugin.Configuration);
            return Ok(settings);
        }
    }

    public sealed record SearchLimit(int? RequestsPerMinute);

    [HttpGet("Libraries")]
    public ActionResult<IReadOnlyList<LibraryView>> Libraries() => Ok(_library.GetVirtualFolders()
        .Select(folder => new LibraryView(folder.ItemId ?? string.Empty, folder.Name ?? "Library",
            CollectionType(folder.Name, folder.CollectionType?.ToString())))
        .Where(folder => folder.Id.Length != 0 && folder.MediaType.Length != 0)
        .OrderBy(folder => folder.Name).ToArray());

    [HttpGet("LibraryPaths")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<LibraryOption>> LibraryPaths() => Ok(LibrariesForValidation().OrderBy(x => x.Name).ToArray());

    [HttpGet("AddDestinations")]
    public ActionResult<IReadOnlyList<AddDestinationView>> AddDestinations()
    {
        var libraries = LibrariesForValidation();
        return Ok(new[] { "movie", "series", "anime" }.Select(type => FindAddDestination(type, libraries))
            .OfType<LibraryOption>().Select(library => new AddDestinationView(library.LibraryId, library.Name, library.MediaType,
                !string.IsNullOrWhiteSpace(DefaultLibraryPath(library.MediaType)))).ToArray());
    }

    [HttpGet("SearchUiSettings")]
    public ActionResult<SearchUiSettings> SearchUiSettings() =>
        Ok(new SearchUiSettings(Plugin.Instance?.Configuration.ReplaceDefaultSearch ?? true));

    [HttpPost("SearchUiSettings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SearchUiSettings> SaveSearchUiSettings([FromBody] SearchUiSettings request)
    {
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            configuration.ReplaceDefaultSearch = request.ReplaceDefaultSearch;
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return Ok(request);
    }

    [HttpGet("SearchDestinations")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SearchDestinationSettings> SearchDestinations()
    {
        var options = Plugin.Instance?.Configuration.SearchDestinations ?? new SearchDestinationOptions();
        return Ok(new SearchDestinationSettings(options.MovieLibraryPath, options.SeriesLibraryPath, options.AnimeLibraryPath));
    }

    [HttpPost("SearchDestinations")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SearchDestinationSettings> SaveSearchDestinations([FromBody] SearchDestinationSettings request)
    {
        var moviePath = ValidateOptionalLibraryPath(request.MovieLibraryPath, "movie");
        var seriesPath = ValidateOptionalLibraryPath(request.SeriesLibraryPath, "series");
        var animePath = ValidateOptionalLibraryPath(request.AnimeLibraryPath, "anime");
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            configuration.SearchDestinations = new SearchDestinationOptions
            {
                MovieLibraryPath = moviePath,
                SeriesLibraryPath = seriesPath,
                AnimeLibraryPath = animePath
            };
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return Ok(new SearchDestinationSettings(moviePath, seriesPath, animePath));
    }

    [HttpPost("Remote/Search")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<IReadOnlyList<RemoteSearchResult>>> SearchRemote([FromBody] RemoteSearchRequest request, CancellationToken ct)
    {
        if (!Plugin.MediaServerSourcesEnabled) return Ok(Array.Empty<RemoteSearchResult>());
        if (!MediaOperationLimits.TrySearch(UserId))
            return StatusCode(429, new { error = "Search request limit reached. Try again shortly." });

        try
        {
            return Ok(await _service.SearchRemoteAsync(NormalizeMediaType(request.MediaType), request.Query, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("Remote/SearchTitles")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> SearchRemoteTitles([FromBody] RemoteSearchRequest request, CancellationToken ct)
    {
        string[] types = request.MediaType switch
        {
            null or "" => ["movie", "series", "anime"],
            "movie" or "series" or "anime" => [request.MediaType],
            _ => []
        };
        if (types.Length == 0 || request.Query?.Trim().Length is not (>= 2 and <= 200))
            return BadRequest(new { error = "Enter between 2 and 200 characters and choose a supported media type." });

        if (!Plugin.MediaServerSourcesEnabled) return Ok(Array.Empty<object>());
        if (!MediaOperationLimits.TrySearch(UserId))
            return StatusCode(429, new { error = "Search request limit reached. Try again shortly." });
        var groups = await Task.WhenAll(types.Select(async mediaType =>
        {
            try
            {
                return (MediaType: mediaType, Items: await _service.SearchRemoteAsync(mediaType, request.Query, ct), Error: (string?)null);
            }
            catch (InvalidOperationException ex)
            {
                return (MediaType: mediaType, Items: (IReadOnlyList<RemoteSearchResult>)[], Error: (string?)ex.Message);
            }
        }));
        if (groups.All(group => group.Error is not null))
            return BadRequest(new { error = groups[0].Error });

        return Ok(groups.SelectMany(group => group.Items.Select(item => new
        {
            group.MediaType, item.Name, item.Url, item.Source, item.PosterUrl
        })));
    }

    [HttpPost("Preview")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<PreviewResult>> Preview([FromBody] PreviewRequest request, CancellationToken ct)
    {
        using var operation = MediaOperationLimits.TryEnter(UserId);
        if (operation is null)
            return StatusCode(429, new { error = "Too many media operations. Try again shortly." });

        try
        {
            return Ok(await _service.PreviewAsync(request.Url, request.MediaType, request.Title, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException error)
        {
            return BadRequest(new { error = error.Message });
        }
        catch (Exception error) when (!ct.IsCancellationRequested
            && error is HttpRequestException or WebException or IOException or OperationCanceledException)
        {
            return StatusCode(502, new { error = "Could not read the configured media source." });
        }
    }

    [HttpPost("Add")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AddResult>> Add([FromBody] AddRequest request, CancellationToken ct)
    {
        using var operation = MediaOperationLimits.TryEnter(UserId);
        if (operation is null)
            return StatusCode(429, new { error = "Too many media operations. Try again shortly." });

        try
        {
            var mediaType = NormalizeMediaType(request.MediaType);
            var option = FindAddDestination(mediaType);
            if (option is null)
                return BadRequest(new { error = $"Set the {mediaType} default save location on the Media Sources page." });

            var preview = await _service.PreviewAsync(request.Url, mediaType, request.Title, ct).ConfigureAwait(false);
            var addLock = GetAddLock(option.Path, preview.Title);
            await addLock.WaitAsync(ct).ConfigureAwait(false);
            AddResult result;
            try
            {
                result = _service.Write(preview, option.Path, _playback.CreateUrl);
            }
            finally
            {
                addLock.Release();
            }
            _library.QueueLibraryScan();
            return Ok(result with
            {
                Created = result.Created.Select(file => Path.GetRelativePath(option.Path, file)).ToArray(),
                Skipped = result.Skipped.Select(file => Path.GetRelativePath(option.Path, file)).ToArray(),
                TitlePath = result.Title,
                LibraryId = option.LibraryId,
                Refresh = "Jellyfin library scan queued."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is (HttpRequestException or WebException or OperationCanceledException))
        {
            return StatusCode(502, new { error = "Could not read the selected media folder. Check the source connection and credentials." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { error = "Could not write STRM files. Check that Jellyfin can write to the library folder and retry." });
        }
    }

    [HttpPost("Download")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<AddResult>> Download([FromBody] AddRequest request, CancellationToken ct)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var option = FindDownloadDestination(mediaType);
        if (option is null) return BadRequest(new { error = $"Set the {mediaType} default save location on the Media Sources page." });
        try
        {
            var result = await _downloads.DownloadAsync(_service, request, option.Path, ct).ConfigureAwait(false);
            return result is null ? StatusCode(429, new { error = "A download is already running." }) : Ok(result);
        }
        catch (InvalidOperationException error)
        {
            return BadRequest(new { error = error.Message });
        }
        catch (Exception error) when (!ct.IsCancellationRequested
            && error is HttpRequestException or WebException or IOException or OperationCanceledException)
        {
            return StatusCode(502, new { error = "The transfer failed or exceeded its limits." });
        }
    }

    [HttpPost("Downloads")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<DownloadJobStarted> StartDownload([FromBody] AddRequest request)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var option = FindDownloadDestination(mediaType);
        if (option is null) return BadRequest(new { error = $"Set the {mediaType} default save location on the Media Sources page." });
        var job = _downloads.Start(request, option.Path);
        return job is null ? StatusCode(429, new { error = "The download queue is full." }) : Accepted(job);
    }

    [HttpDelete("Downloads/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult CancelDownload(string id) => _downloads.Cancel(id) ? Accepted() : NotFound();

    [HttpGet("Downloads/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<DownloadJobView> DownloadStatus(string id)
    {
        var job = _downloads.Find(id);
        if (job is null) return NotFound(new { error = "Download job was not found." });
        return Ok(job);
    }

    [HttpPost("Sources/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<TestSourceResult>> TestSource([FromBody] TestSourceRequest request, CancellationToken ct)
    {
        ValidateLocalSource(request.Url);
        try
        {
            var entries = await _service.TestConnectionAsync(request.Url, request.Username, request.Password, ct).ConfigureAwait(false);
            return Ok(new TestSourceResult(true, entries, $"Connection successful. Found {entries} entries."));
        }
        catch (Exception ex) when (ex is HttpRequestException or WebException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return BadRequest(new { error = $"Connection failed: {ex.Message}" });
        }
    }

    [HttpGet("Sources")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<CustomSourceView>> Sources()
    {
        var items = Plugin.GetSourceSnapshot(includeDisabled: true)
            .Select(x => new CustomSourceView(x.Id, x.Name, x.Url, x.MediaType, !string.IsNullOrEmpty(x.Username)))
            .OrderBy(x => x.Name)
            .ToArray();
        return Ok(items);
    }

    [HttpGet("Sources/Export")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SourceExportFile> ExportSources([FromQuery] bool includeCredentials = false)
    {
        var sources = Plugin.GetSourceSnapshot(includeDisabled: true)
            .OrderBy(x => x.Name)
            .Select(x => new SourceTransferItem(x.Name, x.Url, x.MediaType,
                includeCredentials ? x.Username : null,
                includeCredentials ? x.Password : null))
            .ToArray();
        return Ok(new SourceExportFile(1, DateTime.UtcNow, sources));
    }

    [HttpPost("Sources/Import")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SourceImportResult> ImportSources([FromBody] SourceExportFile import)
    {
        if (import.Version != 1) return BadRequest(new { error = "This source export version is not supported." });
        if (import.Sources is null) return BadRequest(new { error = "The import file does not contain a sources list." });
        if (import.Sources.Count > 500) return BadRequest(new { error = "An import file can contain at most 500 sources." });

        var validated = new List<CustomSource>(import.Sources.Count);
        for (var index = 0; index < import.Sources.Count; index++)
        {
            var item = import.Sources[index];
            var name = (item.Name ?? string.Empty).Trim();
            if (name.Length < 2) return BadRequest(new { error = $"Source {index + 1} has an invalid name." });
            try
            {
                validated.Add(new CustomSource
                {
                    Name = name,
                    Url = EnsureDirectoryUrl(ValidateLocalSource(item.Url)).AbsoluteUri,
                    MediaType = NormalizeSourceMediaType(item.MediaType),
                    Username = (item.Username ?? string.Empty).Trim(),
                    Password = item.Password ?? string.Empty
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = $"Source {index + 1} ({name}) is invalid: {ex.Message}" });
            }
        }

        var imported = 0;
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            foreach (var source in validated)
            {
                if (configuration.CustomSources.Any(x => x.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase) && x.MediaType == source.MediaType)) continue;
                configuration.CustomSources.Add(source);
                imported++;
            }
            if (imported > 0) Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return Ok(new SourceImportResult(imported, validated.Count - imported, validated.Count));
    }

    [HttpPost("Sources/TestAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<SavedSourceTestResult>>> TestAllSources(CancellationToken ct)
    {
        var sources = Plugin.GetSourceSnapshot(includeDisabled: true);
        var tests = sources.Select(async source =>
        {
            try
            {
                var entries = await _service.TestConnectionAsync(source.Url, source.Username, source.Password, ct).ConfigureAwait(false);
                return new SavedSourceTestResult(source.Id, true, entries, $"Connection successful. Found {entries} entries.");
            }
            catch (Exception ex) when (ex is HttpRequestException or WebException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                return new SavedSourceTestResult(source.Id, false, 0, $"Connection failed: {ex.Message}");
            }
        });
        return Ok(await Task.WhenAll(tests).ConfigureAwait(false));
    }

    [HttpPost("Sources")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<CustomSourceView> AddSource([FromBody] CustomSourceRequest request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        var mediaType = NormalizeSourceMediaType(request.MediaType);
        var uri = ValidateLocalSource(request.Url);
        if (name.Length < 2) return BadRequest(new { error = "Enter a source name." });
        var source = new CustomSource
        {
            Name = name,
            Url = EnsureDirectoryUrl(uri).AbsoluteUri,
            MediaType = mediaType,
            Username = (request.Username ?? string.Empty).Trim(),
            Password = request.Password ?? string.Empty
        };
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            if (configuration.CustomSources.Any(x => x.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase) && x.MediaType == source.MediaType))
                return Conflict(new { error = "That source is already configured for this media type." });
            configuration.CustomSources.Add(source);
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return Ok(new CustomSourceView(source.Id, source.Name, source.Url, source.MediaType, source.Username.Length > 0));
    }

    [HttpDelete("Sources/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteSource(string id)
    {
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            var removed = configuration.CustomSources.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return NoContent();
    }

    [HttpPost("Requests")]
    [RequestSizeLimit(8192)]
    public ActionResult<MediaRequestView> SubmitRequest([FromBody] SubmitMediaRequest request)
    {
        if (UserId == Guid.Empty) return Forbid();
        using var rate = MediaOperationLimits.AcquireRate(UserId);
        if (!rate.IsAcquired) return StatusCode(429);
        Uri source;
        string mediaType;
        try
        {
            source = _service.ValidateConfiguredSource(request.Url);
            mediaType = NormalizeMediaType(request.MediaType);
        }
        catch (InvalidOperationException error)
        {
            return BadRequest(new { error = error.Message });
        }
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim();
        if (title.Length > 200) return BadRequest(new { error = "Keep the title under 201 characters." });
        var item = new MediaAddRequest
        {
            RequestedById = UserId,
            RequestedBy = User.Identity?.Name ?? "Jellyfin user",
            Title = title,
            Url = source.AbsoluteUri,
            MediaType = mediaType
        };
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            if (configuration.MediaRequests.Count(x => x.RequestedById == UserId) >= 100) return StatusCode(429);
            if (configuration.MediaRequests.Any(x => x.RequestedById == UserId && x.Url.Equals(item.Url, StringComparison.Ordinal) && x.MediaType == item.MediaType))
                return Conflict(new { error = "This title is already requested." });
            configuration.MediaRequests.Add(item);
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return Ok(ToView(item));
    }

    [HttpGet("Requests")]
    public async Task<ActionResult<IReadOnlyList<MediaRequestView>>> Requests([FromServices] IAuthorizationService authorization)
    {
        var elevated = (await authorization.AuthorizeAsync(User, Policies.RequiresElevation)).Succeeded;
        lock (Plugin.ConfigurationSync)
        {
            return Ok((Plugin.Instance?.Configuration.MediaRequests ?? [])
                .Where(x => elevated || (UserId != Guid.Empty && x.RequestedById == UserId))
                .OrderByDescending(x => x.RequestedAtUtc).Select(ToView).ToArray());
        }
    }

    [HttpDelete("Requests/{id}")]
    public async Task<ActionResult> DeleteRequest(string id, [FromServices] IAuthorizationService authorization)
    {
        var elevated = (await authorization.AuthorizeAsync(User, Policies.RequiresElevation)).Succeeded;
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            var removed = configuration.MediaRequests.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && (elevated || (UserId != Guid.Empty && x.RequestedById == UserId)));
            if (removed == 0) return NotFound();
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return NoContent();
    }

    [HttpGet("Automation/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<AutomationSettingsView> AutomationSettings()
    {
        var x = Plugin.Instance?.Configuration.Automation ?? new AutomationOptions();
        return Ok(ToAutomationView(x));
    }

    [HttpPost("Automation/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<AutomationSettingsView> SaveAutomationSettings([FromBody] AutomationSettingsRequest request)
    {
        if (request.IntervalHours is < 1 or > 168) return BadRequest(new { error = "Run interval must be between 1 and 168 hours." });
        if (request.MaxPerCategory is < 1 or > 25) return BadRequest(new { error = "Category limit must be between 1 and 25." });
        if (request.MinimumRating is < 0 or > 10) return BadRequest(new { error = "Minimum rating must be between 0 and 10." });
        if (request.MinimumVotes is < 0 or > 10000000) return BadRequest(new { error = "Minimum votes is invalid." });
        var destinations = Plugin.Instance?.Configuration.SearchDestinations ?? new SearchDestinationOptions();
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            var previous = configuration.Automation;
            configuration.Automation = new AutomationOptions
            {
                Enabled = request.Enabled,
                PreviewOnly = request.PreviewOnly,
                TmdbCredential = string.IsNullOrWhiteSpace(request.TmdbCredential) ? previous.TmdbCredential : request.TmdbCredential.Trim(),
                TrendingMovies = request.TrendingMovies,
                NewMovies = request.NewMovies,
                TopMovies = request.TopMovies,
                TrendingSeries = request.TrendingSeries,
                NewSeries = request.NewSeries,
                TopSeries = request.TopSeries,
                TrendingAnime = request.TrendingAnime,
                IntervalHours = request.IntervalHours,
                MaxPerCategory = request.MaxPerCategory,
                MinimumRating = request.MinimumRating,
                MinimumVotes = request.MinimumVotes,
                MovieLibraryPath = destinations.MovieLibraryPath,
                SeriesLibraryPath = destinations.SeriesLibraryPath,
                AnimeLibraryPath = destinations.AnimeLibraryPath,
                LastRunUtc = previous.LastRunUtc
            };
            Plugin.Instance!.UpdateConfiguration(configuration);
            return Ok(ToAutomationView(configuration.Automation));
        }
    }

    [HttpPost("Automation/Preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<AutomationRunResult>> PreviewAutomation(CancellationToken ct) => Ok(await _automation.RunAsync(true, ct).ConfigureAwait(false));

    [HttpPost("Automation/Run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<AutomationRunResult>> RunAutomation(CancellationToken ct) => Ok(await _automation.RunAsync(false, ct).ConfigureAwait(false));

    [HttpPost("Automation/AddTitle")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<AddDiscoveryTitleResult>> AddDiscoveryTitle([FromBody] AddDiscoveryTitleRequest request, CancellationToken ct)
    {
        using var operation = MediaOperationLimits.TryEnter(UserId);
        if (operation is null)
            return StatusCode(429, new { error = "Too many media operations. Try again shortly." });

        if (string.IsNullOrWhiteSpace(request.ContentId) || request.ContentId.Length > 128)
            return BadRequest(new { error = "The discovery title is invalid." });
        var catalog = await DiscoverCatalogAsync(ct).ConfigureAwait(false);
        var item = catalog.FirstOrDefault(title => title.ContentId.Equals(request.ContentId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return NotFound(new { error = "Refresh the discovery catalog and select the title again." });
        try
        {
            return Ok(await _automation.AddTitleAsync(item, request.SelectedUrl, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException error)
        {
            return Conflict(new { error = error.Message });
        }
    }

    [HttpGet("Automation/Catalog")]
    public async Task<ActionResult<IReadOnlyList<DiscoveryTitle>>> AutomationCatalog(CancellationToken ct)
    {
        using var operation = MediaOperationLimits.TryEnter(UserId);
        if (operation is null)
            return StatusCode(429, new { error = "Too many media operations. Try again shortly." });

        var items = await DiscoverCatalogAsync(ct).ConfigureAwait(false);
        AutomationAddedItem[] addedItems;
        lock (Plugin.ConfigurationSync)
        {
            addedItems = Plugin.Instance?.Configuration.AutomationAddedItems.ToArray() ?? [];
        }
        var activeAddedIds = addedItems.Where(x => x.Files.Any(System.IO.File.Exists))
            .Select(x => x.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ok(items.Where(x => !activeAddedIds.Contains(x.ContentId)).ToArray());
    }

    private async Task<IReadOnlyList<DiscoveryTitle>> DiscoverCatalogAsync(CancellationToken ct)
    {
        var saved = Plugin.Instance?.Configuration.Automation ?? new AutomationOptions();
        var catalog = new AutomationOptions
        {
            TmdbCredential = saved.TmdbCredential,
            TrendingMovies = true,
            NewMovies = true,
            TopMovies = true,
            TrendingSeries = true,
            NewSeries = true,
            TopSeries = true,
            TrendingAnime = true,
            MaxPerCategory = Math.Clamp(saved.MaxPerCategory, 5, 25),
            MinimumRating = saved.MinimumRating,
            MinimumVotes = saved.MinimumVotes
        };
        return await _discovery.DiscoverAsync(catalog, ct).ConfigureAwait(false);
    }

    [HttpGet("Automation/History")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<AutomationHistoryView>> AutomationHistory()
    {
        lock (Plugin.ConfigurationSync)
        {
            return Ok((Plugin.Instance?.Configuration.AutomationHistory ?? [])
                .OrderByDescending(x => x.TimestampUtc).Take(100)
                .Select(x => new AutomationHistoryView(x.TimestampUtc, x.ContentId, x.Title, x.MediaType, x.Status, x.Message)).ToArray());
        }
    }

    [HttpGet("Automation/Exclusions")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<AutomationExclusionView>> AutomationExclusions()
    {
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return Ok(configuration.AutomationExcludedIds.Select(id =>
            {
                var item = configuration.AutomationHistory.LastOrDefault(x => x.ContentId.Equals(id, StringComparison.OrdinalIgnoreCase));
                return new AutomationExclusionView(id, item?.Title ?? id, item?.MediaType ?? string.Empty, item?.TimestampUtc);
            }).ToArray());
        }
    }

    [HttpDelete("Automation/Exclusions/{*contentId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteAutomationExclusion(string contentId)
    {
        lock (Plugin.ConfigurationSync)
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin configuration is unavailable.");
            var removed = configuration.AutomationExcludedIds.RemoveAll(x => x.Equals(contentId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return NotFound();
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
        return NoContent();
    }

    private LibraryOption[] LibrariesForValidation() => _library.GetVirtualFolders()
        .SelectMany(v => v.Locations.Select(path => new LibraryOption(
            v.Locations.Count() > 1 ? $"{v.Name} — {Path.GetFileName(path)}" : v.Name ?? "Library",
            Path.GetFullPath(path),
            CollectionType(v.Name, v.CollectionType?.ToString()),
            v.ItemId ?? string.Empty)))
        .Where(x => x.MediaType.Length > 0)
        .ToArray();

    private LibraryOption? FindDownloadDestination(string mediaType)
    {
        var configuredPath = DefaultLibraryPath(mediaType);
        var requestedPath = string.IsNullOrWhiteSpace(configuredPath) ? string.Empty : Path.GetFullPath(configuredPath);
        return LibrariesForValidation().FirstOrDefault(x => x.MediaType == mediaType && x.Path.Equals(requestedPath, StringComparison.OrdinalIgnoreCase));
    }

    private LibraryOption? FindAddDestination(string mediaType, LibraryOption[]? libraries = null)
    {
        var configuredPath = DefaultLibraryPath(mediaType);
        var compatible = (libraries ?? LibrariesForValidation()).Where(x => x.MediaType == mediaType).ToArray();
        return string.IsNullOrWhiteSpace(configuredPath)
            ? compatible.Length == 1 ? compatible[0] : null
            : compatible.FirstOrDefault(x => Path.TrimEndingDirectorySeparator(x.Path).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath)), StringComparison.OrdinalIgnoreCase));
    }

    private static string DefaultLibraryPath(string mediaType) =>
        Plugin.Instance?.Configuration.SearchDestinations.GetLibraryPath(mediaType) ?? string.Empty;

    private static MediaRequestView ToView(MediaAddRequest item) => new(item.Id, item.RequestedBy, item.Title, item.Url, item.MediaType, item.RequestedAtUtc);
    private static AutomationSettingsView ToAutomationView(AutomationOptions x) => new(x.Enabled, x.PreviewOnly, !string.IsNullOrWhiteSpace(x.TmdbCredential), x.TrendingMovies, x.NewMovies, x.TopMovies, x.TrendingSeries, x.NewSeries, x.TopSeries, x.TrendingAnime, x.IntervalHours, x.MaxPerCategory, x.MinimumRating, x.MinimumVotes, x.MovieLibraryPath, x.SeriesLibraryPath, x.AnimeLibraryPath, x.LastRunUtc, x.LastRunUtc?.AddHours(Math.Clamp(x.IntervalHours, 1, 168)));
    private string ValidateOptionalLibraryPath(string? value, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var full = Path.GetFullPath(value);
        if (!LibrariesForValidation().Any(x => x.MediaType == mediaType && x.Path.Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Select a compatible {mediaType} library.");
        return full;
    }
    private static string NormalizeMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" => "movie",
        "series" => "series",
        "anime" => "anime",
        _ => throw new InvalidOperationException("Choose Movies, Shows, or Anime.")
    };
    private static string NormalizeSourceMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" => "movie",
        "series" => "series",
        "both" => "both",
        "anime" => "anime",
        _ => throw new InvalidOperationException("Choose Movies, TV Series, Both, or Anime.")
    };
    private static string CollectionType(string? name, string? value)
    {
        var type = value?.ToLowerInvariant();
        if (name?.Contains("Anime", StringComparison.OrdinalIgnoreCase) == true) return "anime";
        if (type == "movies") return "movie";
        if (type == "tvshows") return "series";
        return string.Empty;
    }
    private static Uri ValidateLocalSource(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
            throw new InvalidOperationException("Use a complete HTTP, HTTPS, or FTP URL.");
        if (!LinkAdderService.IsSafeSourceUri(uri) || uri.Query.Length != 0)
            throw new InvalidOperationException("Use a source directory URL without embedded credentials, query, or fragment.");
        if (!IPAddress.TryParse(uri.Host, out var address) || !LinkAdderService.IsPrivate(address))
            throw new InvalidOperationException("Only private/local IP source URLs are allowed.");
        return uri;
    }
    private static Uri EnsureDirectoryUrl(Uri uri) => uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
