using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>One row of the raw key/value table under the form.</summary>
/// <remarks><see cref="Original"/> is what the row was filled with. A row is only written back when
/// it differs, so unchanged rows cannot overwrite edits made in the form above.</remarks>
public sealed class ServerPropertyRow
{
    public string Key { get; init; } = "";
    public string Original { get; init; } = "";
    public string Value { get; set; } = "";

    public bool Changed => !string.Equals(Value ?? "", Original, StringComparison.Ordinal);
}

/// <summary>
/// A form over a server's <c>server.properties</c>: the keys people usually change, plus a table of
/// every key in the file.
/// </summary>
/// <remarks>
/// <para><see cref="ServerPropertiesService"/> keeps comments, key order, unknown keys and line
/// endings, and the form only writes keys whose value was changed. Saving a fresh server therefore
/// does not add dozens of guessed defaults, which matters because modded servers write their own
/// settings into this file.</para>
/// <para>Created from code so it can take its host window and read-only flag as constructor
/// arguments.</para>
/// </remarks>
public sealed partial class ServerPropertiesEditor : UserControl
{
    private readonly MainWindow? _shell;
    private readonly bool _canEdit;

    private ServerPropertiesFile? _file;
    private string? _dir;
    private bool _running;

    // Set while the form is being filled, so those writes are not taken for user edits.
    private bool _filling;

    // What the form showed when last loaded or saved. Save writes only fields that differ from
    // this, which keeps untouched keys out of the file.
    private Snapshot _saved = new();

    private readonly ObservableCollection<ServerPropertyRow> _rawRows = new();

    public ServerPropertiesEditor(MainWindow? shell, bool canEdit)
    {
        InitializeComponent();
        _shell = shell;
        _canEdit = canEdit;
        RawGrid.ItemsSource = _rawRows;
        RawGrid.IsReadOnly = !canEdit;

        if (!canEdit)
        {
            const string why = "You have read-only access to this instance, so its server settings "
                             + "cannot be changed here.";
            foreach (var control in new FrameworkElement[] { SaveButton, AddKeyButton })
            {
                control.IsEnabled = false;
                control.ToolTip = why;
                ToolTipService.SetShowOnDisabled(control, true);
            }
        }
    }

    /// <summary>The folder whose <c>server.properties</c> is on screen, or null before the first
    /// <see cref="Load"/>.</summary>
    public string? ServerRunDir => _dir;

    /// <summary>True when the form holds changes that are not in the file.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The port the form is currently showing. The hosting page prints it in its connect
    /// hint, so it has to follow the box rather than the file.</summary>
    public int Port { get; private set; } = 25565;

    /// <summary>Raised whenever <see cref="IsDirty"/> changes, so the host can enable its own
    /// "you have unsaved server settings" affordances.</summary>
    public event Action<bool>? DirtyChanged;

    /// <summary>A sentence for the host's status line. Never an exception's own words.</summary>
    public event Action<string>? Status;

    /// <summary>Raised after a save that changed the port.</summary>
    public event Action<int>? PortChanged;

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the file in <paramref name="serverRunDir"/> and fills the form.
    /// </summary>
    /// <remarks>A folder with no file yet is not an error: the form shows Minecraft's defaults, so
    /// it works before the server has ever run.</remarks>
    public void Load(string serverRunDir)
    {
        _dir = serverRunDir;
        try
        {
            _file = ServerPropertiesService.Read(serverRunDir);
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-props", ex);
            _file = null;
            PathLabel.Text = ServerPropertiesService.PathFor(serverRunDir);
            ShowNotice("This server.properties could not be read, so the form is showing nothing. "
                     + "The full error is in the launcher log.");
            Status?.Invoke("server.properties could not be read.");
            return;
        }

        FillForm();
    }

    /// <summary>Re-reads the file, but only when nothing would be lost. Used when something else
    /// on the page writes a property (the whitelist switch) and the form is sitting clean.</summary>
    public void ReloadIfClean()
    {
        if (IsDirty || _dir is null) return;
        Load(_dir);
    }

