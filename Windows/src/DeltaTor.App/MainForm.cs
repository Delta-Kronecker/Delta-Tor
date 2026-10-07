namespace DeltaTor.App;

/// <summary>
/// Stage-1 skeleton window. The real UI (ring button, header, drawer with
/// the 7 advanced cards) lands in stage 3 and follows the Android layout 1:1.
/// Palette here is the exact theme hexes from ui/DeltaTorTheme.kt.
/// </summary>
public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "DeltaTOR";
        BackColor = Color.FromArgb(0x12, 0x14, 0x1C);   // Bg
        ForeColor = Color.FromArgb(0xF5, 0xF7, 0xFC);   // Text
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 680);

        var wordmark = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0xB7, 0x9C, 0xFF),  // AccentLight
            Text = "DELTA TOR",
            Location = new Point(16, 16),
        };
        Controls.Add(wordmark);

        var stageNote = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(0x78, 0x82, 0x9B),  // Muted
            Text = "stage 1/5 — skeleton (see PLAN.md)",
            Location = new Point(16, 56),
        };
        Controls.Add(stageNote);
    }
}
