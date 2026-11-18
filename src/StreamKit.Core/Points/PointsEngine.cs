using StreamKit.Core.Abstractions;
using StreamKit.Core.Commands;
using StreamKit.Core.Events;

namespace StreamKit.Core.Points;

/// <summary>
/// Política de puntos. La persistencia y su caché viven en <see cref="IViewerStore"/>.
/// </summary>
public sealed class PointsEngine(
    IViewerStore store,
    CooldownTracker cooldowns,
    IClock clock,
    Options.StreamKitOptions options)
{
    public async Task<long> AwardForGiftAsync(GiftEvent gift, CancellationToken ct)
    {
        // Solo se abona al cerrar la ráfaga: la plataforma repite el evento por cada regalo.
        if (!gift.StreakFinished)
            return 0;

        var diamonds = Math.Max(1, gift.Diamonds);
        var points = (long)diamonds * Math.Max(1, gift.Count) * Math.Max(0, options.Points.PerDiamond);
        if (points <= 0)
            return 0;

        await store.TouchAsync(gift.Viewer, gift.At, ct).ConfigureAwait(false);
        await store.RegisterGiftAsync(gift.Viewer, gift.TotalDiamonds, gift.At, ct).ConfigureAwait(false);

        return await store
            .AddPointsAsync(gift.Viewer, points, $"gift:{gift.GiftId}", clock.UtcNow, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Devuelve null cuando el usuario está en su ventana de espera de puntos por chat.</summary>
    public async Task<long?> AwardForChatAsync(ChatEvent chat, CancellationToken ct)
    {
        var perChat = Math.Max(0, options.Points.PerChat);
        if (perChat == 0)
            return null;

        var window = TimeSpan.FromSeconds(Math.Max(0, options.Points.ChatCooldownSeconds));
        if (!cooldowns.TryAcquire($"chat:{chat.Viewer.UserId}", window))
            return null;

        await store.TouchAsync(chat.Viewer, chat.At, ct).ConfigureAwait(false);
        return await store
            .AddPointsAsync(chat.Viewer, perChat, "chat", clock.UtcNow, ct)
            .ConfigureAwait(false);
    }

    public async Task<long> AwardAsync(ViewerRef viewer, long delta, string reason, CancellationToken ct)
    {
        if (delta == 0)
            return await store.GetPointsAsync(viewer.UserId, ct).ConfigureAwait(false);

        await store.TouchAsync(viewer, clock.UtcNow, ct).ConfigureAwait(false);
        return await store.AddPointsAsync(viewer, delta, reason, clock.UtcNow, ct).ConfigureAwait(false);
    }

    public Task<bool> TransferAsync(ViewerRef from, string targetUniqueIdOrId, long amount, CancellationToken ct)
        => store.TryTransferAsync(from.UserId, targetUniqueIdOrId, amount, clock.UtcNow, ct);

    public Task<long> GetPointsAsync(string userId, CancellationToken ct)
        => store.GetPointsAsync(userId, ct);

    public Task<ViewerBalance?> FindAsync(string uniqueIdOrUserId, CancellationToken ct)
        => store.FindAsync(uniqueIdOrUserId, ct);

    public Task<IReadOnlyList<ViewerBalance>> GetTopAsync(int count, CancellationToken ct)
        => store.GetTopAsync(count, ct);

    public async Task<int> AwardWatchTimeAsync(CancellationToken ct)
    {
        var perMinute = Math.Max(0, options.Points.PerMinute);
        if (perMinute == 0)
            return 0;

        var window = TimeSpan.FromMinutes(Math.Max(1, options.Points.ActiveWindowMinutes));
        return await store
            .AwardActiveViewersAsync(perMinute, window, clock.UtcNow, ct)
            .ConfigureAwait(false);
    }
}
