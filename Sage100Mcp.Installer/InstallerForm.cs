using System.ComponentModel;
using System.Diagnostics;

namespace Sage100Mcp.Installer;

/// <summary>
/// Assistant d'installation en une seule fenêtre. L'interface est construite dans le code (pas de
/// fichier Designer) : peu de contrôles, et une seule source de vérité pour la mise en page.
/// </summary>
internal sealed class InstallerForm : Form
{
    private const string RuntimeDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string DefaultServerName = "Sage 100";

    private readonly TextBox _installDir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serverUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _licenseKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly BindingList<SageDatabase> _databases = [];
    private readonly DataGridView _grid = new();
    private readonly CheckBox _registerClaude = new() { Text = "Déclarer le serveur MCP dans Claude Desktop, sous le nom :", AutoSize = true, Checked = true };
    private readonly TextBox _claudeName = new() { Width = 200, Text = DefaultServerName };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _runtimeStatus = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
    private readonly LinkLabel _runtimeLink = new() { AutoSize = true, Text = "Télécharger .NET 10", Visible = false };
    private readonly Button _install = new() { Text = "Installer", AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
    private readonly Button _close = new() { Text = "Fermer", AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
    private readonly List<Control> _inputs = [];

    public InstallerForm()
    {
        Text = "Installation du serveur MCP Sage 100";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 720);
        Size = new Size(900, 780);
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();

        var existingDir = ClaudeDesktopConfig.FindInstalledLauncherDir();
        _installDir.Text = existingDir ?? Installation.DefaultInstallDir;
        _serverUrl.Text = InstallSettings.DefaultServerUrl;
        LoadExisting(_installDir.Text);
        CheckRuntimes();
    }

    // --- Mise en page --------------------------------------------------------------------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // intro
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // runtimes
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // dossier
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // licence
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));   // bases
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Claude
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));   // journal
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // boutons

        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(840, 0),
            Text = "Cet assistant installe le lanceur du serveur MCP Sage 100, déclare les bases Sage à interroger " +
                   "et ajoute le serveur à Claude Desktop. Il peut être relancé pour modifier la configuration : " +
                   "l'installation existante est reprise telle quelle."
        });

        var runtimes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        runtimes.Controls.Add(_runtimeStatus);
        runtimes.Controls.Add(_runtimeLink);
        _runtimeLink.LinkClicked += (_, _) => OpenUrl(RuntimeDownloadUrl);
        root.Controls.Add(runtimes);

        // Dossier
        var browse = new Button { Text = "Parcourir…", AutoSize = true };
        browse.Click += (_, _) => BrowseInstallDir();
        root.Controls.Add(Group("Dossier d'installation", Row(_installDir, browse)));

        // Licence
        var license = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        license.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        license.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        license.Controls.Add(FieldLabel("Serveur de licences"), 0, 0);
        license.Controls.Add(_serverUrl, 1, 0);
        license.Controls.Add(FieldLabel("Clé de licence"), 0, 1);
        license.Controls.Add(_licenseKey, 1, 1);
        root.Controls.Add(Group("Licence", license));

        // Bases
        ConfigureGrid();
        var addFiles = new Button { Text = "Ajouter des fichiers .gcm / .mae…", AutoSize = true };
        addFiles.Click += (_, _) => AddSageFiles();
        var remove = new Button { Text = "Retirer la ligne", AutoSize = true };
        remove.Click += (_, _) => RemoveSelected();
        var dbButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        dbButtons.Controls.Add(addFiles);
        dbButtons.Controls.Add(remove);
        dbButtons.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(8, 7, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Le nom est celui que l'on cite à Claude (paramètre base_sage). Une seule base par défaut."
        });
        var dbPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dbPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dbPanel.Controls.Add(dbButtons);
        dbPanel.Controls.Add(_grid);
        var dbGroup = Group("Bases Sage 100", dbPanel);
        dbGroup.Dock = DockStyle.Fill;
        root.Controls.Add(dbGroup);

        // Claude Desktop
        _registerClaude.CheckedChanged += (_, _) => _claudeName.Enabled = _registerClaude.Checked;
        var claude = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        claude.Controls.Add(_registerClaude);
        claude.Controls.Add(_claudeName);
        root.Controls.Add(Group("Claude Desktop", claude));

        // Journal
        var logGroup = Group("Journal", _log);
        logGroup.Dock = DockStyle.Fill;
        root.Controls.Add(logGroup);

        // Boutons
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_install);
        _install.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
        AcceptButton = _install;
        root.Controls.Add(buttons);

        Controls.Add(root);
        _inputs.AddRange([_installDir, browse, _serverUrl, _licenseKey, _grid, addFiles, remove, _registerClaude, _claudeName, _install]);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SageDatabase.Name), HeaderText = "Nom", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SageDatabase.Description), HeaderText = "Description", FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SageDatabase.Server), HeaderText = "Serveur SQL", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SageDatabase.Database), HeaderText = "Base SQL", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(SageDatabase.Default), HeaderText = "Défaut", FillWeight = 8 });
        _grid.DataSource = _databases;

        // Case « Défaut » exclusive : cocher une base décoche les autres.
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].DataPropertyName != nameof(SageDatabase.Default)) return;
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            var clicked = _databases[e.RowIndex];
            if (!clicked.Default) return;
            foreach (var db in _databases) db.Default = ReferenceEquals(db, clicked);
            _grid.Refresh();
        };
    }

    private static GroupBox Group(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        content.Dock = content.Dock == DockStyle.None ? DockStyle.Top : content.Dock;
        group.Controls.Add(content);
        return group;
    }

    private static TableLayoutPanel Row(Control main, Control side)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(main, 0, 0);
        row.Controls.Add(side, 1, 0);
        return row;
    }

    private static Label FieldLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 0, 8, 0) };

    // --- Chargement ----------------------------------------------------------------------------

    private void CheckRuntimes()
    {
        var missing = Installation.MissingRuntimes();
        if (missing.Count == 0)
        {
            _runtimeStatus.Text = "✔ Runtimes .NET 10 présents.";
            _runtimeStatus.ForeColor = Color.DarkGreen;
            _runtimeLink.Visible = false;
        }
        else
        {
            _runtimeStatus.Text = $"✖ À installer avant de continuer : {string.Join(" et ", missing)} (x64).";
            _runtimeStatus.ForeColor = Color.Firebrick;
            _runtimeLink.Visible = true;
        }
    }

    private void BrowseInstallDir()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Dossier d'installation du serveur MCP Sage 100",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_installDir.Text) ? _installDir.Text : "",
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _installDir.Text = dialog.SelectedPath;
        LoadExisting(dialog.SelectedPath);
    }

    /// <summary>Reprend la configuration d'une installation existante pour la modifier.</summary>
    private void LoadExisting(string installDir)
    {
        InstallSettings? settings;
        try
        {
            settings = Directory.Exists(installDir) ? InstallSettings.TryLoad(installDir) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            Log($"Configuration existante illisible, ignorée : {ex.Message}");
            return;
        }

        if (settings is null) return;

        _serverUrl.Text = settings.ServerUrl;
        _licenseKey.Text = settings.LicenseKey;
        _databases.Clear();
        foreach (var db in settings.Databases) _databases.Add(db);

        var launcher = Path.Combine(installDir, Installation.LauncherFileName);
        if (ClaudeDesktopConfig.FindRegisteredName(launcher) is { } name) _claudeName.Text = name;

        Log($"Installation existante reprise : {installDir} ({settings.Databases.Count} base(s)).");
    }

    private void AddSageFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Fichiers de base Sage 100",
            Filter = SageFileReader.DialogFilter,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        foreach (var file in dialog.FileNames)
        {
            SageDatabase read;
            try
            {
                read = SageFileReader.Read(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log($"✖ {ex.Message}");
                continue;
            }

            // Le .gcm et le .mae d'une même société pointent souvent sur la même base SQL.
            var same = _databases.FirstOrDefault(d =>
                string.Equals(d.Server, read.Server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Database, read.Database, StringComparison.OrdinalIgnoreCase));
            if (same is not null)
            {
                if (!same.Description.Contains(read.Description, StringComparison.OrdinalIgnoreCase))
                {
                    same.Description = $"{same.Description} + {read.Description.ToLowerInvariant()}";
                }
                Log($"{Path.GetFileName(file)} : base {read.Database} déjà déclarée ({same.Name}).");
                continue;
            }

            read.Name = UniqueName(read.Name);
            read.Default = _databases.Count == 0;
            _databases.Add(read);
            Log($"{Path.GetFileName(file)} → base {read.Database} sur {read.Server}.");
        }
        _grid.Refresh();
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; _databases.Any(d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name}_{i}";
        }
        return candidate;
    }

    private void RemoveSelected()
    {
        foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().ToList())
        {
            if (row.DataBoundItem is SageDatabase db) _databases.Remove(db);
        }
        if (_databases.Count > 0 && !_databases.Any(d => d.Default)) _databases[0].Default = true;
        _grid.Refresh();
    }

    // --- Installation --------------------------------------------------------------------------

    private string? ValidateInputs()
    {
        _grid.EndEdit();

        if (Installation.MissingRuntimes() is { Count: > 0 } missing)
            return $"Installez d'abord {string.Join(" et ", missing)} (x64) : {RuntimeDownloadUrl}";

        var dir = _installDir.Text.Trim();
        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) return "Indiquez un chemin complet pour le dossier d'installation.";

        if (!Uri.TryCreate(_serverUrl.Text.Trim(), UriKind.Absolute, out var url) || (url.Scheme != "https" && url.Scheme != "http"))
            return "L'adresse du serveur de licences est invalide.";
        if (_licenseKey.Text.Trim().Length == 0) return "Saisissez la clé de licence.";

        if (_databases.Count == 0) return "Ajoutez au moins une base Sage (fichier .gcm ou .mae).";
        foreach (var db in _databases)
        {
            if (string.IsNullOrWhiteSpace(db.Name)) return "Chaque base doit avoir un nom.";
            if (string.IsNullOrWhiteSpace(db.Server) || string.IsNullOrWhiteSpace(db.Database))
                return $"Base « {db.Name} » : serveur SQL et base SQL sont obligatoires.";
        }
        var duplicate = _databases.GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) return $"Le nom « {duplicate.Key} » est utilisé par plusieurs bases.";

        if (_registerClaude.Checked && _claudeName.Text.Trim().Length == 0) return "Donnez un nom au serveur MCP pour Claude Desktop.";
        return null;
    }

    private async Task InstallAsync()
    {
        if (ValidateInputs() is { } error)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_databases.Any(d => d.Default)) _databases[0].Default = true;

        var installDir = Path.GetFullPath(_installDir.Text.Trim());
        var launcherPath = Path.Combine(installDir, Installation.LauncherFileName);
        SetBusy(true);
        _log.Clear();

        try
        {
            Log($"Dossier : {installDir}");
            Log(Installation.CopyLauncher(installDir) ? "Lanceur copié." : "Lanceur déjà à jour.");

            var settings = new InstallSettings { ServerUrl = _serverUrl.Text.Trim(), LicenseKey = _licenseKey.Text.Trim() };
            settings.Databases.AddRange(_databases);
            var backup = settings.Save(installDir);
            Log(backup is null ? "Configuration créée." : $"Configuration mise à jour (ancienne version : {Path.GetFileName(backup)}).");

            Log("Téléchargement du serveur MCP autorisé par la licence (jusqu'à une minute si le serveur de licences est en veille)…");
            var (exitCode, output) = await Installation.RunLauncherUpdateAsync(installDir, CancellationToken.None);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Log("  " + line);
            if (exitCode != 0)
            {
                Log("✖ Le serveur MCP n'a pas pu être installé. Vérifiez la clé de licence et l'accès à Internet, puis relancez.");
                MessageBox.Show(this,
                    "Le téléchargement du serveur MCP a échoué (détail dans le journal).\n\n" +
                    "La configuration est enregistrée : corrigez la clé de licence ou l'accès réseau puis cliquez à nouveau sur Installer.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_registerClaude.Checked)
            {
                foreach (var file in ClaudeDesktopConfig.Register(_claudeName.Text.Trim(), launcherPath))
                {
                    Log($"Claude Desktop : serveur « {_claudeName.Text.Trim()} » déclaré dans {file}");
                }
            }

            Log("✔ Installation terminée.");
            var restart = Process.GetProcessesByName("Claude").Length > 0
                ? "\n\nClaude Desktop est ouvert : quittez-le complètement (clic droit sur son icône dans la zone de notification → Quitter) puis rouvrez-le pour charger le serveur."
                : "";
            MessageBox.Show(this, "Le serveur MCP Sage 100 est installé." + restart, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log($"✖ {ex.Message}");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in _inputs) control.Enabled = !busy;
        _claudeName.Enabled = !busy && _registerClaude.Checked;
        UseWaitCursor = busy;
    }

    private void Log(string message)
    {
        _log.AppendText(message + Environment.NewLine);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
