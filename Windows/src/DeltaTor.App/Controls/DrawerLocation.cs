using DeltaTor.Core;

namespace DeltaTor.App.Controls;

/// <summary>
/// The LOCATION half of the drawer's flow: the section header with the live
/// selection summary, the amber caution card, the "Any location" row, and
/// the country directory split into the capacity head and the rest of the
/// world. Every builder measures at layout time (cached font metrics and
/// TextRenderer wraps) and returns a node whose draw closure only paints —
/// rows outside the viewport are skipped by the core loop before any of
/// this runs.
/// </summary>
public sealed partial class DrawerPanel
{
    private const int PickerTop = 25; // EXIT_PICKER_TOP (MainActivity 499)

    private bool _showCountries;
    private bool _showAllCountries;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    private IReadOnlyList<ExitCountry> _directory = Array.Empty<ExitCountry>();
    private int _dirCapCount = -1;

    private void BuildLocation(Flow flow)
    {
        _selected.Clear();
        foreach (var c in ExitNodes.Codes) _selected.Add(c);
        var selection = SelectionSummary();

        AddSectionHeader(flow, "LOCATION", selection, _showCountries,
            () =>
            {
                _showCountries = !_showCountries;
                MarkDirty();
                Invalidate();
            });
        if (!_showCountries) return;

        var canClear = ExitNodes.Codes.Count > 0;
        AddWarnCard(flow, canClear,
            () =>
            {
                ExitNodes.Clear();
                MarkDirty();
                Invalidate();
            });

        AddCountryRow(flow, "🌐", "Any location · default", "--",
            _selected.Count == 0, 0, 0,
            () =>
            {
                ExitNodes.Clear();
                MarkDirty();
                Invalidate();
            }, last: false);

        var directory = Directory();
        if (directory.Count == 0)
        {
            AddLoading(flow);
            return;
        }

        void Row(ExitCountry c, int exits, double share, bool last) =>
            AddCountryRow(flow, UiHelpers.FlagEmoji(c.Code), c.Name, c.Code,
                _selected.Contains(c.Code), exits, share,
                () =>
                {
                    ExitNodes.Toggle(c.Code, c.Name);
                    MarkDirty();
                    Invalidate();
                }, last);

        var capacity = ExitCapacityIndex.ByCountry;
        if (capacity.Count == 0)
        {
            // No exit data yet: the alphabetical directory in one group —
            // an empty picker until a background table arrives would read
            // as broken.
            AddGroupHeader(flow, "COUNTRIES", "EXIT DATA NOT LOADED", true, null);
            for (var i = 0; i < directory.Count; i++)
                Row(directory[i], 0, 0, i == directory.Count - 1);
            return;
        }

        var withExits = directory.Take(PickerTop).ToList();
        var without = directory.Skip(withExits.Count).ToList();

        if (withExits.Count > 0)
        {
            AddGroupHeader(flow,
                "COUNTRIES WITH THE MOST EXIT BANDWIDTH",
                $"TOP {withExits.Count} OF {directory.Count}", false, null);
        }

        for (var i = 0; i < withExits.Count; i++)
        {
            var c = withExits[i];
            capacity.TryGetValue(c.Code, out var cap);
            Row(c, cap?.Exits ?? 0, cap?.Weight ?? 0,
                without.Count == 0 && i == withExits.Count - 1);
        }

        if (without.Count > 0)
        {
            // A country picked before the relay data arrived lives in this
            // group and nowhere else, so the group opens itself rather than
            // hiding the selection.
            var holdsSelection = without.Any(c => _selected.Contains(c.Code));
            AddGroupHeader(flow,
                "REST OF WORLD · MOST HAVE NO EXIT",
                _showAllCountries ? "HIDE" : "SHOW ALL", true,
                () =>
                {
                    // Clear a selection that lives in this group before it
                    // collapses, so nothing gets stuck hidden.
                    if (holdsSelection) ExitNodes.Clear();
                    _showAllCountries = !_showAllCountries;
                    MarkDirty();
                    Invalidate();
                });

            if (_showAllCountries || holdsSelection)
            {
                for (var i = 0; i < without.Count; i++)
                    Row(without[i], 0, 0, i == without.Count - 1);
            }
        }
    }

    private string SelectionSummary()
    {
        var codes = ExitNodes.Codes;
        if (codes.Count == 0) return "Any location · default";
        if (codes.Count == 1)
        {
            var only = codes[0];
            var names = ExitNodes.Names;
            return $"{UiHelpers.FlagEmoji(only)}  {(names.TryGetValue(only, out var n) ? n : "")}".Trim();
        }
        return $"{codes.Count} countries selected";
    }

