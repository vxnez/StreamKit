namespace StreamKit.Core.Rules;

/// <summary>Regla de automatización evaluada localmente contra eventos del directo.</summary>
public sealed class AutomationRule
{
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>chat, gift, like, follow, share, join, subscribe, streamEnded, any.</summary>
    public string On { get; set; } = "any";

    public RuleWhere Where { get; set; } = new();

    public int CooldownMs { get; set; }

    public int DelayMs { get; set; }

    public List<RuleAction> Actions { get; set; } = new();
}

public sealed class RuleWhere
{
    public string? GiftId { get; set; }
    public string? GiftName { get; set; }
    public int? MinCount { get; set; }
    public int? MinDiamonds { get; set; }
    public string? User { get; set; }
    public string? Command { get; set; }
    public string? Keyword { get; set; }
}

public sealed class RuleAction
{
    /// <summary>scene | sourceVisibility | filter | hotkey | mute | raw.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Scene { get; set; }
    public string? Source { get; set; }
    public string? Filter { get; set; }
    public string? Hotkey { get; set; }

    public bool? Enabled { get; set; }
    public bool? Visible { get; set; }
    public bool? Muted { get; set; }

    public string? RequestType { get; set; }
    public Dictionary<string, object?>? RequestData { get; set; }
}
