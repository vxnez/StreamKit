using System.Diagnostics;

namespace StreamKit.Core.Diagnostics;

public sealed record ResourceSnapshot(
    double CpuPercentOfMachine,
    double CpuPercentOfCore,
    double WorkingSetMb,
    double PrivateMb,
    int Threads,
    double ManagedHeapMb,
    TimeSpan Uptime);

/// <summary>
/// Muestrea CPU y memoria en un hilo de fondo (1 Hz). Los consumidores leen el último valor,
/// así que la UI y /api/diagnostics no compiten por el contador del proceso.
/// </summary>
public sealed class ResourceSampler : IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Task? _loop;
    private ResourceSnapshot _last = new(0, 0, 0, 0, 0, 0, TimeSpan.Zero);

    public void Start(TimeSpan interval)
    {
        if (_loop is not null)
            return;

        _process.Refresh();
        _loop = Task.Run(() => LoopAsync(interval, _cts.Token));
    }

    public ResourceSnapshot Sample() => Volatile.Read(ref _last);

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        var previousCpu = _process.TotalProcessorTime;
        var previousAt = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            _process.Refresh();

            var nowCpu = _process.TotalProcessorTime;
            var nowAt = Stopwatch.GetTimestamp();

            var cpuDeltaSeconds = (nowCpu - previousCpu).TotalSeconds;
            var elapsedSeconds = (nowAt - previousAt) / (double)Stopwatch.Frequency;
            previousCpu = nowCpu;
            previousAt = nowAt;

            var cores = Math.Max(1, Environment.ProcessorCount);
            var ofCore = elapsedSeconds > 0 ? cpuDeltaSeconds / elapsedSeconds * 100 : 0;

            var snapshot = new ResourceSnapshot(
                CpuPercentOfMachine: ofCore / cores,
                CpuPercentOfCore: ofCore,
                WorkingSetMb: _process.WorkingSet64 / 1024d / 1024d,
                PrivateMb: _process.PrivateMemorySize64 / 1024d / 1024d,
                Threads: _process.Threads.Count,
                ManagedHeapMb: GC.GetTotalMemory(false) / 1024d / 1024d,
                Uptime: DateTimeOffset.UtcNow - _startedAt);

            Volatile.Write(ref _last, snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // esperado
            }
        }

        _cts.Dispose();
        _process.Dispose();
    }
}
