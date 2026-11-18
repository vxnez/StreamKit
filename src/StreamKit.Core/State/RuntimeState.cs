namespace StreamKit.Core.State;

/// <summary>Estado observable por la UI y por /api/state. Escrituras atómicas, lecturas sin lock.</summary>
public sealed class RuntimeState
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private long _eventsProcessed;
    private long _eventsDropped;
    private long _alertsDropped;
    private long _commandsHandled;
    private long _lastEventUtcTicks;
    private int _viewers;
    private int _queueDepth;
    private int _obsConnected;
    private volatile string _tiktokState = "desconectado";
    private volatile string _tiktokHost = string.Empty;
    private volatile string _obsScene = string.Empty;

    public void SetTikTok(string state, string? host = null)
    {
        _tiktokState = state;
        if (host is not null)
            _tiktokHost = host;
    }

    public void SetViewers(int count) => Volatile.Write(ref _viewers, Math.Max(0, count));

    public void SetObs(bool connected, string? scene = null)
    {
        Volatile.Write(ref _obsConnected, connected ? 1 : 0);
        if (scene is not null)
            _obsScene = scene;
    }

    public void SetQueueDepth(int depth) => Volatile.Write(ref _queueDepth, Math.Max(0, depth));

    public void MarkEvent()
    {
        Interlocked.Increment(ref _eventsProcessed);
        Interlocked.Exchange(ref _lastEventUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void MarkCommand() => Interlocked.Increment(ref _commandsHandled);

    public void MarkEventDropped() => Interlocked.Increment(ref _eventsDropped);

    public void MarkAlertDropped() => Interlocked.Increment(ref _alertsDropped);

    public RuntimeSnapshot Snapshot()
    {
        var lastTicks = Interlocked.Read(ref _lastEventUtcTicks);
        return new RuntimeSnapshot(
            TikTokState: _tiktokState,
            TikTokHost: _tiktokHost,
            Viewers: Volatile.Read(ref _viewers),
            LastEventAt: lastTicks == 0 ? null : new DateTimeOffset(lastTicks, TimeSpan.Zero),
            ObsConnected: Volatile.Read(ref _obsConnected) == 1,
            ObsScene: _obsScene,
            EventsProcessed: Interlocked.Read(ref _eventsProcessed),
            EventsDropped: Interlocked.Read(ref _eventsDropped),
            AlertsDropped: Interlocked.Read(ref _alertsDropped),
            CommandsHandled: Interlocked.Read(ref _commandsHandled),
            QueueDepth: Volatile.Read(ref _queueDepth),
            StartedAt: _startedAt,
            Uptime: DateTimeOffset.UtcNow - _startedAt);
    }
}

public sealed record RuntimeSnapshot(
    string TikTokState,
    string TikTokHost,
    int Viewers,
    DateTimeOffset? LastEventAt,
    bool ObsConnected,
    string ObsScene,
    long EventsProcessed,
    long EventsDropped,
    long AlertsDropped,
    long CommandsHandled,
    int QueueDepth,
    DateTimeOffset StartedAt,
    TimeSpan Uptime);
