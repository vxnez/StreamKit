using StreamKit.Core.Events;

namespace StreamKit.Core.Rules;

/// <summary>
/// Evaluación pura de una regla contra un evento. Sin estado, sin OBS y sin reloj: se puede
/// probar con eventos construidos a mano y reutilizar desde la vista previa de la interfaz.
/// </summary>
public static class RuleMatcher
{
    public static bool Matches(AutomationRule rule, StreamEvent evt)
    {
        if (rule is null || !rule.Enabled || evt is null)
            return false;

        var on = string.IsNullOrWhiteSpace(rule.On) ? "any" : rule.On.Trim();
        if (!on.Equals("any", StringComparison.OrdinalIgnoreCase)
            && !on.Equals(evt.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesWhere(rule.Where, evt);
    }

    private static bool MatchesWhere(RuleWhere? where, StreamEvent evt)
    {
        if (where is null)
            return true;

        if (evt is IViewerEvent viewerEvent && !string.IsNullOrWhiteSpace(where.User))
        {
            var wanted = where.User.Trim().TrimStart('@');
            var viewer = viewerEvent.Viewer;
            if (!viewer.UniqueId.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                && !viewer.UserId.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (evt is GiftEvent gift)
        {
            if (!string.IsNullOrWhiteSpace(where.GiftId)
                && !gift.GiftId.Equals(where.GiftId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(where.GiftName)
                && !gift.GiftName.Contains(where.GiftName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (where.MinCount is > 0 && gift.Count < where.MinCount)
                return false;

            if (where.MinDiamonds is > 0 && gift.TotalDiamonds < where.MinDiamonds)
                return false;
        }

        if (evt is ChatEvent chat)
        {
            if (!string.IsNullOrWhiteSpace(where.Command) && !StartsWithCommand(chat.Message, where.Command))
                return false;

            if (!string.IsNullOrWhiteSpace(where.Keyword)
                && !chat.Message.Contains(where.Keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithCommand(string message, string command)
    {
        var text = message.TrimStart();
        var value = command.Trim();

        return text.StartsWith(value, StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("!" + value, StringComparison.OrdinalIgnoreCase);
    }
}
