namespace DeltaTor.App;

/// <summary>
/// The exact DeltaTor (win) theme — dark, borderless, fully owner-drawn look.
/// Colors are copied 1:1 from the Android DeltaTorTheme.kt object, which
/// itself was copied 1:1 from the former WinForms TorJetUi.cs theme
/// (TorJet Core) — so these hexes are the original Windows values.
///
/// Typography keeps the WinForms pt ladder documented next to the Android sp
/// sizes: Big 18 / Title 11 / H2 9.5 / Body 9.25 / Small 8 / Caption 7.25.
/// </summary>
public static class DeltaTorTheme
{
    public static readonly Color Bg = Color.FromArgb(0xFF, 0x12, 0x14, 0x1C);
    public static readonly Color Surface = Color.FromArgb(0xFF, 0x1A, 0x1D, 0x26);
    public static readonly Color SurfaceAlt = Color.FromArgb(0xFF, 0x23, 0x27, 0x32);
    public static readonly Color SurfaceLight = Color.FromArgb(0xFF, 0x2C, 0x31, 0x3E);
    public static readonly Color Border = Color.FromArgb(0xFF, 0x2D, 0x32, 0x41);
    public static readonly Color BorderLight = Color.FromArgb(0xFF, 0x3C, 0x42, 0x52);
    public static readonly Color Text = Color.FromArgb(0xFF, 0xF5, 0xF7, 0xFC);
    public static readonly Color Muted = Color.FromArgb(0xFF, 0x78, 0x82, 0x9B);
    public static readonly Color Accent = Color.FromArgb(0xFF, 0x8A, 0x5C, 0xF6);
    public static readonly Color AccentLight = Color.FromArgb(0xFF, 0xB7, 0x9C, 0xFF);
    public static readonly Color AccentSoft = Color.FromArgb(0xFF, 0x60, 0x40, 0xBE);
    public static readonly Color AccentDark = Color.FromArgb(0xFF, 0x48, 0x30, 0xA0);
    public static readonly Color Green = Color.FromArgb(0xFF, 0x34, 0xD3, 0x99);
    public static readonly Color GreenLight = Color.FromArgb(0xFF, 0x7D, 0xF3, 0xC0);
    public static readonly Color GreenDark = Color.FromArgb(0xFF, 0x26, 0x9E, 0x76);
    public static readonly Color Red = Color.FromArgb(0xFF, 0xEF, 0x5C, 0x70);
    public static readonly Color Amber = Color.FromArgb(0xFF, 0xF5, 0xB2, 0x3C);
    public static readonly Color AmberLight = Color.FromArgb(0xFF, 0xFF, 0xD5, 0x8A);

    // Material scheme counterparts the Android theme defines (text on colored
    // surfaces): onSecondary sits on Green, onError sits on Red.
    public static readonly Color OnSecondary = Color.FromArgb(0xFF, 0x00, 0x16, 0x11);
    public static readonly Color OnError = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    // Launcher background (Android res drawable color #05091A).
    public static readonly Color LauncherBg = Color.FromArgb(0xFF, 0x05, 0x09, 0x1A);

    /// <summary>Font family: Segoe UI is the WinForms counterpart of Roboto.</summary>
    public const string FontFamilyName = "Segoe UI";

    // WinForms pt ladder (see the class doc); Android sp values in comments.
    public const float BigPt = 18f;        // 28sp bold
    public const float TitlePt = 11f;      // 17sp bold
    public const float H2Pt = 9.5f;        // 15sp bold
    public const float BodyPt = 9.25f;     // 14sp regular
    public const float SmallPt = 8f;       // 12sp regular
    public const float CaptionPt = 7.25f;  // 10sp bold

    // Cached fonts (process lifetime; the ladder is fixed).
    public static readonly Font Big = new(FontFamilyName, BigPt, FontStyle.Bold);
    public static readonly Font Title = new(FontFamilyName, TitlePt, FontStyle.Bold);
    public static readonly Font H2 = new(FontFamilyName, H2Pt, FontStyle.Bold);
    public static readonly Font Body = new(FontFamilyName, BodyPt, FontStyle.Regular);
    public static readonly Font Small = new(FontFamilyName, SmallPt, FontStyle.Regular);
    public static readonly Font Caption = new(FontFamilyName, CaptionPt, FontStyle.Bold);
}
