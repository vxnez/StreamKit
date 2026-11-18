namespace StreamKit.Core.Options;

public sealed class StreamKitOptions
{
    public TikTokOptions TikTok { get; set; } = new();
    public PointsOptions Points { get; set; } = new();
    public CommandOptions Commands { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public ObsOptions Obs { get; set; } = new();
    public LogOptions Logging { get; set; } = new();
    public List<Rules.AutomationRule> Rules { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (Web.Port is < 1024 or > 65535)
            yield return "Web.Port debe estar entre 1024 y 65535.";
        if (Alerts.MaxDepth is < 1 or > 1024)
            yield return "Alerts.MaxDepth debe estar entre 1 y 1024.";
        if (Points.PerMinute < 0 || Points.PerChat < 0 || Points.PerDiamond < 0)
            yield return "Los valores de puntos no pueden ser negativos.";
        if (Commands.Prefix.Length != 1)
            yield return "Commands.Prefix debe ser exactamente un carácter.";
        if (!Uri.TryCreate(Obs.Url, UriKind.Absolute, out var obsUri) || obsUri.Scheme != "ws")
            yield return "Obs.Url debe ser una URL ws:// válida.";
        if (!string.IsNullOrWhiteSpace(TikTok.HostId) && !TikTok.Simulator && string.IsNullOrWhiteSpace(TikTok.SigningKey))
            yield return "Sin clave de firma, TikTok.Simulator debe estar activo.";
        if (TikTok.SimulatorEventsPerMinute is < 1 or > 6000)
            yield return "TikTok.SimulatorEventsPerMinute debe estar entre 1 y 6000.";
        foreach (var rule in Rules ?? [])
        {
            foreach (var error in StreamKit.Core.Rules.RuleValidator.Validate(rule))
                yield return error;
        }
    }
}

public sealed class TikTokOptions
{
    public string HostId { get; set; } = string.Empty;

    /// <summary>Clave del servicio de firma (Euler Stream). Se guarda cifrada; nunca se loguea.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Servidor de firma alternativo. Vacío = el que trae la librería.</summary>
    public string SignServerUrl { get; set; } = string.Empty;

    /// <summary>Sin clave de firma, la app arranca en simulador para poder probar todo.</summary>
    public bool Simulator { get; set; } = true;

    public int SimulatorEventsPerMinute { get; set; } = 120;

    public int ReconnectMaxSeconds { get; set; } = 30;

    public bool AutoConnect { get; set; }
}

public sealed class PointsOptions
{
    /// <summary>Puntos por diamante de regalo.</summary>
    public int PerDiamond { get; set; } = 1;

    /// <summary>Puntos por mensaje de chat que participa.</summary>
    public int PerChat { get; set; } = 1;

    /// <summary>Ventana mínima entre dos puntos por chat del mismo usuario.</summary>
    public int ChatCooldownSeconds { get; set; } = 60;

    /// <summary>Puntos por minuto de permanencia (espectadores activos).</summary>
    public int PerMinute { get; set; } = 1;

    /// <summary>Se considera activo a quien emitió un evento en esta ventana.</summary>
    public int ActiveWindowMinutes { get; set; } = 10;
}

public sealed class CommandOptions
{
    public bool Enabled { get; set; } = true;
    public string Prefix { get; set; } = "!";

    /// <summary>Tope de comandos por usuario cada 10 s (anti-spam, en RAM).</summary>
    public int MaxPerUserPer10s { get; set; } = 4;

    /// <summary>Longitud máxima de un mensaje que se analiza como comando.</summary>
    public int MaxMessageLength { get; set; } = 300;
}

public sealed class AlertOptions
{
    public int MaxDepth { get; set; } = 64;
    public int GiftDurationMs { get; set; } = 6000;
    public int FollowDurationMs { get; set; } = 4000;
    public int SubscribeDurationMs { get; set; } = 6000;
    public int LikeDurationMs { get; set; } = 2500;
    public int DefaultDurationMs { get; set; } = 4000;

    /// <summary>Margen extra antes de dar por perdido el ack de un overlay colgado.</summary>
    public int AckGraceMs { get; set; } = 2500;

    public int LikeCoalesceMs { get; set; } = 3000;
    public int GiftCoalesceMs { get; set; } = 1500;

    public int MaxClients { get; set; } = 16;

    /// <summary>Si no hay overlay conectado, cuánto se retiene una alerta antes de descartarla.</summary>
    public int NoClientHoldSeconds { get; set; } = 30;
}

public sealed class WebOptions
{
    public int Port { get; set; } = 8787;

    /// <summary>Token de control. Vacío = se genera y persiste al primer arranque.</summary>
    public string Token { get; set; } = string.Empty;

    public int MaxBodyKb { get; set; } = 256;

    /// <summary>Solo se permite loopback. Existe para dejar explícito que no hay modo LAN.</summary>
    public bool LoopbackOnly { get; set; } = true;
}

public sealed class ObsOptions
{
    public string Url { get; set; } = "ws://127.0.0.1:4455";
    public string Password { get; set; } = string.Empty;
    public bool AutoConnect { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 5;
}

public sealed class LogOptions
{
    public Logging.AppLogLevel Level { get; set; } = Logging.AppLogLevel.Info;
    public int MaxFileMb { get; set; } = 5;
    public int RetainFiles { get; set; } = 5;

    /// <summary>Mensajes de log retenidos en memoria para la pestaña Diagnóstico.</summary>
    public int MemoryEntries { get; set; } = 500;
}