    /// <summary>Tells the form whether the server is up, which changes what a save means.</summary>
    public void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        UpdateNotice();
    }

    private void FillForm()
    {
        if (_file is null) return;
        var p = _file.Properties;

        _filling = true;
        try
        {
            PathLabel.Text = _file.ExistsOnDisk
                ? _file.Path
                : _file.Path + " - not written yet; the server writes it on its first start";

            MotdBox.Text = p.Motd;
            LevelNameBox.Text = p.LevelName;
            LevelSeedBox.Text = p.LevelSeed;
            SpawnProtectionBox.Text = p.SpawnProtection.ToString(CultureInfo.InvariantCulture);
            MaxWorldSizeBox.Text = p.MaxWorldSize.ToString(CultureInfo.InvariantCulture);
            SelectByText(GamemodeBox, p.Gamemode);
            SelectByText(DifficultyBox, p.Difficulty);
            HardcoreCheck.IsChecked = p.Hardcore;
            AllowNetherCheck.IsChecked = p.AllowNether;
            CommandBlockCheck.IsChecked = p.EnableCommandBlock;

            MaxPlayersBox.Text = p.MaxPlayers.ToString(CultureInfo.InvariantCulture);
            IdleTimeoutBox.Text = p.PlayerIdleTimeout.ToString(CultureInfo.InvariantCulture);
            ViewDistanceBox.Text = p.ViewDistance.ToString(CultureInfo.InvariantCulture);
            SimulationDistanceBox.Text = p.SimulationDistance.ToString(CultureInfo.InvariantCulture);
            OnlineModeCheck.IsChecked = p.OnlineMode;
            WhitelistCheck.IsChecked = p.WhiteList;
            EnforceWhitelistCheck.IsChecked = p.EnforceWhitelist;
            PvpCheck.IsChecked = p.Pvp;
            AllowFlightCheck.IsChecked = p.AllowFlight;

            PortBox.Text = p.ServerPort.ToString(CultureInfo.InvariantCulture);
            QueryPortBox.Text = p.QueryPort.ToString(CultureInfo.InvariantCulture);
            RconPortBox.Text = p.RconPort.ToString(CultureInfo.InvariantCulture);
            RconPasswordBox.Text = p.RconPassword;
            EnableQueryCheck.IsChecked = p.EnableQuery;
            EnableRconCheck.IsChecked = p.EnableRcon;

            Port = p.ServerPort;
            _saved = Capture();
            FillRawRows();
        }
        finally { _filling = false; }

        SetDirty(false);
        UpdateNotice();
    }

    private void FillRawRows()
    {
        _rawRows.Clear();
        if (_file is null) return;
        foreach (var pair in _file.Properties.Pairs)
            _rawRows.Add(new ServerPropertyRow { Key = pair.Key, Original = pair.Value, Value = pair.Value });
        UpdateRawCount();
    }

    private void UpdateRawCount() => RawCountLabel.Text = _rawRows.Count == 1
        ? "1 key in the file"
        : $"{_rawRows.Count:N0} keys in the file";

    private static void SelectByText(ComboBox box, string value)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem { Content: string text }
                && string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }

        // Show values the drop-down does not know (a modded gamemode, "1" for survival) instead of
        // replacing them with the first entry.
        box.SelectedItem = null;
        box.Text = value;
    }

    private static string TextOf(ComboBox box) =>
        box.SelectedItem is ComboBoxItem { Content: string text } ? text : box.Text ?? "";

    // ── change tracking ──────────────────────────────────────────────────────

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_filling) return;
        SetDirty(!Capture().Equals(_saved));
    }

    private void SetDirty(bool dirty)
    {
        var docDirty = _file?.Properties.IsDirty == true;
        var next = dirty || docDirty;
        SaveButton.IsEnabled = next && _canEdit;
        if (IsDirty == next) return;
        IsDirty = next;
        DirtyChanged?.Invoke(next);
    }

    private Snapshot Capture() => new()
    {
        Motd = MotdBox.Text,
        LevelName = LevelNameBox.Text,
        LevelSeed = LevelSeedBox.Text,
        SpawnProtection = SpawnProtectionBox.Text,
        MaxWorldSize = MaxWorldSizeBox.Text,
        Gamemode = TextOf(GamemodeBox),
        Difficulty = TextOf(DifficultyBox),
        Hardcore = HardcoreCheck.IsChecked == true,
        AllowNether = AllowNetherCheck.IsChecked == true,
        CommandBlock = CommandBlockCheck.IsChecked == true,
        MaxPlayers = MaxPlayersBox.Text,
        IdleTimeout = IdleTimeoutBox.Text,
        ViewDistance = ViewDistanceBox.Text,
        SimulationDistance = SimulationDistanceBox.Text,
        OnlineMode = OnlineModeCheck.IsChecked == true,
        Whitelist = WhitelistCheck.IsChecked == true,
        EnforceWhitelist = EnforceWhitelistCheck.IsChecked == true,
        Pvp = PvpCheck.IsChecked == true,
        AllowFlight = AllowFlightCheck.IsChecked == true,
        Port = PortBox.Text,
        QueryPort = QueryPortBox.Text,
        RconPort = RconPortBox.Text,
        RconPassword = RconPasswordBox.Text,
        EnableQuery = EnableQueryCheck.IsChecked == true,
        EnableRcon = EnableRconCheck.IsChecked == true,
    };

    // ── save ─────────────────────────────────────────────────────────────────

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try { await SaveAsync(); }
        catch (Exception ex)
        {
            AppLog.LogError("server-props", ex);
            Status?.Invoke("server.properties could not be saved. The full error is in the launcher log.");
        }
    }

    /// <summary>
    /// Writes the form back. Returns false when nothing was written: an invalid number, a file that
    /// changed underneath us, or a declined overwrite.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (_file is null || _dir is null || !_canEdit) return false;

        var errors = new List<string>();
        var port = Int(PortBox, "Port", errors, 1, 65535);
        var queryPort = Int(QueryPortBox, "Query port", errors, 1, 65535);
        var rconPort = Int(RconPortBox, "RCON port", errors, 1, 65535);
        var maxPlayers = Int(MaxPlayersBox, "Max players", errors, 1, 100000);
        var viewDistance = Int(ViewDistanceBox, "View distance", errors, 2, 64);
        var simDistance = Int(SimulationDistanceBox, "Simulation distance", errors, 2, 64);
        var spawnProtection = Int(SpawnProtectionBox, "Spawn protection", errors, 0, 100000);
        var idleTimeout = Int(IdleTimeoutBox, "Idle kick", errors, 0, 100000);
        var maxWorldSize = Long(MaxWorldSizeBox, "Max world size", errors, 1, 29999984);

        if (errors.Count > 0)
        {
            ErrorLabel.Text = string.Join("  ", errors);
            ErrorLabel.Visibility = Visibility.Visible;
            Status?.Invoke("Nothing was saved: " + errors[0]);
            return false;
        }
        ErrorLabel.Visibility = Visibility.Collapsed;

        // Ask before overwriting an edit made since the form was filled (by hand, or by the
        // server itself).
        if (ServerPropertiesService.ChangedOnDisk(_file))
        {
            var overwrite = await AppDialog.ConfirmAsync(_shell,
                "server.properties changed on disk",
                "This file has been edited since the form was filled in - by hand, or by the server "
                + "itself. Saving now writes what is on screen and loses that edit.",
                "Overwrite", "Cancel", danger: true);
            if (!overwrite)
            {
                Status?.Invoke("Nothing was saved. Reload to pick up the change on disk.");
                return false;
            }
        }

        var p = _file.Properties;

        // Raw rows first, and only the ones that were edited; applying unchanged rows would
        // overwrite the form's edits with the old values.
        foreach (var row in _rawRows.Where(r => r.Changed))
            p.Set(row.Key, row.Value ?? "");

        // Then the form, so a key changed in both places ends up as the box shows. Only changed
        // fields: Set() appends missing keys, so writing everything would add every vanilla default.
        if (MotdBox.Text != _saved.Motd) p.Motd = MotdBox.Text;
        if (LevelNameBox.Text != _saved.LevelName) p.LevelName = LevelNameBox.Text.Trim();
        if (LevelSeedBox.Text != _saved.LevelSeed) p.LevelSeed = LevelSeedBox.Text.Trim();
        if (SpawnProtectionBox.Text != _saved.SpawnProtection) p.SpawnProtection = spawnProtection;
        if (MaxWorldSizeBox.Text != _saved.MaxWorldSize) p.MaxWorldSize = maxWorldSize;
        if (TextOf(GamemodeBox) != _saved.Gamemode) p.Gamemode = TextOf(GamemodeBox);
        if (TextOf(DifficultyBox) != _saved.Difficulty) p.Difficulty = TextOf(DifficultyBox);
        if (HardcoreCheck.IsChecked == true != _saved.Hardcore) p.Hardcore = HardcoreCheck.IsChecked == true;
        if (AllowNetherCheck.IsChecked == true != _saved.AllowNether) p.AllowNether = AllowNetherCheck.IsChecked == true;
        if (CommandBlockCheck.IsChecked == true != _saved.CommandBlock) p.EnableCommandBlock = CommandBlockCheck.IsChecked == true;

        if (MaxPlayersBox.Text != _saved.MaxPlayers) p.MaxPlayers = maxPlayers;
        if (IdleTimeoutBox.Text != _saved.IdleTimeout) p.PlayerIdleTimeout = idleTimeout;
        if (ViewDistanceBox.Text != _saved.ViewDistance) p.ViewDistance = viewDistance;
        if (SimulationDistanceBox.Text != _saved.SimulationDistance) p.SimulationDistance = simDistance;
        if (OnlineModeCheck.IsChecked == true != _saved.OnlineMode) p.OnlineMode = OnlineModeCheck.IsChecked == true;
        if (WhitelistCheck.IsChecked == true != _saved.Whitelist) p.WhiteList = WhitelistCheck.IsChecked == true;
        if (EnforceWhitelistCheck.IsChecked == true != _saved.EnforceWhitelist) p.EnforceWhitelist = EnforceWhitelistCheck.IsChecked == true;
        if (PvpCheck.IsChecked == true != _saved.Pvp) p.Pvp = PvpCheck.IsChecked == true;
        if (AllowFlightCheck.IsChecked == true != _saved.AllowFlight) p.AllowFlight = AllowFlightCheck.IsChecked == true;

        if (PortBox.Text != _saved.Port) p.ServerPort = port;
        if (QueryPortBox.Text != _saved.QueryPort) p.QueryPort = queryPort;
        if (RconPortBox.Text != _saved.RconPort) p.RconPort = rconPort;
        if (RconPasswordBox.Text != _saved.RconPassword) p.RconPassword = RconPasswordBox.Text;
        if (EnableQueryCheck.IsChecked == true != _saved.EnableQuery) p.EnableQuery = EnableQueryCheck.IsChecked == true;
        if (EnableRconCheck.IsChecked == true != _saved.EnableRcon) p.EnableRcon = EnableRconCheck.IsChecked == true;

        if (!p.IsDirty)
        {
            SetDirty(false);
            Status?.Invoke("Nothing to save - the file already says this.");
            return true;
        }

        var previousPort = Port;
        var file = _file;
        _file = await Task.Run(() => ServerPropertiesService.Save(file));
        AppLog.Log("server", $"Wrote {_file.Path}.");

        FillForm();
        Status?.Invoke("Saved server.properties.");
        if (Port != previousPort) PortChanged?.Invoke(Port);
        return true;
    }

    private static int Int(TextBox box, string label, List<string> errors, int min, int max)
    {
        var text = (box.Text ?? "").Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max)
            return value;
        errors.Add($"{label} must be a whole number between {min} and {max}.");
        return min;
    }

    private static long Long(TextBox box, string label, List<string> errors, long min, long max)
    {
        var text = (box.Text ?? "").Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max)
            return value;
        errors.Add($"{label} must be a whole number between {min} and {max}.");
        return min;
    }

    // ── raw table ────────────────────────────────────────────────────────────

    private void OnRawCellEdited(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // The binding has not written the new value back yet at this point, so the dirty flag is
        // set on the next turn of the dispatcher rather than from the value in the cell.
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() => SetDirty(true)));
    }

    private async void OnAddKey(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_shell is null || _file is null || !_canEdit) return;
            var key = await _shell.PromptAsync("Add a server property", "Key", "");
            key = (key ?? "").Trim();
            if (key.Length == 0) return;
            if (_file.Properties.Contains(key))
            {
                Status?.Invoke($"'{key}' is already in the file.");
                RawExpander.IsExpanded = true;
                return;
            }

            // Appended rather than rebuilding the table, which would lose other unsaved row edits.
            _file.Properties.Set(key, "");
            _rawRows.Add(new ServerPropertyRow { Key = key, Original = "", Value = "" });
            UpdateRawCount();
            RawExpander.IsExpanded = true;
            SetDirty(true);
            Status?.Invoke($"Added '{key}'. Give it a value and save.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-props", ex);
            Status?.Invoke("That key could not be added. The full error is in the launcher log.");
        }
    }

    private async void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_file is null || !_canEdit) return;
            if (RawGrid.SelectedItem is not ServerPropertyRow row) return;

            var ok = await AppDialog.ConfirmAsync(_shell, "Remove this key",
                $"'{row.Key}' will be taken out of server.properties when you save. Minecraft writes "
                + "the key back with its own default the next time the server starts.",
                "Remove", "Cancel", danger: true);
            if (!ok) return;

            _file.Properties.Remove(row.Key);
            _rawRows.Remove(row);
            UpdateRawCount();
            SetDirty(true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-props", ex);
            Status?.Invoke("That key could not be removed. The full error is in the launcher log.");
        }
    }

    // ── reload ───────────────────────────────────────────────────────────────

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_dir is null) return;
            if (IsDirty)
            {
                var ok = await AppDialog.ConfirmAsync(_shell, "Throw away these changes",
                    "Reloading reads server.properties again. Everything you changed on this form "
                    + "and have not saved is lost.",
                    "Reload", "Keep editing", danger: true);
                if (!ok) return;
            }
            Load(_dir);
            Status?.Invoke("Reloaded server.properties.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-props", ex);
            Status?.Invoke("server.properties could not be read. The full error is in the launcher log.");
        }
    }

    // ── notice ───────────────────────────────────────────────────────────────

    private void UpdateNotice()
    {
        if (_file is null) return;
        var notes = new List<string>();
        if (_running)
            notes.Add("The server is running. Saved changes take effect the next time it starts - "
                    + "and the whitelist and op lists are better changed from the Players tab, which "
                    + "sends the command instead.");
        if (!_file.ExistsOnDisk)
            notes.Add("This server has never run, so the file is not there yet. Saving writes it.");

        if (notes.Count == 0) NoticePanel.Visibility = Visibility.Collapsed;
        else ShowNotice(string.Join(" ", notes));
    }

    private void ShowNotice(string text)
    {
        NoticeLabel.Text = text;
        NoticePanel.Visibility = Visibility.Visible;
    }

    /// <summary>The form's values as plain data, so "did anything change" is one comparison rather
    /// than twenty-four.</summary>
    private readonly record struct Snapshot
    {
        public string Motd { get; init; }
        public string LevelName { get; init; }
        public string LevelSeed { get; init; }
        public string SpawnProtection { get; init; }
        public string MaxWorldSize { get; init; }
        public string Gamemode { get; init; }
        public string Difficulty { get; init; }
        public bool Hardcore { get; init; }
        public bool AllowNether { get; init; }
        public bool CommandBlock { get; init; }
        public string MaxPlayers { get; init; }
        public string IdleTimeout { get; init; }
        public string ViewDistance { get; init; }
        public string SimulationDistance { get; init; }
        public bool OnlineMode { get; init; }
        public bool Whitelist { get; init; }
        public bool EnforceWhitelist { get; init; }
        public bool Pvp { get; init; }
        public bool AllowFlight { get; init; }
        public string Port { get; init; }
        public string QueryPort { get; init; }
        public string RconPort { get; init; }
        public string RconPassword { get; init; }
        public bool EnableQuery { get; init; }
        public bool EnableRcon { get; init; }
    }
}