    /// <summary>The picker order (ExitNodes.order, MainActivity 90): capacity
    /// head first, the rest alphabetical. Cached until the capacity table grows.</summary>
    private IReadOnlyList<ExitCountry> Directory()
    {
        var cap = ExitCapacityIndex.ByCountry;
        if (_dirCapCount == cap.Count && _directory.Count > 0) return _directory;
        var all = BridgeCountries.TopSync();
        if (cap.Count == 0)
        {
            _directory = all;
            _dirCapCount = 0;
            return _directory;
        }
        var byCode = all.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var head = cap
            .OrderByDescending(kv => kv.Value.Weight)
            .Select(kv => byCode.TryGetValue(kv.Key, out var c) ? c : null)
            .Where(c => c != null)
            .Cast<ExitCountry>()
            .ToList();
        var rest = all
            .Where(c => !cap.ContainsKey(c.Code))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _directory = head.Concat(rest).ToList();
        _dirCapCount = cap.Count;
        return _directory;
    }

    // ---- nodes --------------------------------------------------------------

    /// <summary>The amber caution card over the country list (MainActivity 706).</summary>
    private void AddWarnCard(Flow flow, bool canClear, Action onClear)
    {
        var w = flow.W;
        const float marginX = 20f;
        var cardW = w - marginX * 2f;
        var innerX = marginX + 16f;
        var innerW = cardW - 32f;
        var boxX = innerX + 12f;
        var boxW = innerW - 24f;
        var boxRight = innerX + innerW;
        const string paragraph =
            "Picking a country sends your traffic through a relay there. " +
            "It can lower your speed and make the connection less stable.";

        var clearW = canClear ? DrawUtil.SpacedWidth("CLEAR", _fLabel, 1.2f) : 0f;
        var clearX = boxRight - 12f - clearW - 10f;
        var labelX = boxX + 16f + 8f;
        var labelMaxW = (canClear ? clearX : boxRight) - labelX - 8f;

        var headH = Math.Max(16f, _cardTitleLineH);
        var paraSize = Wrap(paragraph, _fBody, boxW);
        var boxH = 11f + headH + 6f + paraSize.Height + 11f;
        var cardH = 8f + boxH + 8f;
        var top = flow.Y;

        flow.Add(cardH, (g, sy) =>
        {
            var card = new RectangleF(marginX, sy, cardW, cardH);
            using (var path = DrawUtil.RoundedRect(card, 18f))
            {
                using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    card, Color.FromArgb(0xFF, 0x20, 0x24, 0x2F),
                    DeltaTorTheme.Surface, 90f);
                g.FillPath(brush, path);
                using var pen = new Pen(DeltaTorTheme.Border);
                g.DrawPath(pen, path);
            }

            var boxY = sy + 8f;
            var box = new RectangleF(innerX, boxY, innerW, boxH);
            using (var path = DrawUtil.RoundedRect(box, 12f))
            {
                using var fill = new SolidBrush(Color.FromArgb(26, DeltaTorTheme.Amber));
                g.FillPath(fill, path);
            }

            Icons.Warn(g,
                new RectangleF(boxX, boxY + 11f + (headH - 16f) / 2f, 16f, 16f),
                DeltaTorTheme.Amber);
            var labelY = boxY + 11f + (headH - _labelLineH) / 2f;
            using (var b = new SolidBrush(DeltaTorTheme.Amber))
                DrawUtil.DrawSpaced(g, "USE ONLY WHEN NEEDED", _fLabel, b,
                    labelX, labelY, labelMaxW, 1.2f);

            if (canClear)
            {
                var clearY = boxY + 11f + (headH - _labelLineH) / 2f - 3f;
                using (var b = new SolidBrush(DeltaTorTheme.Red))
                    DrawUtil.DrawSpaced(g, "CLEAR", _fLabel, b,
                        clearX + 10f, clearY + 3f, clearW + 2f, 1.2f);
                _scrollHits.Add((
                    new RectangleF(clearX, top + (clearY - sy),
                        clearW + 20f, _labelLineH + 6f),
                    onClear));
            }

            DrawWrap(g, paragraph, _fBody, DeltaTorTheme.Muted,
                boxX, sy + 8f + 11f + headH + 6f, paraSize);
        });
    }

    /// <summary>CountryRow (MainActivity 2798): emoji, name, capacity numbers,
    /// code and the check circle, over a DrawerRow divider.</summary>
    private void AddCountryRow(
        Flow flow, string emoji, string name, string code, bool selected,
        int exits, double share, Action onClick, bool last)
    {
        const float inset = 20f;
        var w = flow.W;
        var blockH = Math.Max(17f, Math.Max(_emojiLineH, _nameLineH));
        var h = 10f + blockH + 10f;

        flow.Add(h, (g, sy) =>
        {
            var row = new RectangleF(inset, sy, w - inset * 2f, h);
            if (selected)
            {
                using var path = DrawUtil.RoundedRect(row, 10f);
                using var fill = new SolidBrush(Color.FromArgb(31, DeltaTorTheme.Accent));
                g.FillPath(fill, path);
            }

            var contentY = sy + (h - blockH) / 2f;
            var x = row.X + 8f;
            using (var b = new SolidBrush(DeltaTorTheme.Text))
                g.DrawString(emoji, _fEmoji!, b, x, contentY + (17f - _emojiLineH) / 2f);
            x += 19f + 12f;

            // Capacity numbers sit between the name and the code; measured
            // here because only visible rows ever reach this closure.
            var tailW = 10f + DrawUtil.SpacedWidth(code, _fLabelReg, 1f)
                + 10f + 16f + 8f;
            if (exits > 0)
                tailW += g.MeasureString("00.0%", _fLabel).Width + 8f
                    + g.MeasureString("000", _fLabel).Width + 10f;
            var nameMaxW = row.Right - 8f - x - tailW;
            using (var b = new SolidBrush(
                selected ? DeltaTorTheme.Text : DeltaTorTheme.Muted))
                DrawUtil.DrawSpaced(g, name, selected ? _fNameBold : _fName, b,
                    x, contentY + (blockH - _nameLineH) / 2f, nameMaxW, 0f);

            var right = row.Right - 8f;
            var circle = new RectangleF(right - 16f, sy + (h - 16f) / 2f, 16f, 16f);
            using (var path = DrawUtil.RoundedRect(circle, 8f))
            {
                if (selected)
                {
                    using var fill = new SolidBrush(DeltaTorTheme.AccentLight);
                    g.FillPath(fill, path);
                }
                else
                {
                    using var pen = new Pen(DeltaTorTheme.Border);
                    g.DrawPath(pen, path);
                }
            }
            if (selected)
                Icons.Check(g,
                    new RectangleF(circle.X + 3f, circle.Y + 3f, 10f, 10f),
                    Color.FromArgb(0xFF, 0x14, 0x17, 0x1F));
            right -= 16f + 10f;

            var codeW = DrawUtil.SpacedWidth(code, _fLabelReg, 1f);
            using (var b = new SolidBrush(Color.FromArgb(204, DeltaTorTheme.Muted)))
                DrawUtil.DrawSpaced(g, code, _fLabelReg, b,
                    right - codeW, contentY + (blockH - _labelLineH) / 2f, codeW + 2f, 1f);
            right -= codeW + 10f;

            if (exits > 0)
            {
                var shareText = $"{UiHelpers.Format1((float)(share * 100))}%";
                var shareW = g.MeasureString(shareText, _fLabel).Width;
                var exitsW = g.MeasureString($"{exits}", _fLabel).Width;
                right -= exitsW + 8f;
                using (var b = new SolidBrush(Color.FromArgb(140, DeltaTorTheme.Muted)))
                    g.DrawString($"{exits}", _fLabel, b, right,
                        contentY + (blockH - _labelLineH) / 2f);
                right -= shareW + 8f;
                using (var b = new SolidBrush(selected
                    ? DeltaTorTheme.AccentLight
                    : Color.FromArgb(217, DeltaTorTheme.Muted)))
                    g.DrawString(shareText, _fLabel, b, right,
                        contentY + (blockH - _labelLineH) / 2f);
            }

            if (!last)
            {
                using var div = new SolidBrush(Color.FromArgb(153, DeltaTorTheme.BorderLight));
                g.FillRectangle(div, inset, sy + h, w - inset * 2f, 1f);
            }
        }, onClick);
    }

    /// <summary>DrawerGroupHeader (MainActivity 2756): muted caption left,
    /// trailing action right; the row itself is the hit when actionable.</summary>
    private void AddGroupHeader(
        Flow flow, string title, string trailing, bool muted, Action? onClick)
    {
        var w = flow.W;
        var h = 12f + _cardTitleLineH + 6f;
        flow.Add(h, (g, sy) =>
        {
            var color = muted
                ? Color.FromArgb(153, DeltaTorTheme.Muted)
                : DeltaTorTheme.Muted;
            using (var b = new SolidBrush(color))
                g.DrawString(title, _fLabel, b, 26f, sy + 12f);

            var trailFont = onClick != null ? _fLabel : _fLabelReg;
            var trailW = DrawUtil.AdvanceWidth(trailing, trailFont);
            using (var b = new SolidBrush(
                onClick != null
                    ? DeltaTorTheme.AccentLight
                    : Color.FromArgb(179, color)))
                g.DrawString(trailing, trailFont, b, w - 20f - trailW, sy + 12f);
        }, onClick);
    }

    private void AddLoading(Flow flow)
    {
        var h = 12f + _bodyLineH + 12f;
        flow.Add(h, (g, sy) =>
        {
            using var b = new SolidBrush(Color.FromArgb(189, DeltaTorTheme.Muted));
            g.DrawString("Reading country list …", _fBody, b, 26f, sy + 12f);
        });
    }

    /// <summary>The emoji face, found once: walking FontFamily.Families per
    /// row per paint was the single most expensive thing in a scroll frame.</summary>
    private Font EmojiFont()
    {
        if (_fEmoji != null) return _fEmoji;
        foreach (var f in FontFamily.Families)
        {
            if (f.Name is not ("Segoe UI Emoji" or "Segoe UI Symbol")) continue;
            _fEmoji = new Font(f, 13.5f);
            return _fEmoji;
        }
        _fEmoji = new Font(DeltaTorTheme.FontFamilyName, 13.5f);
        return _fEmoji;
    }
}
