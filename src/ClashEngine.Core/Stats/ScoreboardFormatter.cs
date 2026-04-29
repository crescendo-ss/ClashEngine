using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Pure formatter that turns a finalized <see cref="MatchPayload"/> into a multi-line
/// chat-friendly scoreboard, one block per team. The four-section layout (combat | damage |
/// accuracy | rating) mirrors the standard zone matchmaking scoreboard format.
/// </summary>
/// <remarks>
/// Server ticks are 10 ms (100 ticks/second). Distance samples are in raw subspace pixels;
/// <c>dE</c> is reported in 16-pixel tiles. Rating columns require <c>postOrdinalByPlayer</c>;
/// players missing from that map render as <c>"-"</c> for both +/- and Rat.
///
/// Output is plain ASCII -- the SubSpace chat font's printable set doesn't include em-dash
/// or Greek delta, so previously-used <c>'—'</c> (em-dash) and <c>'Δ'</c> (delta)
/// rendered as substitute glyphs and broke column alignment.
/// </remarks>
public static class ScoreboardFormatter
{
    private const int TicksPerSecond = 100;
    private const int PixelsPerTile = 16;
    private const string Dash = "-";

    private static readonly Column[] LeftCols =
    {
        new("Player", 20, LeftAlign: true), new("Ki/De", 5), new("TK", 2), new("AS", 2),
        new("FR", 4), new("WR", 2), new("WRk", 3), new("WEPS", 4), new("dE", 4),
        new("Mi", 2), new("PTime", 5),
    };
    private static readonly Column[] DamageCols =
    {
        new("DDealt", 6), new("DTaken", 6), new("DmgE", 4), new("KiDmg", 5), new("FRDmg", 5), new("TmDmg", 4),
    };
    private static readonly Column[] AccuracyCols = { new("AcB", 3), new("AcG", 3) };
    private static readonly Column[] RatingCols = { new("+/-", 4), new("Rat", 4) };

    private readonly record struct Column(string Header, int Width, bool LeftAlign = false);

    /// <summary>
    /// Renders <paramref name="payload"/> as a list of chat lines. Teams are emitted in rank
    /// order; within each team rows follow the original team order. A totals row closes each
    /// team's table.
    /// </summary>
    /// <param name="payload">Finalized match envelope.</param>
    /// <param name="postOrdinalByPlayer">
    /// Map of player name (case-insensitive) to post-match ordinal (mu - 3*sigma). When
    /// provided, the +/- column shows (post-pre)*10 rounded and Rat shows post*10 rounded.
    /// </param>
    public static IReadOnlyList<string> Format(
        MatchPayload payload,
        IReadOnlyDictionary<string, double>? postOrdinalByPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var statsByName = new Dictionary<string, PlayerStatsPayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in payload.Participants) statsByName[p.Name] = p;

        string plain = BuildDivider('-');
        string thick = BuildDivider('=');
        int teamCount = payload.Teams.Count;

        var lines = new List<string>();
        if (teamCount == 0) return lines;

