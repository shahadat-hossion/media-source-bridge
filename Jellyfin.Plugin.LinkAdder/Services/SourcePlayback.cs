using System.Net;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.LinkAdder.Services;

public sealed class SourcePlayback(IServerApplicationHost host)
{
    private static readonly SemaphoreSlim Readers = new(16);
    private static readonly string[] ForwardedHeaders = ["Range", "If-Range"];
    private static readonly string[] ResponseHeaders = ["Accept-Ranges", "Content-Range", "Content-Length", "Content-Type"];

    public string CreateUrl(string source)
    {
        if (new Uri(source).Scheme == Uri.UriSchemeFtp) return source;
        var payload = Encoding.UTF8.GetBytes(source);
        var ticket = WebEncoders.Base64UrlEncode(payload) + "." + WebEncoders.Base64UrlEncode(Sign(payload));
        var origin = host.GetLocalApiUrl("127.0.0.1", "http", host.HttpPort).TrimEnd('/');
        return origin + "/Plugins/LinkAdder/Playback/" + ticket;
    }

    public async Task StreamAsync(HttpContext context, string ticket, CancellationToken ct)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || !TryReadTicket(ticket, out var source))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await Readers.WaitAsync(0, ct).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            var media = new LinkAdderService(Plugin.GetSourceSnapshot());
            await media.ProxyPlaybackAsync(source, context, ForwardedHeaders, ResponseHeaders, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            if (context.Response.HasStarted) context.Abort();
            else context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || (error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            if (context.Response.HasStarted) context.Abort();
            else context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            Readers.Release();
        }
    }

    private static bool TryReadTicket(string ticket, out string source)
    {
        source = string.Empty;
        if (ticket.Length > 2800) return false;
        var separator = ticket.IndexOf('.');
        if (separator < 1) return false;
        try
        {
            var payload = WebEncoders.Base64UrlDecode(ticket[..separator]);
            var signature = WebEncoders.Base64UrlDecode(ticket[(separator + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload))) return false;
            source = Encoding.UTF8.GetString(payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Sign(byte[] payload) =>
        HMACSHA256.HashData(Convert.FromHexString(Plugin.GetPlaybackSigningKey()), payload);
}
