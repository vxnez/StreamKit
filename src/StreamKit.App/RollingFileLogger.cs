using System.Text;
using System.Threading.Channels;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;

namespace StreamKit.App;

public sealed class RollingFileLogger : IAppLog, IAsyncDisposable
{
    private const int QueueCapacity = 1024;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly object _memorySync = new();
    private readonly List<LogEntry> _memory = new();
    private readonly string _filePath;
    private readonly AppLogLevel _minimumLevel;
    private readonly long _maxBytes;
    private readonly int _retainFiles;
    private readonly int _memoryLimit;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _writer;
    private bool _disposed;

    public RollingFileLogger(LogOptions options, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _minimumLevel = options.Level;
        _maxBytes = Math.Max(64 * 1024L, options.MaxFileMb * 1024L * 1024L);
        _retainFiles = Math.Clamp(options.RetainFiles, 1, 50);
        _memoryLimit = Math.Clamp(options.MemoryEntries, 1, 10_000);
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamKit",
            "logs",
            "streamkit.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _writer = Task.Run(() => WriteLoopAsync(_disposeCts.Token), CancellationToken.None);
    }

    public IReadOnlyList<LogEntry> RecentEntries
    {
        get
        {
            lock (_memorySync)
                return _memory.ToArray();
        }
    }

    public bool IsEnabled(AppLogLevel level) => !_disposed && level >= _minimumLevel;

    public void Log(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
            return;

        var entry = new LogEntry(DateTimeOffset.UtcNow, level, category, Redact(message), exception);
        lock (_memorySync)
        {
            _memory.Add(entry);
            if (_memory.Count > _memoryLimit)
                _memory.RemoveRange(0, _memory.Count - _memoryLimit);
        }

        _queue.Writer.TryWrite(entry);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.Writer.TryComplete();

        // Se le da al escritor la oportunidad de vaciar lo que ya está en cola; si el disco está
        // trabado, se corta a los dos segundos para no colgar el cierre de la app.
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await AppendAsync(entry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // El logging no debe tumbar la aplicación ni generar un bucle de errores.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task AppendAsync(LogEntry entry, CancellationToken ct)
    {
        var line = Format(entry);
        var bytes = Encoding.UTF8.GetByteCount(line);
        var currentLength = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        if (currentLength > 0 && currentLength + bytes > _maxBytes)
            Rotate();

        await File.AppendAllTextAsync(_filePath, line, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private void Rotate()
    {
        for (var index = _retainFiles - 1; index >= 1; index--)
        {
            var source = $"{_filePath}.{index}";
            var target = $"{_filePath}.{index + 1}";
            if (File.Exists(source))
                File.Move(source, target, overwrite: true);
        }

        if (File.Exists(_filePath))
            File.Move(_filePath, _filePath + ".1", overwrite: true);
    }

    private static string Format(LogEntry entry)
    {
        var exception = entry.Exception is null
            ? string.Empty
            : $" exception={entry.Exception.GetType().Name}:{Redact(entry.Exception.Message)}";
        return $"{entry.At:O} [{entry.Level}] [{Redact(entry.Category)}] {Redact(entry.Message)}{exception}{Environment.NewLine}";
    }

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = value;
        foreach (var marker in new[] { "api_key", "apikey", "password", "token", "streamkey", "stream_key", "authorization" })
        {
            var start = 0;
            while ((start = result.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var separator = result.IndexOfAny(new[] { '=', ':', ' ' }, start + marker.Length);
                if (separator < 0)
                    break;

                var valueStart = separator + 1;
                while (valueStart < result.Length && (result[valueStart] == ' ' || result[valueStart] == '"'))
                    valueStart++;
                var valueEnd = valueStart;
                while (valueEnd < result.Length && !char.IsWhiteSpace(result[valueEnd]) && result[valueEnd] is not ',' and not '"' and not ';')
                    valueEnd++;
                result = result[..valueStart] + "[REDACTED]" + result[valueEnd..];
                start = valueStart + "[REDACTED]".Length;
            }
        }

        return result;
    }
}
