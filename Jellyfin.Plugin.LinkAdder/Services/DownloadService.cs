using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.LinkAdder.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinkAdder.Services;

public sealed class DownloadService(ILibraryManager library, ILogger<DownloadService> logger) : BackgroundService
{
    private static readonly TimeSpan JobRetention = TimeSpan.FromHours(12);
    private static readonly TimeSpan TransferDeadline = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _transfer = new(1);
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(4) { SingleReader = true });
    private CancellationToken _stopping;

    private sealed class Job(DownloadJobView view, AddRequest request, string destination)
    {
        public volatile DownloadJobView View = view;
        public AddRequest Request { get; } = request;
        public string Destination { get; } = destination;
        public CancellationTokenSource Cancel { get; } = new();
        public DateTime? FinishedAt { get; set; }
    }

    internal async Task<AddResult?> DownloadAsync(LinkAdderService media, AddRequest request, string destination, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
        deadline.CancelAfter(TransferDeadline);
        if (!await _transfer.WaitAsync(0, deadline.Token).ConfigureAwait(false)) return null;
        try
        {
            var result = await TransferAsync(media, request, destination, deadline.Token).ConfigureAwait(false);
            return result with { Refresh = QueueScan() };
        }
        finally
        {
            _transfer.Release();
        }
    }

    internal DownloadJobStarted? Start(AddRequest request, string destination)
    {
        var cutoff = DateTime.UtcNow - JobRetention;
        foreach (var (id, job) in _jobs)
        {
            lock (job)
            {
                if (job.FinishedAt < cutoff && _jobs.TryRemove(id, out _)) job.Cancel.Dispose();
            }
        }

        var queued = new DownloadJobView(Guid.NewGuid().ToString("N"), "queued", 0, "Preparing download…", 0, 0);
        var entry = new Job(queued, request, destination);
        _jobs[queued.JobId] = entry;
        if (_queue.Writer.TryWrite(entry)) return new DownloadJobStarted(queued.JobId);
        _jobs.TryRemove(queued.JobId, out _);
        entry.Cancel.Dispose();
        return null;
    }

    internal DownloadJobView? Find(string id) => _jobs.TryGetValue(id, out var job) ? job.View : null;

    internal bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return false;
        lock (job)
        {
            if (job.FinishedAt is not null) return true;
            job.Cancel.Cancel();
            if (job.View.Status == "queued")
                job.View = job.View with { Status = "cancelled", Message = "Download cancelled." };
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancel.Token);
        deadline.CancelAfter(TransferDeadline);
        try
        {
            await _transfer.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                job.View = job.View with { Status = "running" };
                var media = new LinkAdderService(Plugin.GetSourceSnapshot());
                var result = await TransferAsync(media, job.Request, job.Destination, deadline.Token, (percentage, message) =>
                    job.View = job.View with { Percentage = Math.Round(percentage, 1), Message = message }).ConfigureAwait(false);
                job.View = job.View with
                {
                    Status = "complete",
                    Percentage = 100,
                    Message = QueueScan(),
                    Downloaded = result.Created.Count,
                    Skipped = result.Skipped.Count
                };
            }
            finally
            {
                _transfer.Release();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || job.Cancel.IsCancellationRequested)
        {
            job.View = job.View with { Status = "cancelled", Message = "Download cancelled." };
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Media download {JobId} failed.", job.View.JobId);
            job.View = job.View with { Status = "failed", Message = "Download failed or exceeded its limits. Check the Jellyfin logs." };
        }
        finally
        {
            lock (job) job.FinishedAt = DateTime.UtcNow;
        }
    }

    private static async Task<AddResult> TransferAsync(
        LinkAdderService media, AddRequest request, string destination, CancellationToken ct,
        Action<double, string>? progress = null)
    {
        var preview = await media.PreviewAsync(request.Url, request.MediaType, request.Title, ct).ConfigureAwait(false);
        return await media.DownloadAsync(preview, destination, ct, progress).ConfigureAwait(false);
    }

    private string QueueScan()
    {
        try
        {
            library.QueueLibraryScan();
            return "Download complete. Jellyfin library scan queued.";
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not queue the library scan after a download.");
            return "Download complete. Jellyfin could not queue a library scan.";
        }
    }
}
