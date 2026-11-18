namespace StreamKit.Core.Events;

/// <summary>Evento normalizado: el dominio no conoce TikTok ni OBS.</summary>
public abstract record StreamEvent(DateTimeOffset At)
{
    public abstract string Kind { get; }
}

public interface IViewerEvent
{
    ViewerRef Viewer { get; }
}

public sealed record ChatEvent(
    DateTimeOffset At,
    ViewerRef Viewer,
    string Message,
    bool IsModerator = false,
    bool IsSubscriber = false,
    bool IsOwner = false) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "chat";
}

public sealed record GiftEvent(
    DateTimeOffset At,
    ViewerRef Viewer,
    string GiftId,
    string GiftName,
    int Count,
    int Diamonds,
    bool StreakFinished = true) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "gift";

    /// <summary>Diamantes totales de la ráfaga (la plataforma informa el valor unitario).</summary>
    public int TotalDiamonds => Math.Max(0, Diamonds) * Math.Max(1, Count);
}

public sealed record LikeEvent(
    DateTimeOffset At,
    ViewerRef Viewer,
    int Count = 1) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "like";
}

public sealed record FollowEvent(DateTimeOffset At, ViewerRef Viewer) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "follow";
}

public sealed record ShareEvent(DateTimeOffset At, ViewerRef Viewer) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "share";
}

public sealed record JoinEvent(DateTimeOffset At, ViewerRef Viewer) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "join";
}

public sealed record SubscribeEvent(
    DateTimeOffset At,
    ViewerRef Viewer,
    int Months = 1) : StreamEvent(At), IViewerEvent
{
    public override string Kind => "subscribe";
}

public sealed record ViewerCountEvent(DateTimeOffset At, int Count) : StreamEvent(At)
{
    public override string Kind => "viewers";
}

public sealed record StreamEndedEvent(DateTimeOffset At) : StreamEvent(At)
{
    public override string Kind => "streamEnded";
}
