using System.Globalization;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Commands;
using StreamKit.Core.Events;
using StreamKit.Core.Points;
using StreamKit.Core.State;

namespace StreamKit.Core.Commands;

public enum PermissionLevel
{
    Everyone = 0,
    Moderator = 1,
    Owner = 2,
}

public sealed record CommandDefinition(
    string Name,
    string Description,
    string Usage,
    PermissionLevel Permission,
    int CooldownSeconds,
    int PerUserCooldownSeconds,
    IReadOnlyList<string> Aliases);

public sealed record CommandResponse(string Text, bool IsError = false)
{
    public static CommandResponse Ok(string text) => new(text);

    public static CommandResponse Error(string text) => new(text, IsError: true);
}

/// <summary>
/// Enrutador de comandos de chat. Todo el estado de tiempos vive en RAM; solo los puntos
/// (y los movimientos con valor de auditoría) tocan la base.
/// </summary>
public sealed class CommandRouter
{
    private const int RateWindowSeconds = 10;

    private readonly PointsEngine _points;
    private readonly CooldownTracker _cooldowns;
    private readonly RateLimiter _rate;
    private readonly RuntimeState _state;
    private readonly Options.StreamKitOptions _options;
    private readonly Dictionary<string, CommandDefinition> _byName;

    public CommandRouter(
        IViewerStore store,
        PointsEngine points,
        CooldownTracker cooldowns,
        RateLimiter rate,
        RuntimeState state,
        Options.StreamKitOptions options)
    {
        _points = points;
        _cooldowns = cooldowns;
        _rate = rate;
        _state = state;
        _options = options;

        Commands =
        [
            new("puntos", "Muestra tus puntos", "!puntos", PermissionLevel.Everyone, 0, 3, ["pts"]),
            new("top", "Ranking de puntos", "!top", PermissionLevel.Everyone, 30, 30, ["ranking"]),
            new("dados", "Tira un dado de 6 caras", "!dados", PermissionLevel.Everyone, 0, 5, ["dado"]),
            new("comandos", "Lista los comandos disponibles", "!comandos", PermissionLevel.Everyone, 30, 30, ["ayuda"]),
            new("regalar", "Transfiere puntos a otro espectador", "!regalar <usuario> <cantidad>", PermissionLevel.Everyone, 0, 30, ["transferir"]),
            new("sumar", "Suma puntos a un espectador (moderadores)", "!sumar <usuario> <cantidad>", PermissionLevel.Moderator, 0, 0, ["dar"]),
        ];

        _byName = new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Commands)
        {
            _byName[definition.Name] = definition;
            foreach (var alias in definition.Aliases)
                _byName[alias] = definition;
        }
    }

    public IReadOnlyList<CommandDefinition> Commands { get; }

    public async Task<CommandResponse?> HandleAsync(ChatEvent chat, CancellationToken ct)
    {
        if (!_options.Commands.Enabled)
            return null;

        if (!CommandParser.TryParse(
                chat.Message,
                _options.Commands.Prefix.Length == 1 ? _options.Commands.Prefix[0] : '!',
                Math.Max(16, _options.Commands.MaxMessageLength),
                out var parsed))
        {
            return null;
        }

        if (!_rate.TryAcquire(
                $"cmd-rate:{chat.Viewer.UserId}",
                Math.Max(1, _options.Commands.MaxPerUserPer10s),
                TimeSpan.FromSeconds(RateWindowSeconds)))
        {
            return null;
        }

        if (!_byName.TryGetValue(parsed.Name, out var definition))
            return null;

        if (!HasPermission(chat, definition.Permission))
            return null;

        var globalKey = $"cmd:{definition.Name}";
        if (!_cooldowns.TryAcquire(globalKey, TimeSpan.FromSeconds(definition.CooldownSeconds)))
            return null;

        var userKey = $"cmdu:{definition.Name}:{chat.Viewer.UserId}";
        if (!_cooldowns.TryAcquire(userKey, TimeSpan.FromSeconds(definition.PerUserCooldownSeconds)))
            return null;

        _state.MarkCommand();
        return await ExecuteAsync(definition.Name, parsed, chat, ct).ConfigureAwait(false);
    }

    private async Task<CommandResponse> ExecuteAsync(
        string name,
        ParsedCommand parsed,
        ChatEvent chat,
        CancellationToken ct)
    {
        var display = chat.Viewer.DisplayName;

        switch (name)
        {
            case "puntos":
            {
                var balance = await _points.GetPointsAsync(chat.Viewer.UserId, ct).ConfigureAwait(false);
                return CommandResponse.Ok($"{display}, tenés {Format(balance)} puntos.");
            }

            case "top":
            {
                var top = await _points.GetTopAsync(3, ct).ConfigureAwait(false);
                if (top.Count == 0)
                    return CommandResponse.Ok("Todavía no hay puntos registrados.");

                var parts = top.Select((v, i) =>
                    $"{i + 1}) {v.Nickname} {Format(v.Points)}");
                return CommandResponse.Ok("Top 3 · " + string.Join(" · ", parts));
            }

            case "dados":
            {
                var roll = Random.Shared.Next(1, 7);
                return CommandResponse.Ok($"{display} sacó {roll} en el dado.");
            }

            case "comandos":
            {
                var names = string.Join(" ", Commands
                    .Where(c => c.Permission == PermissionLevel.Everyone)
                    .Select(c => $"{_options.Commands.Prefix}{c.Name}"));
                return CommandResponse.Ok($"Comandos: {names}");
            }

            case "regalar":
            {
                var target = parsed.Arg(0).TrimStart('@');
                if (target.Length == 0 || !TryParseAmount(parsed.Arg(1), out var amount))
                    return CommandResponse.Error($"Uso: {_options.Commands.Prefix}regalar <usuario> <cantidad>");

                if (string.Equals(target, chat.Viewer.UniqueId, StringComparison.OrdinalIgnoreCase))
                    return CommandResponse.Error("No podés regalarte puntos a vos mismo.");

                var ok = await _points.TransferAsync(chat.Viewer, target, amount, ct).ConfigureAwait(false);
                return ok
                    ? CommandResponse.Ok($"{display} regaló {Format(amount)} puntos a @{target}.")
                    : CommandResponse.Error($"No se pudo transferir: revisá el usuario y tu saldo.");
            }

            case "sumar":
            {
                var target = parsed.Arg(0).TrimStart('@');
                if (target.Length == 0 || !TryParseAmount(parsed.Arg(1), out var amount))
                    return CommandResponse.Error($"Uso: {_options.Commands.Prefix}sumar <usuario> <cantidad>");

                var viewer = await _points.FindAsync(target, ct).ConfigureAwait(false);
                if (viewer is null)
                    return CommandResponse.Error($"No encontré a @{target}.");

                var balance = await _points.AwardAsync(
                    new ViewerRef(viewer.UserId, viewer.UniqueId, viewer.Nickname),
                    amount,
                    $"manual:{chat.Viewer.UniqueId}",
                    ct).ConfigureAwait(false);

                return CommandResponse.Ok($"{viewer.Nickname} ahora tiene {Format(balance)} puntos.");
            }

            default:
                return CommandResponse.Error("Comando no implementado.");
        }
    }

    private static bool HasPermission(ChatEvent chat, PermissionLevel required) => required switch
    {
        PermissionLevel.Owner => chat.IsOwner,
        PermissionLevel.Moderator => chat.IsModerator || chat.IsOwner,
        _ => true,
    };

    private static bool TryParseAmount(string raw, out long amount)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)
           && amount is > 0 and <= 1_000_000;

    private static string Format(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));
}
