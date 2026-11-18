using System.Net.WebSockets;

namespace StreamKit.Obs;

/// <summary>No se pudo abrir la conexión con obs-websocket.</summary>
public sealed class ObsConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OBS exige contraseña o la enviada no es la correcta.</summary>
public sealed class ObsAuthenticationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OBS respondió algo que no cumple el protocolo v5.</summary>
public sealed class ObsProtocolException(string message) : Exception(message);

/// <summary>OBS no contestó una petición dentro del plazo configurado.</summary>
public sealed class ObsRequestTimeoutException(string requestType, TimeSpan timeout)
    : Exception($"OBS no respondió a {requestType} en {timeout.TotalSeconds:0.#} s.");

/// <summary>El socket se cerró (por OBS o por la red) mientras había peticiones en vuelo.</summary>
public sealed class ObsConnectionClosedException(
    string message,
    WebSocketCloseStatus? closeStatus = null,
    Exception? inner = null) : Exception(message, inner)
{
    public WebSocketCloseStatus? CloseStatus { get; } = closeStatus;
}
