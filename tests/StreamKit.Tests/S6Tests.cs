using System.Text;
using StreamKit.App;
using StreamKit.Core.Logging;
using StreamKit.Core.Options;
using Xunit;

namespace StreamKit.Tests;

public sealed class S6Tests
{
    [Fact]
    public async Task ConfigStore_roundtrips_encrypted_options()
    {
        var path = Path.Combine(Path.GetTempPath(), "StreamKitTests", Guid.NewGuid().ToString("N"), "settings.dat");
        var options = new StreamKitOptions();
        options.TikTok.HostId = "mi_canal";
        options.TikTok.SigningKey = "secret-signing-key";
        options.Obs.Password = "obs-secret";

        var store = new ConfigStore(path);
        await store.SaveAsync(options);
        var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes);
        var loaded = await store.LoadAsync();

        Assert.DoesNotContain("secret-signing-key", text);
        Assert.DoesNotContain("obs-secret", text);
        Assert.Equal("mi_canal", loaded.TikTok.HostId);
        Assert.Equal("secret-signing-key", loaded.TikTok.SigningKey);
        Assert.Equal("obs-secret", loaded.Obs.Password);

        Directory.Delete(Directory.GetParent(path)!.Parent!.FullName, recursive: true);
    }

    [Fact]
    public async Task ConfigStore_returns_defaults_when_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "StreamKitTests", Guid.NewGuid().ToString("N"), "settings.dat");
        var store = new ConfigStore(path);

        var loaded = await store.LoadAsync();

        Assert.True(loaded.TikTok.Simulator);
        Assert.Equal(8787, loaded.Web.Port);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Logger_redacts_secrets_before_writing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamKitTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "streamkit.log");
        await using (var logger = new RollingFileLogger(new LogOptions { Level = AppLogLevel.Debug, MaxFileMb = 1 }, path))
        {
            logger.Info("test", "api_key=plain-api-key password:plain-password token=plain-token");
            await Task.Delay(100);
        }

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("plain-api-key", content);
        Assert.DoesNotContain("plain-password", content);
        Assert.DoesNotContain("plain-token", content);
        Assert.Contains("[REDACTED]", content);
        Directory.Delete(directory, recursive: true);
    }
}
