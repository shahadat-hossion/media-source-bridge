using System.Collections.Concurrent;
using Jellyfin.Plugin.LinkAdder.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LinkAdder.Services;

public sealed class DownloadService(ILibraryManager library)
{
    private const string CompletedMessage = "Download complete. Jellyfin library scan started.";
    private static readonly TimeSpan JobRetention = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, JobSnapshot> _jobs = new(StringComparer.OrdinalIgnoreCase);

    private sealed record JobSnapshot(DownloadJobView View, DateTime UpdatedUtc);

    internal async Task<AddResult> DownloadAsync(
        LinkAdderService media, AddRequest request, string destination, CancellationToken cancellationToken)
    {
        var result = await TransferAsync(media, request, destination, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(ScanLibraryAsync);
        return result with { Refresh = CompletedMessage };
    }

    internal DownloadJobStarted Start(LinkAdderService media, AddRequest request, string destination)
    {
        var cutoff = DateTime.UtcNow - JobRetention;
        foreach (var (id, job) in _jobs)
        {
            if (job.UpdatedUtc < cutoff && job.View.Status is "complete" or "failed")
            {
                _jobs.TryRemove(id, out _);
            }
        }

        var queued = new DownloadJobView(Guid.NewGuid().ToString("N"), "queued", 0, "Preparing download…", 0, 0);
        Publish(queued);
        _ = Task.Run(() => RunJobAsync(queued, media, request, destination));
        return new DownloadJobStarted(queued.JobId);
    }

    internal DownloadJobView? Find(string id) => _jobs.TryGetValue(id, out var snapshot) ? snapshot.View : null;

    private async Task RunJobAsync(DownloadJobView job, LinkAdderService media, AddRequest request, string destination)
    {
        job = job with { Status = "running" };
        Publish(job);
        try
        {
            var result = await TransferAsync(media, request, destination, CancellationToken.None, (percentage, message) =>
            {
                job = job with { Percentage = Math.Round(percentage, 1), Message = message };
                Publish(job);
            }).ConfigureAwait(false);
            job = job with
            {
                Status = "complete",
                Percentage = 100,
                Message = CompletedMessage,
                Downloaded = result.Created.Count,
                Skipped = result.Skipped.Count
            };
            Publish(job);
            await ScanLibraryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Publish(job with { Status = "failed", Message = ex.Message });
        }
    }

    private async Task<AddResult> TransferAsync(
        LinkAdderService media, AddRequest request, string destination, CancellationToken cancellationToken,
        Action<double, string>? progress = null)
    {
        var preview = await media.PreviewAsync(request.Url, request.MediaType, request.Title, cancellationToken).ConfigureAwait(false);
        return await media.DownloadAsync(preview, destination, cancellationToken, progress).ConfigureAwait(false);
    }

    private void Publish(DownloadJobView view) => _jobs[view.JobId] = new JobSnapshot(view, DateTime.UtcNow);

    private Task ScanLibraryAsync() => library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);
}
