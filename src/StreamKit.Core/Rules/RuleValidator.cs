namespace StreamKit.Core.Rules;

/// <summary>
/// Validación de reglas antes de guardarlas o aplicarlas. Devuelve mensajes en español listos
/// para la UI; no lanza excepciones.
/// </summary>
public static class RuleValidator
{
    private static readonly HashSet<string> Events = new(StringComparer.OrdinalIgnoreCase)
    {
        "any", "chat", "gift", "like", "follow", "share", "join", "subscribe", "streamended",
    };

    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "scene", "sourcevisibility", "filter", "hotkey", "mute", "raw",
    };

    public static IEnumerable<string> Validate(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var id = string.IsNullOrWhiteSpace(rule.Id) ? "(sin id)" : rule.Id.Trim();

        if (string.IsNullOrWhiteSpace(rule.Id))
            yield return "Cada regla necesita un id único.";

        var on = string.IsNullOrWhiteSpace(rule.On) ? "any" : rule.On.Trim();
        if (!Events.Contains(on))
            yield return $"La regla «{id}» usa un evento desconocido: «{rule.On}».";

        if (rule.CooldownMs < 0 || rule.DelayMs < 0)
            yield return $"La regla «{id}» no admite cooldown ni demora negativos.";

        if (rule.Actions is null || rule.Actions.Count == 0)
        {
            yield return $"La regla «{id}» no tiene acciones.";
            yield break;
        }

        for (var i = 0; i < rule.Actions.Count; i++)
        {
            var action = rule.Actions[i];
            var label = $"La regla «{id}», acción {i + 1}";

            if (action is null)
            {
                yield return $"{label}: está vacía.";
                continue;
            }

            var type = action.Type?.Trim();
            if (string.IsNullOrWhiteSpace(type) || !ActionTypes.Contains(type))
            {
                yield return $"{label}: tipo de acción desconocido «{action.Type}».";
                continue;
            }

            switch (type.ToLowerInvariant())
            {
                case "scene" when string.IsNullOrWhiteSpace(action.Scene):
                    yield return $"{label}: falta la escena.";
                    break;
                case "sourcevisibility":
                    if (string.IsNullOrWhiteSpace(action.Scene))
                        yield return $"{label}: falta la escena.";
                    if (string.IsNullOrWhiteSpace(action.Source))
                        yield return $"{label}: falta la fuente.";
                    if (action.Visible is null)
                        yield return $"{label}: falta visible (true/false).";
                    break;
                case "filter":
                    if (string.IsNullOrWhiteSpace(action.Source))
                        yield return $"{label}: falta la fuente.";
                    if (string.IsNullOrWhiteSpace(action.Filter))
                        yield return $"{label}: falta el filtro.";
                    if (action.Enabled is null)
                        yield return $"{label}: falta enabled (true/false).";
                    break;
                case "hotkey" when string.IsNullOrWhiteSpace(action.Hotkey):
                    yield return $"{label}: falta el nombre del hotkey.";
                    break;
                case "mute":
                    if (string.IsNullOrWhiteSpace(action.Source))
                        yield return $"{label}: falta la fuente.";
                    if (action.Muted is null)
                        yield return $"{label}: falta muted (true/false).";
                    break;
                case "raw" when string.IsNullOrWhiteSpace(action.RequestType):
                    yield return $"{label}: falta requestType.";
                    break;
            }
        }
    }
}
