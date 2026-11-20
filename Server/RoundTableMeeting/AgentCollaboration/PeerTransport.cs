using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Abstraction.Contracts.Meetings;

namespace RoundTableMeeting.AgentCollaboration;

public sealed record PeerCommand(string? Room = null, Guid MeetingCode = default, string? DisplayName = null,
    string? InvitationKey = null, Guid MessageId = default, string? Content = null, Guid[]? AttachmentIds = null,
    Guid? ReplyToMessageId = null, Guid? AttachmentId = null, long? BeforeSequence = null, int Limit = 5,
    Guid ClientSubmissionId = default, Guid? RecipientParticipantId = null, string? RecipientDisplayName = null);

public sealed class PeerTransport : IDisposable
{
    private readonly HashSet<Uri> trusted;
    private readonly HttpClient publicClient;
    private readonly HttpClient trustedClient;
    private readonly IDataProtector files;
    private readonly TimeProvider clock;
    private readonly long fileLimit;
    public Uri? PublicSite { get; }
    internal sealed record FileRoute(string Site, string Secret, string Method, DateTimeOffset ExpiresAt, long Limit);

    public PeerTransport(IConfiguration config, MeetingSiteAddress address, IDataProtectionProvider protection, TimeProvider clock, IOptions<AttachmentOptions> limits)
    {
        this.clock = clock;
        fileLimit = limits.Value.MaxFileBytes;
        PublicSite = address.PublicUrl is null ? null : Normalize(address.PublicUrl);
        trusted = (config.GetSection("Federation:TrustedSites").Get<string[]>() ?? []).Select(Normalize).ToHashSet();
        files = protection.CreateProtector("RoundTableMeeting.PeerFile.v1");
        publicClient = Client(false);
        trustedClient = Client(true);
    }

    public static Uri Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value.Contains('\\'))
            throw new McpException("invalid_site_url");
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    private static HttpClient Client(bool trusted)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (!trusted) handler.ConnectCallback = ConnectPublic;
        return new(handler) { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && (b[1] == 2
                || b[1] == 1 && (b[2] < 2 || b[2] == 0xd && b[3] == 0xb8)))
                && !(b[0] == 0x3f && b[1] == 0xff);
        return b[0] is not (0 or 10 or 127) && b[0] < 224
            && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] is 0 or 168)
            && !(b[0] == 192 && b[1] == 88 && b[2] == 99) && !(b[0] == 198 && b[1] is 18 or 19 or 51)
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113) && !ip.Equals(IPAddress.Parse("168.63.129.16"));
    }

    private static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip))) throw new HttpRequestException("site_address_not_public");
        // Use exactly the addresses inspected above, including on each new pooled connection.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    internal HttpClient ForSite(Uri site)
    {
        if (trusted.Contains(site)) return trustedClient;
        if (site.Scheme != "https" || site.IsLoopback) throw new McpException("site_requires_trust");
        return publicClient;
    }

    public async Task<T> Call<T>(string destination, string operation, PeerCommand command, CancellationToken ct)
    {
        var site = Normalize(destination);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "poll" ? 5 : 30));
        try
        {
            using var res = await ForSite(site).PostAsJsonAsync(new Uri(site, "roundtable-peer/v1/" + operation), command, timeout.Token);
            if (!res.IsSuccessStatusCode)
            {
                var code = res.StatusCode == HttpStatusCode.BadRequest && res.Content.Headers.ContentType?.MediaType == "application/json"
                    ? (await res.Content.ReadFromJsonAsync<PeerError>(timeout.Token))?.Code : null;
                throw new McpException(code ?? "peer_unavailable");
            }
            return await res.Content.ReadFromJsonAsync<T>(PeerJson.Options, timeout.Token) ?? throw new McpException("invalid_peer_response");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new McpException("peer_timeout"); }
        catch (HttpRequestException) { throw new McpException("peer_unreachable"); }
        catch (JsonException) { throw new McpException("invalid_peer_response"); }
        catch (NotSupportedException) { throw new McpException("invalid_peer_response"); }
    }

    public sealed record PeerError(string Code);
    public McpTransferTicket WrapTicket(string site, McpTransferTicket ticket, string pathBase)
    {
        if (ticket.Method is not ("GET" or "POST") || ticket.ExpiresAt <= clock.GetUtcNow() || ticket.MaxFileBytes <= 0)
            throw new McpException("invalid_peer_ticket");
        var route = new FileRoute(site, ticket.BearerToken, ticket.Method, ticket.ExpiresAt, Math.Min(ticket.MaxFileBytes, fileLimit));
        return new(pathBase + "/roundtable-peer-files", route.Method, files.Protect(JsonSerializer.Serialize(route)), route.ExpiresAt, route.Limit);
    }

    internal FileRoute? OpenTicket(string token, string method)
    {
        try
        {
            var route = JsonSerializer.Deserialize<FileRoute>(files.Unprotect(token));
            return route?.Method == method && route.ExpiresAt > clock.GetUtcNow() ? route : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return null; }
    }

    public void Dispose() { publicClient.Dispose(); trustedClient.Dispose(); }
}