        lines.Add(plain);
        for (int t = 0; t < teamCount; t++)
        {
            EmitTeamBlock(lines, payload.Teams[t], statsByName, postOrdinalByPlayer, teamCount, plain);
            // Between teams: a single thick (=====) divider replaces the "bottom + blank + top"
            // sandwich. After the last team: a plain divider closes the table.
            lines.Add(t == teamCount - 1 ? plain : thick);
        }
        return lines;
    }

    private static void EmitTeamBlock(
        List<string> lines,
        RankedTeamPayload team,
        IReadOnlyDictionary<string, PlayerStatsPayload> statsByName,
        IReadOnlyDictionary<string, double>? postOrdinalByPlayer,
        int teamCount,
        string plain)
    {
        // First column doubles as the section title -- "Victors" / "Defeated" for 2-team
        // matches, "Rank #N" otherwise -- with the team's score in parens. Replaces the
        // separate title row the layout used to carry.
        string firstColLabel = LabelForRank(team.Rank, teamCount, team.Score);

        lines.Add(BuildHeader(firstColLabel));
        lines.Add(plain);

        var totals = new TeamTotals();
        for (int i = 0; i < team.Players.Count; i++)
        {
            var name = team.Players[i];
            if (!statsByName.TryGetValue(name, out var ps)) continue;
            lines.Add(BuildRow(ps, postOrdinalByPlayer));
            totals.Accumulate(ps, postOrdinalByPlayer);
        }
        lines.Add(plain);
        lines.Add(BuildTotals(totals));
    }

    private static string LabelForRank(int rank, int teamCount, int score)
    {
        string baseLabel = teamCount == 2
            ? rank == 1 ? "Winners" : "Losers"
            : $"Rank #{rank}";
        return $"{baseLabel} ({score} pts)";
    }

    private static string BuildHeader(string firstColLabel)
    {
        var sb = new StringBuilder(RowWidth());
        sb.Append("| ");
        AppendCell(sb, firstColLabel, LeftCols[0].Width, LeftCols[0].LeftAlign);
        for (int i = 1; i < LeftCols.Length; i++)
        {
            sb.Append(' ');
            AppendCell(sb, LeftCols[i].Header, LeftCols[i].Width, LeftCols[i].LeftAlign);
        }
        AppendSection(sb, DamageCols, c => c.Header);
        AppendSection(sb, AccuracyCols, c => c.Header);
        AppendSection(sb, RatingCols, c => c.Header);
        sb.Append(" |");
        return sb.ToString();
    }

    private static string BuildRow(PlayerStatsPayload ps, IReadOnlyDictionary<string, double>? postOrdinal)
    {
        var bul = GetWeapon(ps.PerWeapon, "Bullet");
        var bom = GetWeapon(ps.PerWeapon, "Bomb");

        string kide = $"{ps.Kills,2}/{ps.Deaths,2}";
        string fr = ps.ForcedRepelCredit < 0.05 ? "0" : RoundToInt(ps.ForcedRepelCredit).ToString(CultureInfo.InvariantCulture);
        int wr = GetItemCount(ps.WastedItems, "Repel");
        int wrk = GetItemCount(ps.WastedItems, "Rocket");
        int mi = GetWeaponFireCount(ps.PerWeapon, "Mine") + GetWeaponFireCount(ps.PerWeapon, "BouncingMine");
        int weps = ComputeEnergyPerSecond(ps);
        string de = FormatDistanceTiles(ps.DistanceSamples);
        string ptime = FormatPTime(ps.ActiveTicks);

        long dealt = ps.DamageDealt, taken = ps.DamageTaken;
        string dmgE = FormatPercent(dealt + taken == 0 ? -1 : (double)dealt / (dealt + taken));
        // AcB = bomb accuracy, AcG = gun (bullet) accuracy. Keep these mapped to the
        // corresponding weapon's fire/hit counters or the column header lies.
        string acb = FormatPercent(bom.Fire == 0 ? -1 : (double)bom.Hit / bom.Fire);
        string acg = FormatPercent(bul.Fire == 0 ? -1 : (double)bul.Hit / bul.Fire);
        (string delta, string rat) = FormatRatingCells(ps, postOrdinal);

        var leftValues = new[]
        {
            ps.Name, kide, ps.Teamkills.ToString(CultureInfo.InvariantCulture),
            ps.Assists.ToString(CultureInfo.InvariantCulture), fr,
            wr.ToString(CultureInfo.InvariantCulture), wrk.ToString(CultureInfo.InvariantCulture),
            weps.ToString(CultureInfo.InvariantCulture), de,
            mi.ToString(CultureInfo.InvariantCulture), ptime,
        };
        var damageValues = new[]
        {
            dealt.ToString(CultureInfo.InvariantCulture), taken.ToString(CultureInfo.InvariantCulture), dmgE,
            RoundToInt(ps.KillDamage).ToString(CultureInfo.InvariantCulture),
            RoundToInt(ps.ForcedRepelDamage).ToString(CultureInfo.InvariantCulture),
            ps.TeamDamageDealt.ToString(CultureInfo.InvariantCulture),
        };
        var accValues = new[] { acb, acg };
        var ratingValues = new[] { delta, rat };

        var sb = new StringBuilder(RowWidth());
        AppendValueSection(sb, LeftCols, leftValues, openSection: true);
        AppendValueSection(sb, DamageCols, damageValues);
        AppendValueSection(sb, AccuracyCols, accValues);
        AppendValueSection(sb, RatingCols, ratingValues);
        sb.Append(" |");
        return sb.ToString();
    }

    private static string BuildTotals(TeamTotals t)
    {
        string kide = $"{t.Kills,2}/{t.Deaths,2}";
        string dmgE = FormatPercent(t.Dealt + t.Taken == 0 ? -1 : (double)t.Dealt / (t.Dealt + t.Taken));
        string acb = FormatPercent(t.BomFire == 0 ? -1 : (double)t.BomHit / t.BomFire);
        string acg = FormatPercent(t.BulFire == 0 ? -1 : (double)t.BulHit / t.BulFire);
        int activeSeconds = (int)(t.ActiveTicks / TicksPerSecond);
        int weps = activeSeconds <= 0 ? 0 : RoundToInt((double)t.Energy / activeSeconds);
        string de = t.DistanceSampleCount == 0 ? Dash
            : RoundToInt(t.DistanceSum / t.DistanceSampleCount / PixelsPerTile).ToString(CultureInfo.InvariantCulture);
        string ptime = FormatPTime(t.ActiveTicks);
        string deltaCell = t.RatingCount == 0 ? Dash : FormatSignedInt(t.RatingDeltaSum);
        string ratCell = t.RatingCount == 0 ? Dash
            : RoundToInt((double)t.RatingPostSum / t.RatingCount).ToString(CultureInfo.InvariantCulture);

        var leftValues = new[]
        {
            "Totals", kide, t.Teamkills.ToString(CultureInfo.InvariantCulture),
            t.Assists.ToString(CultureInfo.InvariantCulture), RoundToInt(t.ForcedRepelCredit).ToString(CultureInfo.InvariantCulture),
            t.WastedRepels.ToString(CultureInfo.InvariantCulture), t.WastedRockets.ToString(CultureInfo.InvariantCulture),
            weps.ToString(CultureInfo.InvariantCulture), de,
            t.Mines.ToString(CultureInfo.InvariantCulture), ptime,
        };
        var damageValues = new[]
        {
            t.Dealt.ToString(CultureInfo.InvariantCulture), t.Taken.ToString(CultureInfo.InvariantCulture), dmgE,
            RoundToInt(t.KillDamage).ToString(CultureInfo.InvariantCulture),
            RoundToInt(t.ForcedRepelDamage).ToString(CultureInfo.InvariantCulture),
            t.TeamDamage.ToString(CultureInfo.InvariantCulture),
        };
        var accValues = new[] { acb, acg };
        var ratingValues = new[] { deltaCell, ratCell };

        var sb = new StringBuilder(RowWidth());
        AppendValueSection(sb, LeftCols, leftValues, openSection: true);
        AppendValueSection(sb, DamageCols, damageValues);
        AppendValueSection(sb, AccuracyCols, accValues);
        AppendValueSection(sb, RatingCols, ratingValues);
        sb.Append(" |");
        return sb.ToString();
    }

    private static string BuildDivider(char fill = '-')
    {
        int width = RowWidth();
        var chars = new char[width];
        for (int i = 0; i < width; i++) chars[i] = fill;
        chars[0] = '+';
        chars[width - 1] = '+';

        // Section break corners line up with the '|' separator between sections. A closed
        // section opens with " |", so the '|' itself sits one position past the previous
        // section's end -- hence the +1 to land the '+' on the bar instead of the leading space.
        int afterLeft = SectionEndIndex(LeftCols, openSection: true);
        int afterDmg = afterLeft + SectionEndIndex(DamageCols, openSection: false);
        int afterAcc = afterDmg + SectionEndIndex(AccuracyCols, openSection: false);
        if (afterLeft + 1 < width) chars[afterLeft + 1] = '+';
        if (afterDmg + 1 < width) chars[afterDmg + 1] = '+';
        if (afterAcc + 1 < width) chars[afterAcc + 1] = '+';
        return new string(chars);
    }

    // --- section / cell appenders ---

    private static void AppendSection(StringBuilder sb, Column[] cols, Func<Column, string> selector, bool openSection = false)
    {
        sb.Append(openSection ? "|" : " |");
        for (int i = 0; i < cols.Length; i++)
        {
            sb.Append(' ');
            AppendCell(sb, selector(cols[i]), cols[i].Width, cols[i].LeftAlign);
        }
    }

    private static void AppendValueSection(StringBuilder sb, Column[] cols, string[] values, bool openSection = false)
    {
        sb.Append(openSection ? "|" : " |");
        for (int i = 0; i < cols.Length; i++)
        {
            sb.Append(' ');
            AppendCell(sb, values[i], cols[i].Width, cols[i].LeftAlign);
        }
    }

    private static void AppendCell(StringBuilder sb, string value, int width, bool leftAlign)
    {
        if (value.Length >= width) { sb.Append(value, 0, width); return; }
        int pad = width - value.Length;
        if (leftAlign) { sb.Append(value); sb.Append(' ', pad); }
        else { sb.Append(' ', pad); sb.Append(value); }
    }

    // --- width / index helpers ---

    private static int SectionEndIndex(Column[] cols, bool openSection)
    {
        // Counts the width contributed by a section: opener ('|' or ' |') + per-column (' ' + width).
        int n = openSection ? 1 : 2;
        for (int i = 0; i < cols.Length; i++) n += 1 + cols[i].Width;
        return n;
    }

    private static int RowWidth()
    {
        int n = SectionEndIndex(LeftCols, openSection: true);
        n += SectionEndIndex(DamageCols, openSection: false);
        n += SectionEndIndex(AccuracyCols, openSection: false);
        n += SectionEndIndex(RatingCols, openSection: false);
        n += 2; // closing " |"
        return n;
    }

    // --- value helpers ---

    private static (string Delta, string Rat) FormatRatingCells(
        PlayerStatsPayload ps, IReadOnlyDictionary<string, double>? postOrdinal)
    {
        if (postOrdinal is null || !postOrdinal.TryGetValue(ps.Name, out var post)) return (Dash, Dash);
        if (ps.RatingAtStart is null) return (Dash, RoundToInt(post * 10.0).ToString(CultureInfo.InvariantCulture));
        double pre = ps.RatingAtStart.Mu - 3.0 * ps.RatingAtStart.Sigma;
        return (FormatSignedInt(RoundToInt((post - pre) * 10.0)),
                RoundToInt(post * 10.0).ToString(CultureInfo.InvariantCulture));
    }

    private static int ComputeEnergyPerSecond(PlayerStatsPayload ps)
    {
        long energy = SumEnergy(ps.PerWeapon);
        int seconds = (int)(ps.ActiveTicks / TicksPerSecond);
        return seconds <= 0 ? 0 : RoundToInt((double)energy / seconds);
    }

    private static long SumEnergy(IReadOnlyDictionary<string, WeaponStatsPayload> perWeapon)
    {
        long total = 0;
        foreach (var (_, w) in perWeapon) total += w.EnergySpent;
        return total;
    }

    private static (long Fire, long Hit) GetWeapon(IReadOnlyDictionary<string, WeaponStatsPayload> perWeapon, string key) =>
        perWeapon.TryGetValue(key, out var w) ? (w.FireCount, w.HitCount) : (0, 0);

    private static int GetWeaponFireCount(IReadOnlyDictionary<string, WeaponStatsPayload> perWeapon, string key) =>
        perWeapon.TryGetValue(key, out var w) ? w.FireCount : 0;

    private static int GetItemCount(IReadOnlyDictionary<string, int> items, string key) =>
        items.TryGetValue(key, out var n) ? n : 0;

    private static string FormatDistanceTiles(IReadOnlyList<DistanceSamplePayload> samples)
    {
        if (samples.Count == 0) return Dash;
        double total = 0;
        for (int i = 0; i < samples.Count; i++) total += samples[i].Distance;
        return RoundToInt(total / samples.Count / PixelsPerTile).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatPTime(uint ticks)
    {
        int seconds = (int)(ticks / TicksPerSecond);
        int mm = seconds / 60;
        int ss = seconds % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{mm}:{ss:D2}");
    }

    private static string FormatPercent(double ratio) =>
        ratio < 0 ? Dash : RoundToInt(ratio * 100).ToString(CultureInfo.InvariantCulture);

    private static string FormatSignedInt(int n) =>
        n > 0 ? "+" + n.ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);

    private static int RoundToInt(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // --- per-team accumulator ---

    private sealed class TeamTotals
    {
        public int Kills, Deaths, Teamkills, Assists, WastedRepels, WastedRockets, Mines;
        public double ForcedRepelCredit, KillDamage, ForcedRepelDamage;
        public long Dealt, Taken, TeamDamage, BulFire, BulHit, BomFire, BomHit, Energy;
        public uint ActiveTicks;
        public int DistanceSampleCount;
        public double DistanceSum;
        public int RatingDeltaSum, RatingPostSum, RatingCount;

        public void Accumulate(PlayerStatsPayload ps, IReadOnlyDictionary<string, double>? postOrdinal)
        {
            Kills += ps.Kills; Deaths += ps.Deaths; Teamkills += ps.Teamkills; Assists += ps.Assists;
            ForcedRepelCredit += ps.ForcedRepelCredit;
            WastedRepels += GetItemCount(ps.WastedItems, "Repel");
            WastedRockets += GetItemCount(ps.WastedItems, "Rocket");
            Mines += GetWeaponFireCount(ps.PerWeapon, "Mine") + GetWeaponFireCount(ps.PerWeapon, "BouncingMine");
            Dealt += ps.DamageDealt; Taken += ps.DamageTaken; TeamDamage += ps.TeamDamageDealt;
            KillDamage += ps.KillDamage; ForcedRepelDamage += ps.ForcedRepelDamage;
            var bul = GetWeapon(ps.PerWeapon, "Bullet"); BulFire += bul.Fire; BulHit += bul.Hit;
            var bom = GetWeapon(ps.PerWeapon, "Bomb"); BomFire += bom.Fire; BomHit += bom.Hit;
            Energy += SumEnergy(ps.PerWeapon);
            ActiveTicks += ps.ActiveTicks;
            DistanceSampleCount += ps.DistanceSamples.Count;
            for (int i = 0; i < ps.DistanceSamples.Count; i++) DistanceSum += ps.DistanceSamples[i].Distance;

            if (postOrdinal is not null
                && ps.RatingAtStart is not null
                && postOrdinal.TryGetValue(ps.Name, out var post))
            {
                int delta = RoundToInt((post - (ps.RatingAtStart.Mu - 3.0 * ps.RatingAtStart.Sigma)) * 10.0);
                int rat = RoundToInt(post * 10.0);
                RatingDeltaSum += delta;
                RatingPostSum += rat;
                RatingCount++;
            }
        }
    }
}
