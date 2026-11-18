using System.Globalization;
using StreamKit.Core.Events;

namespace StreamKit.TikTok;

/// <summary>
/// Datos crudos de TikTok, desacoplados de la librería. El mapper y los tests trabajan con
/// estos records; solo <see cref="TikTokEventSource"/> conoce TikTokLiveSharp.
/// </summary>
public readonly record struct TikTokUser(long Id, string UniqueId, string Nickname)
{
    public static TikTokUser Empty => new(0, string.Empty, string.Empty);

    public ViewerRef ToViewer()
    {
        var unique = (UniqueId ?? string.Empty).Trim().TrimStart('@');
        var id = Id > 0
            ? Id.ToString(CultureInfo.InvariantCulture)
            : unique;

        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(unique))
            return ViewerRef.Empty;

        return new ViewerRef(id, unique, Nickname ?? string.Empty);
    }
}

public sealed record TikTokChatMessage(TikTokUser User, string Message);

public sealed record TikTokGiftMessage(
    TikTokUser User,
    long GiftId,
    string GiftName,
    int UnitDiamonds,
    long ComboCount,
    bool StreakFinished);

public sealed record TikTokLikeMessage(TikTokUser User, long Count);

public sealed record TikTokFollowMessage(TikTokUser User);

public sealed record TikTokShareMessage(TikTokUser User);

public sealed record TikTokJoinMessage(TikTokUser User, long ViewerCount);

public sealed record TikTokSubscribeMessage(TikTokUser User, int Months);

public sealed record TikTokViewerCountMessage(long Count);
