using System.Security.Cryptography;
using System.Text;

namespace StreamKit.Web;

public enum GuardDecision
{
    Allow = 0,
    Forbidden = 1,
    Unauthorized = 2,
}

public readonly record struct GuardRequest(
    string? Host,
    string? Origin,
    string? Token,
    string? RemoteIp,
    bool RequiresToken);

/// <summary>
/// Política pura de acceso local. "Escuchar en loopback" no alcanza: una página web abierta
/// en la misma máquina puede intentar hablar con 127.0.0.1 (CSRF / DNS rebinding), así que
/// además se valida Host, Origin y token.
/// </summary>
public sealed class LocalRequestGuard
{
    private readonly byte[] _token;

    public LocalRequestGuard(string token, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = Encoding.UTF8.GetBytes(token);
        Port = port;
    }

    public int Port { get; }

    public GuardDecision Check(in GuardRequest request)
    {
        if (!IsLoopbackAddress(request.RemoteIp))
            return GuardDecision.Forbidden;

        if (!IsLoopbackHost(request.Host))
            return GuardDecision.Forbidden;

        if (!string.IsNullOrEmpty(request.Origin) && !IsLoopbackOrigin(request.Origin))
            return GuardDecision.Forbidden;

        if (request.RequiresToken && !TokenMatches(request.Token))
            return GuardDecision.Unauthorized;

        return GuardDecision.Allow;
    }

    private bool TokenMatches(string? provided)
    {
        if (string.IsNullOrEmpty(provided))
            return false;

        var bytes = Encoding.UTF8.GetBytes(provided);
        return bytes.Length == _token.Length && CryptographicOperations.FixedTimeEquals(bytes, _token);
    }

    private bool IsLoopbackOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("http" or "https"))
            return false;

        if (uri.Port != Port)
            return false;

        return IsLoopbackName(uri.Host);
    }

    private bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var value = host.Trim();

        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0)
                return false;

            var address = value[1..end];
            var rest = value[(end + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], out var ipv6Port) || ipv6Port != Port))
                return false;

            return address == "::1";
        }

        var colon = value.LastIndexOf(':');
        string name;
        if (colon > 0)
        {
            name = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], out var port) || port != Port)
                return false;
        }
        else
        {
            name = value;
        }

        return IsLoopbackName(name);
    }

    private static bool IsLoopbackName(string name)
        => name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || name.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackAddress(string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp))
            return true; // transporte en memoria (tests) o Unix socket

        return remoteIp is "127.0.0.1" or "::1" or "::ffff:127.0.0.1";
    }
}
