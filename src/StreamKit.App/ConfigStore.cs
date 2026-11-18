using System.Security.Cryptography;
using System.Text.Json;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;

namespace StreamKit.App;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly IAppLog _log;

    public ConfigStore(string? path = null, IAppLog? log = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamKit",
            "settings.dat");
        _log = log ?? NullAppLog.Instance;
    }

    public string Path => _path;

    public async Task<StreamKitOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return new StreamKitOptions();

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var json = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var options = JsonSerializer.Deserialize<StreamKitOptions>(json, JsonOptions) ?? new StreamKitOptions();
            return options;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("config", "No se pudo descifrar la configuración; se usarán valores seguros.", ex);
            return new StreamKitOptions();
        }
    }

    public async Task SaveAsync(StreamKitOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = options.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidDataException(string.Join(" ", errors));

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("La ruta de configuración no tiene directorio.");

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(options, JsonOptions);
        var protectedBytes = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var temporaryPath = _path + ".tmp";

        await File.WriteAllBytesAsync(temporaryPath, protectedBytes, ct).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
