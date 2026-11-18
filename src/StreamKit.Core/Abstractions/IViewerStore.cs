using StreamKit.Core.Events;

namespace StreamKit.Core.Abstractions;

public sealed record ViewerBalance(
    string UserId,
    string UniqueId,
    string Nickname,
    long Points,
    long LifetimePoints,
    long Gifts,
    long Diamonds);

/// <summary>Persistencia de espectadores. La implementación decide caché y write-behind.</summary>
public interface IViewerStore
{
    Task TouchAsync(ViewerRef viewer, DateTimeOffset at, CancellationToken ct);

    /// <summary>Suma (o resta) puntos y devuelve el saldo resultante.</summary>
    Task<long> AddPointsAsync(ViewerRef viewer, long delta, string reason, DateTimeOffset at, CancellationToken ct);

    Task<long> GetPointsAsync(string userId, CancellationToken ct);

    /// <summary>Busca por handle (@) o por id numérico.</summary>
    Task<ViewerBalance?> FindAsync(string uniqueIdOrUserId, CancellationToken ct);

    Task<IReadOnlyList<ViewerBalance>> GetTopAsync(int count, CancellationToken ct);

    /// <summary>Transferencia atómica; false si el origen no existe o no tiene saldo.</summary>
    Task<bool> TryTransferAsync(string fromUserId, string toUserId, long amount, DateTimeOffset at, CancellationToken ct);

    /// <summary>Puntos por minuto a todos los vistos dentro de la ventana. Devuelve cuántos.</summary>
    Task<int> AwardActiveViewersAsync(int points, TimeSpan activeWindow, DateTimeOffset at, CancellationToken ct);

    Task<int> CountTrackedAsync(CancellationToken ct);

    /// <summary>Contabiliza el regalo para estadísticas (no otorga puntos).</summary>
    Task RegisterGiftAsync(ViewerRef viewer, int diamonds, DateTimeOffset at, CancellationToken ct);

    Task FlushAsync(CancellationToken ct);
}
