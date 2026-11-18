using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using StreamKit.Core.Events;
using StreamKit.Core.Options;
using StreamKit.Core.Rules;
using StreamKit.Web;

namespace StreamKit.App;

/// <summary>
/// Ventana única. Solo pinta estado y traduce clics a llamadas de <see cref="AppComposition"/>:
/// la lógica de red, puntos y OBS nunca vive en el hilo de UI.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppComposition _composition;

    private readonly TextBox _host = new();
    private readonly TextBox _signingKey = new();
    private readonly CheckBox _simulator = new();
    private readonly NumericUpDown _eventsPerMinute = new();
    private readonly CheckBox _autoConnect = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new();
    private readonly Label _connectionState = new();

    private readonly NumericUpDown _perDiamond = new();
    private readonly NumericUpDown _perChat = new();
    private readonly NumericUpDown _chatCooldown = new();
    private readonly NumericUpDown _perMinute = new();
    private readonly NumericUpDown _activeWindow = new();

    private readonly NumericUpDown _webPort = new();
    private readonly TextBox _token = new();
    private readonly TextBox _alertsUrl = new();
    private readonly TextBox _chatUrl = new();
    private readonly TextBox _statsUrl = new();

    private readonly TextBox _obsUrl = new();
    private readonly TextBox _obsPassword = new();
    private readonly NumericUpDown _obsTimeout = new();
    private readonly CheckBox _obsAutoConnect = new();
    private readonly TextBox _rulesJson = new();

    private readonly Label _diagnostics = new();
    private readonly Label _hint = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    private bool _shutdownComplete;

    private static readonly JsonSerializerOptions RuleJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string SampleRules = """
        [
          {
            "id": "regalo-grande",
            "enabled": true,
            "on": "gift",
            "where": { "minDiamonds": 50 },
            "cooldownMs": 5000,
            "actions": [ { "type": "scene", "scene": "Regalos" } ]
          },
          {
            "id": "silencio-ayuda",
            "enabled": false,
            "on": "chat",
            "where": { "command": "!ayuda" },
            "actions": [ { "type": "mute", "source": "Micrófono", "muted": true } ]
          }
        ]
        """;

    public MainForm(AppComposition composition)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        Text = "StreamKit";
        Width = 820;
        Height = 640;
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += MainForm_FormClosing;
        Resize += MainForm_Resize;

        BuildTray();
        BuildLayout();
        LoadSettings(composition.Options);
        RefreshStatus();

        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (!_composition.Options.TikTok.AutoConnect || _composition.IsRunning)
            return;

        try
        {
            await _composition.StartAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            _hint.Text = $"No se pudo conectar automáticamente: {ex.Message}";
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "StreamKit",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        }, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildConnectionPage());
        tabs.TabPages.Add(BuildPointsPage());
        tabs.TabPages.Add(BuildOverlaysPage());
        tabs.TabPages.Add(BuildObsPage());
        tabs.TabPages.Add(BuildDiagnosticsPage());
        root.Controls.Add(tabs, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "Guardar configuración", AutoSize = true };
        save.Click += async (_, _) => await SaveSettingsAsync();
        actions.Controls.Add(save);

        var hide = new Button { Text = "Ocultar en la bandeja", AutoSize = true };
        hide.Click += (_, _) => HideToTray();
        actions.Controls.Add(hide);

        root.Controls.Add(actions, 0, 2);

        _hint.Dock = DockStyle.Fill;
        _hint.AutoSize = true;
        _hint.ForeColor = Color.DimGray;
        root.Controls.Add(_hint, 0, 3);

        Controls.Add(root);
    }

    private TabPage BuildConnectionPage()
    {
        var page = new TabPage("Conexión") { Padding = new Padding(12) };
        var grid = NewGrid();
        var row = 0;

        _host.Width = 320;
        AddField(grid, "Usuario de TikTok", _host, row++);

        _signingKey.UseSystemPasswordChar = true;
        AddField(grid, "Clave de firma (Euler)", _signingKey, row++);

        AddCheck(grid, "Modo simulador (sin clave de firma)", _simulator, row++);

        ConfigureNumber(_eventsPerMinute, 1, 6000, 1);
        AddField(grid, "Eventos simulados por minuto", _eventsPerMinute, row++);

        AddCheck(grid, "Conectar al abrir StreamKit", _autoConnect, row++);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        _connect.Text = "Conectar";
        _connect.AutoSize = true;
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Text = "Desconectar";
        _disconnect.AutoSize = true;
        _disconnect.Click += async (_, _) => await DisconnectAsync();
        buttons.Controls.Add(_connect);
        buttons.Controls.Add(_disconnect);
        grid.Controls.Add(buttons, 1, row++);

        _connectionState.AutoSize = true;
        _connectionState.ForeColor = Color.DimGray;
        grid.Controls.Add(_connectionState, 1, row++);

        var note = new Label
        {
            Text = "Sin clave de firma, StreamKit genera eventos sintéticos para probar puntos, "
                 + "comandos, overlays y reglas de OBS. La clave se guarda cifrada con DPAPI.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(note, 0, row);
        grid.SetColumnSpan(note, 2);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildPointsPage()
    {
        var page = new TabPage("Puntos") { Padding = new Padding(12) };
        var grid = NewGrid();
        var row = 0;

        ConfigureNumber(_perDiamond, 0, 100, 1);
        AddField(grid, "Puntos por diamante", _perDiamond, row++);

        ConfigureNumber(_perChat, 0, 100, 1);
        AddField(grid, "Puntos por mensaje de chat", _perChat, row++);

        ConfigureNumber(_chatCooldown, 0, 3600, 5);
        AddField(grid, "Espera entre puntos por chat (s)", _chatCooldown, row++);

        ConfigureNumber(_perMinute, 0, 100, 1);
        AddField(grid, "Puntos por minuto conectado", _perMinute, row++);

        ConfigureNumber(_activeWindow, 1, 240, 1);
        AddField(grid, "Ventana de actividad (min)", _activeWindow, row++);

        var note = new Label
        {
            Text = "Los puntos se guardan en SQLite con write-behind: el saldo sobrevive al cierre "
                 + "y no se escribe en disco por cada mensaje.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(note, 0, row);
        grid.SetColumnSpan(note, 2);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildOverlaysPage()
    {
        var page = new TabPage("Overlays") { Padding = new Padding(12) };
        var grid = NewGrid();
        var row = 0;

        ConfigureNumber(_webPort, 1024, 65535, 1);
        AddField(grid, "Puerto local", _webPort, row++);

        _token.ReadOnly = true;
        AddField(grid, "Token", _token, row++);

        AddUrlField(grid, "Alertas", _alertsUrl, OverlayChannel.Alerts, ref row);
        AddUrlField(grid, "Chat", _chatUrl, OverlayChannel.Chat, ref row);
        AddUrlField(grid, "Estadísticas", _statsUrl, OverlayChannel.Stats, ref row);

        var note = new Label
        {
            Text = "En OBS: Fuentes → Navegador → pegar la URL. Tamaño recomendado 1920×1080 y "
                 + "«Apagar fuente cuando no esté visible» activado. El puerto se aplica al reiniciar.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(note, 0, row);
        grid.SetColumnSpan(note, 2);

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildObsPage()
    {
        var page = new TabPage("OBS") { Padding = new Padding(12) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var grid = NewGrid();
        var row = 0;

        AddField(grid, "obs-websocket", _obsUrl, row++);

        _obsPassword.UseSystemPasswordChar = true;
        AddField(grid, "Contraseña", _obsPassword, row++);

        ConfigureNumber(_obsTimeout, 1, 120, 1);
        AddField(grid, "Timeout por petición (s)", _obsTimeout, row++);

        AddCheck(grid, "Conectar automáticamente", _obsAutoConnect, row++);

        var note = new Label
        {
            Text = "OBS Studio 28 o superior con obs-websocket 5.x habilitado (por defecto "
                 + "ws://127.0.0.1:4455). Las reglas de abajo se aplican en caliente sobre la "
                 + "conexión actual y se guardan cifradas con el resto de la configuración.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
        };
        grid.Controls.Add(note, 0, row);
        grid.SetColumnSpan(note, 2);

        root.Controls.Add(grid, 0, 0);

        var rules = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 8, 0, 0),
        };
        rules.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rules.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rules.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        rules.Controls.Add(new Label
        {
            Text = "Reglas de automatización (JSON)",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 4),
        }, 0, 0);

        _rulesJson.Multiline = true;
        _rulesJson.AcceptsReturn = true;
        _rulesJson.ScrollBars = ScrollBars.Both;
        _rulesJson.WordWrap = false;
        _rulesJson.Dock = DockStyle.Fill;
        _rulesJson.Font = new Font("Consolas", 9f);
        rules.Controls.Add(_rulesJson, 0, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var apply = new Button { Text = "Aplicar reglas", AutoSize = true };
        apply.Click += async (_, _) => await ApplyRulesAsync();
        var sample = new Button { Text = "Insertar ejemplo", AutoSize = true };
        sample.Click += (_, _) => _rulesJson.Text = SampleRules;
        buttons.Controls.Add(apply);
        buttons.Controls.Add(sample);
        rules.Controls.Add(buttons, 0, 2);

        root.Controls.Add(rules, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildDiagnosticsPage()
    {
        var page = new TabPage("Diagnóstico") { Padding = new Padding(12) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _diagnostics.Dock = DockStyle.Fill;
        _diagnostics.AutoSize = true;
        _diagnostics.Font = new Font("Consolas", 9f);
        root.Controls.Add(_diagnostics, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var test = new Button { Text = "Inyectar chat de prueba", AutoSize = true };
        test.Click += (_, _) => InjectTestChat();
        var logs = new Button { Text = "Abrir carpeta de logs", AutoSize = true };
        logs.Click += (_, _) => OpenFolder(Path.Combine(DataFolder, "logs"));
        var data = new Button { Text = "Abrir carpeta de datos", AutoSize = true };
        data.Click += (_, _) => OpenFolder(DataFolder);
        actions.Controls.Add(test);
        actions.Controls.Add(logs);
        actions.Controls.Add(data);
        root.Controls.Add(actions, 0, 1);

        var note = new Label
        {
            Text = "El chat de prueba usa un usuario ficticio y pasa por el mismo pipeline que TikTok: "
                 + "puntos, comandos y overlays.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
        };
        root.Controls.Add(note, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamKit");

    private async Task ConnectAsync()
    {
        try
        {
            if (!await SaveSettingsAsync(silent: true))
                return;

            await _composition.StartAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo conectar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            await _composition.StopSourceAsync();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo desconectar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<bool> SaveSettingsAsync(bool silent = false)
    {
        var host = _host.Text.Trim();
        var signingKey = _signingKey.Text.Trim();
        var simulator = _simulator.Checked;
        var obsUrl = _obsUrl.Text.Trim();

        if (!simulator && (string.IsNullOrWhiteSpace(signingKey) || string.IsNullOrWhiteSpace(host)))
        {
            MessageBox.Show(
                this,
                "Para el modo real hacen falta el usuario de TikTok y la clave de firma. "
                + "Sin clave, dejá activo el modo simulador.",
                "Configuración incompleta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (!Uri.TryCreate(obsUrl, UriKind.Absolute, out var obsUri) || obsUri.Scheme != "ws")
        {
            MessageBox.Show(
                this,
                "La URL de obs-websocket debe empezar por ws:// (por ejemplo ws://127.0.0.1:4455).",
                "Configuración inválida",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var options = _composition.Options;
        options.TikTok.HostId = host;
        options.TikTok.SigningKey = signingKey;
        options.TikTok.Simulator = simulator;
        options.TikTok.SimulatorEventsPerMinute = (int)_eventsPerMinute.Value;
        options.TikTok.AutoConnect = _autoConnect.Checked;

        options.Points.PerDiamond = (int)_perDiamond.Value;
        options.Points.PerChat = (int)_perChat.Value;
        options.Points.ChatCooldownSeconds = (int)_chatCooldown.Value;
        options.Points.PerMinute = (int)_perMinute.Value;
        options.Points.ActiveWindowMinutes = (int)_activeWindow.Value;

        options.Web.Port = (int)_webPort.Value;

        options.Obs.Url = obsUrl;
        options.Obs.Password = _obsPassword.Text;
        options.Obs.RequestTimeoutSeconds = (int)_obsTimeout.Value;
        options.Obs.AutoConnect = _obsAutoConnect.Checked;

        try
        {
            await _composition.SaveAsync();
            if (!silent)
                _hint.Text = "Configuración guardada de forma cifrada.";

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Configuración inválida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private async Task ApplyRulesAsync()
    {
        List<AutomationRule>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<AutomationRule>>(_rulesJson.Text, RuleJsonOptions);
        }
        catch (JsonException ex)
        {
            MessageBox.Show(
                this,
                $"JSON inválido: {ex.Message}",
                "Reglas OBS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (rules is null)
        {
            MessageBox.Show(
                this,
                "El JSON debe ser una lista de reglas, por ejemplo [ { \"id\": \"...\" } ].",
                "Reglas OBS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            await _composition.ApplyRulesAsync(rules);
            _rulesJson.Text = JsonSerializer.Serialize(_composition.Options.Rules, RuleJsonOptions);
            _hint.Text = rules.Count == 0
                ? "No hay reglas activas."
                : $"{rules.Count} regla(s) aplicadas y guardadas.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Reglas OBS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadSettings(StreamKitOptions options)
    {
        _host.Text = options.TikTok.HostId;
        _signingKey.Text = options.TikTok.SigningKey;
        _simulator.Checked = options.TikTok.Simulator;
        _eventsPerMinute.Value = Math.Clamp(options.TikTok.SimulatorEventsPerMinute, 1, 6000);
        _autoConnect.Checked = options.TikTok.AutoConnect;

        _perDiamond.Value = Math.Clamp(options.Points.PerDiamond, 0, 100);
        _perChat.Value = Math.Clamp(options.Points.PerChat, 0, 100);
        _chatCooldown.Value = Math.Clamp(options.Points.ChatCooldownSeconds, 0, 3600);
        _perMinute.Value = Math.Clamp(options.Points.PerMinute, 0, 100);
        _activeWindow.Value = Math.Clamp(options.Points.ActiveWindowMinutes, 1, 240);

        _webPort.Value = Math.Clamp(options.Web.Port, 1024, 65535);
        _token.Text = options.Web.Token;

        _obsUrl.Text = options.Obs.Url;
        _obsPassword.Text = options.Obs.Password;
        _obsTimeout.Value = Math.Clamp(options.Obs.RequestTimeoutSeconds, 1, 120);
        _obsAutoConnect.Checked = options.Obs.AutoConnect;
        _rulesJson.Text = JsonSerializer.Serialize(options.Rules ?? [], RuleJsonOptions);

        UpdateOverlayUrls();
    }

    private void UpdateOverlayUrls()
    {
        _alertsUrl.Text = _composition.OverlayUrl(OverlayChannel.Alerts);
        _chatUrl.Text = _composition.OverlayUrl(OverlayChannel.Chat);
        _statsUrl.Text = _composition.OverlayUrl(OverlayChannel.Stats);
    }

    private void AddUrlField(TableLayoutPanel grid, string label, TextBox field, OverlayChannel channel, ref int row)
    {
        field.ReadOnly = true;
        AddField(grid, label, field, row);

        var copy = new Button { Text = "Copiar", AutoSize = true, Tag = channel };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(field.Text);
                _hint.Text = $"URL de {label.ToLowerInvariant()} copiada.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Portapapeles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var holder = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        holder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        holder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Dock = DockStyle.Fill;
        holder.Controls.Add(field, 0, 0);
        holder.Controls.Add(copy, 1, 0);
        grid.Controls.Remove(field);
        grid.Controls.Add(holder, 1, row);

        row++;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar StreamKit", null, (_, _) => ShowFromTray());
        menu.Items.Add("Salir", null, (_, _) => Close());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "StreamKit";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        _tray.ShowBalloonTip(1500, "StreamKit", "Sigue corriendo en la bandeja.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RefreshStatus()
    {
        var snapshot = _composition.State.Snapshot();
        var resources = _composition.Resources.Sample();
        var running = _composition.IsRunning;

        _connect.Enabled = !running;
        _disconnect.Enabled = running;
        _connectionState.Text = running
            ? $"Conectado ({_composition.Source.Name}) · {snapshot.TikTokState}"
            : $"Desconectado · {snapshot.TikTokState}";

        _diagnostics.Text = string.Join(Environment.NewLine,
            $"Origen       : {_composition.Source.Name} ({snapshot.TikTokState})",
            $"Canal        : @{snapshot.TikTokHost}",
            $"Espectadores : {snapshot.Viewers}",
            $"Eventos      : {snapshot.EventsProcessed} procesados · {snapshot.EventsDropped} descartados",
            $"Comandos     : {snapshot.CommandsHandled}",
            $"Overlays     : {_composition.Overlays.ClientCount} conectados · cola {_composition.Overlays.QueueDepth}",
            $"OBS          : {(snapshot.ObsConnected ? "conectado" : "desconectado")} · escena {snapshot.ObsScene}",
            $"CPU          : {resources.CpuPercentOfCore:0.0} % de un núcleo",
            $"RAM          : {resources.WorkingSetMb:0.0} MB",
            $"Servidor     : {_composition.Web.BaseUrl}",
            $"Uptime       : {snapshot.Uptime:hh\\:mm\\:ss}");

        if (_token.Text != _composition.Options.Web.Token)
        {
            _token.Text = _composition.Options.Web.Token;
            UpdateOverlayUrls();
        }
    }

    private void InjectTestChat()
    {
        var viewer = new ViewerRef("sim:test", "test_viewer", "Test");
        _composition.Inject(new ChatEvent(DateTimeOffset.UtcNow, viewer, "!puntos"));
        _hint.Text = "Evento de prueba inyectado.";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        Enabled = false;
        try
        {
            await _composition.StopAsync();
            _shutdownComplete = true;
            Close();
        }
        catch (Exception ex)
        {
            Enabled = true;
            e.Cancel = false;
            MessageBox.Show(this, ex.Message, "Error al cerrar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddField(TableLayoutPanel grid, string label, Control control, int row)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 0),
        }, 0, row);

        control.Dock = DockStyle.Top;
        grid.Controls.Add(control, 1, row);
    }

    private static void AddCheck(TableLayoutPanel grid, string text, CheckBox check, int row)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        check.Text = text;
        check.AutoSize = true;
        grid.Controls.Add(check, 1, row);
    }

    private static void ConfigureNumber(NumericUpDown control, int min, int max, int increment)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Increment = increment;
        control.ThousandsSeparator = false;
    }
}
