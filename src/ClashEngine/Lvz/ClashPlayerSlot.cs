using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace ClashEngine.Lvz;

/// <summary>
/// <see cref="IPlayerSlot"/> + <see cref="IMemberStats"/> adapter exposing a single roster slot
/// in a ClashEngine match. Live-reads through to <c>ActiveMatch</c> and the player resolver so
/// MatchLvz's per-kill statbox refresh always sees current state.
/// </summary>
/// <remarks>
/// <para>Item counts (Bursts, Repels, Thors, Bricks, Decoys, Rockets, Portals) are stubbed to 0
/// for now. ClashEngine's stats pipeline tracks <c>ItemUses</c> but not currently-held inventory
/// at the slot level; until that's wired, MatchLvz's item digits will read 0. The slot still
/// otherwise renders correctly.</para>
///
/// <para>PremadeGroupId and LagOuts are stubbed to 0 / null -- both are stats-side concepts the
/// SS league pipeline persists but the ClashEngine adapter doesn't surface yet.</para>
/// </remarks>
internal sealed class ClashPlayerSlot : IPlayerSlot, IMemberStats
{
    private readonly ClashMatchData _matchData;
    private readonly ClashTeam _team;
    private readonly PlayerKey _key;
    private readonly ShipType _ship;

    public ClashPlayerSlot(ClashMatchData matchData, ClashTeam team, int slotIdx, PlayerKey key)
    {
        _matchData = matchData;
        _team = team;
        SlotIdx = slotIdx;
        _key = key;
        _ship = ResolveShipFromQueue(matchData.Queue, team.TeamIdx, slotIdx);
    }

    public IMatchData MatchData => _matchData;
    public ITeam Team => _team;
    public int SlotIdx { get; }

    public PlayerSlotStatus Status
    {
        get
        {
            var match = _matchData.Match;
            if (!match.Statuses.TryGetValue(_key, out var ps)) return PlayerSlotStatus.None;
            // Knockout (lives spent or abandoned) takes precedence over the live PlayerStatus,
            // since the engine doesn't transition Active -> some-knockout-status on the
            // eliminating death; ExitedAt is the marker.
            if (match.IsKnockedOut(_key)) return PlayerSlotStatus.KnockedOut;
            return ps switch
            {
                PlayerStatus.Active => PlayerSlotStatus.Playing,
                PlayerStatus.Pending or PlayerStatus.InGrace => PlayerSlotStatus.Waiting,
                _ => PlayerSlotStatus.None,
            };
        }
    }

    public string? PlayerName => _key.Name;

    public Player? Player => _matchData.Resolver.Resolve(_key);

    public int? PremadeGroupId => null;
    public int LagOuts => 0;

    public int Lives
    {
        get
        {
            var match = _matchData.Match;
            if (!match.LivesPerPlayer.HasValue) return int.MaxValue;
            return match.LivesRemaining.TryGetValue(_key, out var v) ? v : 0;
        }
    }

    public ShipType Ship => _ship;

    // Live-read from the recorder's per-life inventory (initial loadout - uses). Inventory
    // resets to the ship's initial loadout on every OnSpawn; mid-life green pickups aren't
    // currently tracked, so the count is "items the player has used at most" rather than
    // guaranteed-current-truth.
    public byte Bursts => ReadInventory(Core.Stats.ItemKind.Burst);
    public byte Repels => ReadInventory(Core.Stats.ItemKind.Repel);
    public byte Thors => ReadInventory(Core.Stats.ItemKind.Thor);
    public byte Bricks => ReadInventory(Core.Stats.ItemKind.Brick);
    public byte Decoys => ReadInventory(Core.Stats.ItemKind.Decoy);
    public byte Rockets => ReadInventory(Core.Stats.ItemKind.Rocket);
    public byte Portals => ReadInventory(Core.Stats.ItemKind.Portal);

    private byte ReadInventory(Core.Stats.ItemKind kind)
    {
        if (_matchData.StatsRecorder is not { } rec) return 0;
        if (!rec.Stats.TryGetValue(_key, out var ps)) return 0;
        if (!ps.Inventory.TryGetValue(kind, out var n) || n <= 0) return 0;
        return n > byte.MaxValue ? byte.MaxValue : (byte)n;
    }

    // IMemberStats: Kills + Deaths read live from the StatsRecorder if present.
    short IMemberStats.Kills => ReadStat(static ps => ps.Kills);
    short IMemberStats.Deaths => ReadStat(static ps => ps.Deaths);

    private short ReadStat(System.Func<Core.Stats.PlayerStats, int> get)
    {
        if (_matchData.StatsRecorder is not { } rec) return 0;
        if (!rec.Stats.TryGetValue(_key, out var ps)) return 0;
        int v = get(ps);
        if (v < short.MinValue) return short.MinValue;
        if (v > short.MaxValue) return short.MaxValue;
        return (short)v;
    }

    private static ShipType ResolveShipFromQueue(Core.Queue.QueueDefinition queue, int teamIdx, int slotIdx)
    {
        if (queue.ShipBySlot is null) return ShipType.Spec;
        if (teamIdx < 0 || teamIdx >= queue.ShipBySlot.Count) return ShipType.Spec;
        var team = queue.ShipBySlot[teamIdx];
        if (slotIdx < 0 || slotIdx >= team.Count) return ShipType.Spec;
        int raw = team[slotIdx];
        return raw is >= 0 and <= 7 ? (ShipType)raw : ShipType.Spec;
    }
}
