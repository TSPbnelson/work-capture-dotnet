using WorkCapture.Config;
using WorkCapture.Updater;

namespace WorkCapture.App;

/// <summary>
/// Settings dialog for configuring application options (GitHub token, etc.)
/// </summary>
public class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly AppUpdater _updater;

    private TextBox _tokenTextBox = null!;
    private Button _testButton = null!;
    private Button _saveButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;

    private bool _tokenChanged;
    private string _originalToken;

    public SettingsForm(Settings settings, AppUpdater updater)
    {
        _settings = settings;
        _updater = updater;
        _originalToken = settings.Update.GitHubToken;

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        // Form properties
        Text = "WorkCapture Settings";
        Size = new Size(500, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);

        // --- GitHub Token section ---
        var tokenLabel = new Label
        {
            Text = "GitHub Token (for app updates)",
            Location = new Point(20, 20),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        _tokenTextBox = new TextBox
        {
            Location = new Point(20, 45),
            Size = new Size(440, 25),
            PasswordChar = '*',
            MaxLength = 200
        };

        // Show masked token if one exists
        if (!string.IsNullOrEmpty(_originalToken))
        {
            _tokenTextBox.Text = _originalToken;
            _tokenTextBox.ForeColor = SystemColors.WindowText;
        }
        else
        {
            _tokenTextBox.PlaceholderText = "ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        }

        _tokenTextBox.TextChanged += (s, e) => _tokenChanged = true;

        // Help text
        var helpLabel1 = new Label
        {
            Text = "Get a token at: https://github.com/settings/tokens",
            Location = new Point(20, 80),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };

        var helpLabel2 = new Label
        {
            Text = "Required scope: repo (Full control of private repositories)",
            Location = new Point(20, 98),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };

        // Make help URL clickable
        var linkLabel = new LinkLabel
        {
            Text = "Open GitHub Token Settings",
            Location = new Point(20, 122),
            AutoSize = true
        };
        linkLabel.LinkClicked += (s, e) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/settings/tokens",
                UseShellExecute = true
            });
        };

        // Status label for test results
        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(20, 155),
            Size = new Size(440, 20),
            ForeColor = SystemColors.GrayText
        };

        // --- Buttons ---
        _testButton = new Button
        {
            Text = "Test Token",
            Location = new Point(130, 190),
            Size = new Size(100, 32)
        };
        _testButton.Click += OnTestTokenClick;

        _saveButton = new Button
        {
            Text = "Save",
            Location = new Point(240, 190),
            Size = new Size(100, 32)
        };
        _saveButton.Click += OnSaveClick;

        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(350, 190),
            Size = new Size(100, 32)
        };
        _cancelButton.Click += OnCancelClick;

        // Set accept/cancel buttons for Enter/Escape
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        // Add controls
        Controls.AddRange([
            tokenLabel,
            _tokenTextBox,
            helpLabel1,
            helpLabel2,
            linkLabel,
            _statusLabel,
            _testButton,
            _saveButton,
            _cancelButton
        ]);
    }

    private async void OnTestTokenClick(object? sender, EventArgs e)
    {
        var token = _tokenTextBox.Text.Trim();

        if (string.IsNullOrEmpty(token))
        {
            SetStatus("Enter a token first.", Color.OrangeRed);
            return;
        }

        if (!token.StartsWith("ghp_") && !token.StartsWith("github_pat_"))
        {
            SetStatus("Token should start with 'ghp_' or 'github_pat_'", Color.OrangeRed);
            return;
        }

        // Disable button during test
        _testButton.Enabled = false;
        _testButton.Text = "Testing...";
        SetStatus("Checking token against GitHub API...", SystemColors.GrayText);

        try
        {
            // Create a temporary updater with the new token to test it
            using var testUpdater = new AppUpdater(token);
            var (release, error) = await testUpdater.CheckForUpdate();

            if (release != null)
            {
                var versionText = release.UpdateAvailable
                    ? $"Latest: v{release.LatestVersion} (update available!)"
                    : $"Latest: v{release.LatestVersion} (you're up to date)";
                SetStatus($"\u2713 Token valid! {versionText}", Color.Green);
            }
            else
            {
                SetStatus($"\u2717 Token invalid: {error}", Color.OrangeRed);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"\u2717 Error: {ex.Message}", Color.OrangeRed);
        }
        finally
        {
            _testButton.Enabled = true;
            _testButton.Text = "Test Token";
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var token = _tokenTextBox.Text.Trim();

        // Validate token format if not empty
        if (!string.IsNullOrEmpty(token) && !token.StartsWith("ghp_") && !token.StartsWith("github_pat_"))
        {
            SetStatus("Token should start with 'ghp_' or 'github_pat_'", Color.OrangeRed);
            return;
        }

        // Save to settings
        _settings.Update.GitHubToken = token;
        _settings.Save();

        // Update the existing updater with the new token
        _updater.UpdateToken(token);

        Logger.Info("Settings saved (GitHub token updated)");

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
