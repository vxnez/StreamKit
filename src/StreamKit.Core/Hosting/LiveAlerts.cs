using StreamKit.Core.Alerts;
using StreamKit.Core.Events;
using StreamKit.Core.Options;

namespace StreamKit.Core.Hosting;

/// <summary>
/// Traduce un evento del directo a la alerta que ve el espectador. Puro: no toca OBS, ni la cola,
/// ni el reloj. Los campos de <see cref="AlertItem.Data"/> son los que consume <c>overlay.js</c>.
/// </summary>
public static class LiveAlerts
{
    public static AlertItem? From(StreamEvent evt, StreamKitOptions options)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(options);

        return evt switch
        {
            GiftEvent gift => ForGift(gift, options),
            FollowEvent follow => ForViewer(
                follow.Viewer,
                AlertKind.Follow,
                AlertPriority.Follow,
                Math.Max(500, options.Alerts.FollowDurationMs),
                "nuevo seguidor"),
            SubscribeEvent subscribe => ForViewer(
                subscribe.Viewer,
                AlertKind.Subscribe,
                AlertPriority.Subscribe,
                Math.Max(500, options.Alerts.SubscribeDurationMs),
                subscribe.Months > 1 ? $"se suscribió por {subscribe.Months} meses" : "se suscribió",
                new Dictionary<string, object?> { ["months"] = Math.Max(1, subscribe.Months) }),
            ShareEvent share => ForViewer(
                share.Viewer,
                AlertKind.Share,
                AlertPriority.Share,
                Math.Max(500, options.Alerts.DefaultDurationMs),
                "compartió el directo"),
            LikeEvent like => ForLike(like, options),
            _ => null,
        };
    }

    private static AlertItem ForGift(GiftEvent gift, StreamKitOptions options)
    {
        var count = Math.Max(1, gift.Count);
        var text = count > 1
            ? $"{gift.Viewer.DisplayName} envió {count} × {gift.GiftName}"
            : $"{gift.Viewer.DisplayName} envió {gift.GiftName}";

        return new AlertItem
        {
            Id = AlertItem.NewId(),
            Kind = AlertKind.Gift,
            Priority = AlertPriority.Gift,
            Text = text,
            DurationMs = Math.Max(500, options.Alerts.GiftDurationMs),
            Count = count,
            MergeKey = $"gift:{gift.Viewer.UserId}:{gift.GiftId}",
            Data = ViewerData(gift.Viewer, new Dictionary<string, object?>
            {
                ["gift"] = gift.GiftName,
                ["giftId"] = gift.GiftId,
                ["count"] = count,
                ["diamonds"] = gift.TotalDiamonds,
            }),
        };
    }

    private static AlertItem ForLike(LikeEvent like, StreamKitOptions options)
    {
        var count = Math.Max(1, like.Count);
        var text = count == 1
            ? $"{like.Viewer.DisplayName} dejó un like"
            : $"{like.Viewer.DisplayName} dejó {count} likes";

        return new AlertItem
        {
            Id = AlertItem.NewId(),
            Kind = AlertKind.Like,
            Priority = AlertPriority.Like,
            Text = text,
            DurationMs = Math.Max(500, options.Alerts.LikeDurationMs),
            Count = count,
            MergeKey = $"like:{like.Viewer.UserId}",
            Data = ViewerData(like.Viewer, new Dictionary<string, object?>
            {
                ["count"] = count,
            }),
        };
    }

    private static AlertItem ForViewer(
        ViewerRef viewer,
        AlertKind kind,
        int priority,
        int durationMs,
        string detail,
        Dictionary<string, object?>? extra = null)
    {
        var data = ViewerData(viewer, extra);

        return new AlertItem
        {
            Id = AlertItem.NewId(),
            Kind = kind,
            Priority = priority,
            Text = $"{viewer.DisplayName} {detail}",
            DurationMs = durationMs,
            Data = data,
        };
    }

    private static Dictionary<string, object?> ViewerData(
        ViewerRef viewer,
        Dictionary<string, object?>? extra)
    {
        var data = new Dictionary<string, object?>
        {
            ["user"] = viewer.UniqueId,
            ["nickname"] = viewer.DisplayName,
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
                data[pair.Key] = pair.Value;
        }

        return data;
    }
}
