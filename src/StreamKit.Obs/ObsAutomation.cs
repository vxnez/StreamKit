using System.Runtime.CompilerServices;
using System.Threading.Channels;
using StreamKit.Core.Abstractions;
using StreamKit.Core.Events;
using StreamKit.Core.Logging;
using StreamKit.Core.Rules;

namespace StreamKit.Obs;

/// <summary>Ejecuta reglas OBS fuera del pipeline de eventos y en orden estable.</summary>
public sealed class ObsAutomation : IStreamEventHandler, IAsyncDisposable
{
    private const string Category = "obs-automation";
    private const int QueueCapacity = 128;

    private volatile AutomationRule[] _rules;
    private readonly IObsController _obs;
    private readonly IClock _clock;
    private readonly IAppLog _log;
    private readonly Channel<WorkItem> _queue;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _worker;
    private readonly object _cooldownSync = new();
    private readonly Dictionary<AutomationRule, DateTimeOffset> _lastAccepted = new(RuleReferenceComparer.Instance);
    private bool _disposed;

    public ObsAutomation(
        IEnumerable<AutomationRule> rules,
        IObsController obs,
        IClock clock,
        IAppLog? log = null)
    {
        _rules = (rules ?? throw new ArgumentNullException(nameof(rules))).ToArray();
        _obs = obs ?? throw new ArgumentNullException(nameof(obs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? NullAppLog.Instance;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(() => ProcessAsync(_disposeCts.Token), CancellationToken.None);
    }

    public ValueTask HandleAsync(StreamEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_disposed || ct.IsCancellationRequested)
            return ValueTask.CompletedTask;

        var rules = _rules;
        foreach (var rule in rules)
        {
            if (!RuleMatcher.Matches(rule, evt) || !TryAccept(rule))
                continue;

            if (!_queue.Writer.TryWrite(new WorkItem(rule, evt)))
                _log.Warn(Category, $"La cola de automatización está llena; se descarta la regla «{rule.Id}».");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reemplaza el conjunto de reglas sin recrear el trabajador. Se usa cuando la UI guarda
    /// cambios: las reglas nuevas empiezan con los cooldowns limpios.
    /// </summary>
    public void ReplaceRules(IEnumerable<AutomationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules.ToArray();
        lock (_cooldownSync)
            _lastAccepted.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();
        _disposeCts.Cancel();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }

    private bool TryAccept(AutomationRule rule)
    {
        var now = _clock.UtcNow;
        var cooldown = TimeSpan.FromMilliseconds(Math.Max(0, rule.CooldownMs));

        lock (_cooldownSync)
        {
            if (cooldown > TimeSpan.Zero
                && _lastAccepted.TryGetValue(rule, out var last)
                && now - last < cooldown)
            {
                return false;
            }

            _lastAccepted[rule] = now;
            return true;
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (item.Rule.DelayMs > 0)
                        await Task.Delay(item.Rule.DelayMs, ct).ConfigureAwait(false);

                    foreach (var action in item.Rule.Actions)
                        await ExecuteActionAsync(action, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Error(Category, $"La regla «{item.Rule.Id}» produjo un error.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteActionAsync(RuleAction action, CancellationToken ct)
    {
        if (action is null)
        {
            _log.Warn(Category, "Se descartó una acción OBS nula.");
            return;
        }

        var type = action.Type?.Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            _log.Warn(Category, "Se descartó una acción OBS sin tipo.");
            return;
        }

        switch (type.ToLowerInvariant())
        {
            case "scene" when Required(action.Scene, "scene"):
                await _obs.SetSceneAsync(action.Scene!, ct).ConfigureAwait(false);
                break;
            case "sourcevisibility" when Required(action.Scene, "scene") && action.Visible.HasValue && Required(action.Source, "source"):
                await _obs.SetSourceVisibleAsync(action.Scene!, action.Source!, action.Visible.Value, ct).ConfigureAwait(false);
                break;
            case "filter" when Required(action.Source, "source") && Required(action.Filter, "filter") && action.Enabled.HasValue:
                await _obs.SetFilterEnabledAsync(action.Source!, action.Filter!, action.Enabled.Value, ct).ConfigureAwait(false);
                break;
            case "hotkey" when Required(action.Hotkey, "hotkey"):
                await _obs.TriggerHotkeyAsync(action.Hotkey!, ct).ConfigureAwait(false);
                break;
            case "mute" when Required(action.Source, "source") && action.Muted.HasValue:
                await _obs.SetMuteAsync(action.Source!, action.Muted.Value, ct).ConfigureAwait(false);
                break;
            case "raw" when Required(action.RequestType, "requestType"):
                await _obs.SendRawAsync(action.RequestType!, action.RequestData, ct).ConfigureAwait(false);
                break;
            default:
                _log.Warn(Category, $"Se descartó una acción OBS inválida: «{type}».");
                break;
        }
    }

    private bool Required(string? value, string field)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return true;

        _log.Warn(Category, $"Falta el campo «{field}» en una acción OBS.");
        return false;
    }

    private sealed record WorkItem(AutomationRule Rule, StreamEvent Event);

    private sealed class RuleReferenceComparer : IEqualityComparer<AutomationRule>
    {
        public static readonly RuleReferenceComparer Instance = new();

        public bool Equals(AutomationRule? x, AutomationRule? y) => ReferenceEquals(x, y);

        public int GetHashCode(AutomationRule obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
