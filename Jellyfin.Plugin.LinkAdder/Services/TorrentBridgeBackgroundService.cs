using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LinkAdder.Services;

internal sealed class TorrentBridgeBackgroundService(
    IApplicationPaths paths, ILogger<TorrentBridgeBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = Path.Combine(paths.ProgramDataPath, "torrent-bridge");
        var script = Path.Combine(directory, "bridge.mjs");
        var node = Path.Combine(directory, "runtime", OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (!File.Exists(script) || !File.Exists(node)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            using var bridge = new Process
            {
                StartInfo = new ProcessStartInfo(node)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            bridge.StartInfo.ArgumentList.Add(script);
            bridge.StartInfo.Environment["TORRENT_JELLYFIN_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var ffprobe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(ffprobe)) bridge.StartInfo.Environment["TORRENT_FFPROBE"] = ffprobe;

            bridge.OutputDataReceived += (_, line) =>
            {
                if (line.Data is not null) logger.LogInformation("Torrent bridge: {Message}", line.Data);
            };
            bridge.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is not null) logger.LogWarning("Torrent bridge: {Message}", line.Data);
            };

            var started = false;
            try
            {
                started = bridge.Start();
                if (started)
                {
                    bridge.BeginOutputReadLine();
                    bridge.BeginErrorReadLine();
                    await bridge.WaitForExitAsync(stoppingToken);
                    logger.LogWarning("Torrent bridge exited with code {ExitCode}. Restarting in five seconds.", bridge.ExitCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(error, "Torrent bridge could not run. Retrying in five seconds.");
            }
            finally
            {
                if (started && !bridge.HasExited)
                {
                    try { bridge.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception error) { logger.LogWarning(error, "Torrent bridge shutdown failed."); }
                }
            }
            if (!stoppingToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
