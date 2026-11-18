using DeltaTor.Core;

namespace DeltaTor.App.Controls;

/// <summary>
/// The chain card at the top of the drawer's flow. It used to sit above the
/// action pills on the main screen; here it keeps the same visibility rule -
/// it exists once Tor is up or the chain is doing something - and its own
/// status line still decides what a tap does (connect sheet while idle, end
/// the chain while in flight). The drawer closes itself first so the notice
/// or sheet it raises is not stacked over the sheet that would still be
/// covering the screen.
/// </summary>
public sealed partial class DrawerPanel
{
    private const float ChainCardH = 48f;

    private void BuildChain(Flow flow)
    {
        var w = flow.W;
        var x = CardMargin;
        var cardW = w - CardMargin * 2f;
        // Read once per rebuild: the drawer rebuilds on every state tick.
        var chain = AppState.Chain;
        var connected = AppState.State.Connected;
        // Same rule the home screen had: the card exists once Tor is up or
        // the chain is doing something. A chain connect without Tor's SOCKS
        // bridge is refused by the engine, so an always-visible card would
        // only offer a dead end.
        if (chain.Status == ChainStatus.Idle && !connected) return;

        flow.Add(14f, null);
        flow.Add(ChainCardH,
            (g, sy) => DrawChainCard(g, new RectangleF(x, sy, cardW, ChainCardH), chain, connected),
            () => CloseThen(() => ChainTap?.Invoke()));
        flow.Add(7f, (g, sy) => PaintGradientLine(g, sy, w));
    }

    /// <summary>ProxyChainCard (MainActivity 2204): a 48px row whose own
    /// state decides what a tap does. Negotiation is amber "connecting...",
    /// never green, and the host stays out of the card until it is live.</summary>
    private void DrawChainCard(Graphics g, RectangleF rect, ChainState chain, bool connected)
    {
        var active = chain.Status == ChainStatus.Connecting ||
                     chain.Status == ChainStatus.Connected ||
                     chain.Status == ChainStatus.Stopping;
        var failed = chain.Status == ChainStatus.Failed;

        using (var path = DrawUtil.RoundedRect(rect, 14f))
        {
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                rect, DeltaTorTheme.Surface, DeltaTorTheme.SurfaceAlt, 0f);
            g.FillPath(brush, path);
            using var pen = new Pen(DeltaTorTheme.BorderLight);
            g.DrawPath(pen, path);
        }

        string? headline = chain.Status switch
        {
            ChainStatus.Connecting => "connecting\u2026",
            ChainStatus.Connected => chain.UrlMs >= 0
                ? $"on \u00b7 {chain.UrlMs}ms \u2014 tap to disconnect"
                : "on \u2014 tap to disconnect",
            ChainStatus.Stopping => "stopping the chain\u2026",
            ChainStatus.Failed =>
                chain.Message.Length > 0 ? chain.Message : "failed \u00b7 back on Tor",
            _ => connected ? null : "connect with Tor first"
        };
        var headlineColor = failed
            ? DeltaTorTheme.Red
            : chain.Status == ChainStatus.Connecting
                ? DeltaTorTheme.Amber
                : chain.Status == ChainStatus.Connected
                    ? DeltaTorTheme.Green
                    : DeltaTorTheme.Muted;

        // Two lines: "PROXY CHAIN" carries the weight, the small muted line
        // carries the live status.
        var titleH = _bodyLineH;
        var headH = headline != null ? _tinyLineH : 0f;
        var blockH = titleH + (headline != null ? 2f + headH : 0f);
        var ty = rect.Y + (rect.Height - blockH) / 2f;
        using (var brush = new SolidBrush(DeltaTorTheme.Text))
            DrawUtil.DrawSpaced(g, "PROXY CHAIN", _fBodyBold, brush,
                rect.X + 16f, ty, rect.Width - 60f, 0f);
        if (headline != null)
        {
            using var brush = new SolidBrush(headlineColor);
            DrawUtil.DrawSpaced(g, headline, _fTiny, brush,
                rect.X + 16f, ty + titleH + 2f, rect.Width - 60f, 0.6f);
        }

        // Trailing glyph: connected reads as a dot, any active state as an
        // ellipsis, an idle card as a chevron.
        var glyph = chain.Status == ChainStatus.Connected
            ? "\u25CF"
            : active ? "\u2026" : "\u25B8";
        var glyphColor = chain.Status == ChainStatus.Connecting
            ? DeltaTorTheme.Amber
            : chain.Status == ChainStatus.Connected
                ? DeltaTorTheme.Green
                : failed ? DeltaTorTheme.Red : DeltaTorTheme.Accent;
        using var glyphBrush = new SolidBrush(glyphColor);
        var glyphW = DrawUtil.SpacedWidth(glyph, _fBodyBold, 1.2f);
        DrawUtil.DrawSpaced(g, glyph, _fBodyBold, glyphBrush,
            rect.Right - 16f - glyphW, rect.Y + (rect.Height - _bodyLineH) / 2f,
            glyphW + 2f, 1.2f);
    }
}