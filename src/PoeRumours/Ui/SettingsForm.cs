using System.Drawing;
using System.Windows.Forms;

namespace PoeRumours;

// Settings. There is exactly one that matters — the game's language — and one thing to report: which build
// this is (R2).
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    // True when the user picked a different language, i.e. the caller must restart to apply it.
    public bool LanguageChanged { get; private set; }

    public SettingsForm(AppConfig config, IReadOnlyList<string> locales)
    {
        _config = config;

        Text = "PoE Rumours — settings";
        Icon = AppIcon.Window();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 150);

        var label = new Label { Text = "Game language", AutoSize = true, Location = new Point(16, 20) };
        _lang.Location = new Point(16, 42);
        _lang.Width = 140;
        foreach (var l in locales) _lang.Items.Add(l);
        _lang.SelectedItem = locales.Contains(config.Language) ? config.Language : locales[0];

        // R3: never auto-detected. A wrong guess here does not fail loudly — it fails as an app that reads the
        // screen, matches nothing, and shows an empty list, which looks exactly like a tile with no rumours.
        var hint = new Label
        {
            Text = "Must match the client's language. Applied on restart.",
            AutoSize = false,
            Location = new Point(16, 72),
            Size = new Size(310, 18),
            ForeColor = SystemColors.GrayText,
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(150, 110), Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(240, 110), Width = 80 };

        // Fixed width up to where the buttons begin, and ellipsised rather than allowed to grow. A version
        // string is not something we control — the SDK can make it as long as it likes — so an AutoSize label
        // here just walks over the buttons, which is exactly what it did.
        var version = new Label
        {
            Text = $"version {AppVersion.Current}",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(16, 110),
            Size = new Size(ok.Left - 16 - 12, 23),
            ForeColor = SystemColors.GrayText,
        };

        Controls.AddRange([label, _lang, hint, version, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            var picked = (string)_lang.SelectedItem!;
            if (picked != _config.Language)
            {
                _config.Language = picked;
                _config.Save();
                LanguageChanged = true;
            }
        }
        base.OnFormClosing(e);
    }
}
