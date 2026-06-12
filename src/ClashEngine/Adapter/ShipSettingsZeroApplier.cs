using System;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Overrides a set of per-ship client settings to 0 for a player, across all eight ships, via
/// <see cref="IClientSettings"/>. One instance per game-type override mode:
/// <list type="bullet">
/// <item><see cref="NoItems"/> -- the <c>GameType&lt;i&gt;DisallowItems</c> no-items mode: zeroes
/// the item-max settings (<c>BurstMax</c>, <c>RepelMax</c>, <c>DecoyMax</c>, <c>BrickMax</c>,
/// <c>ThorMax</c>, <c>RocketMax</c>, <c>PortalMax</c>) so ships spawn with no stockpilable items
/// and greens can't grant any.</item>
/// <item><see cref="NoAntiwarp"/> -- the <c>GameType&lt;i&gt;DisallowAntiwarp</c> mode: zeroes
/// <c>AntiWarpStatus</c> (0 = ship can't have antiwarp) so nobody can carry, green, or start with
/// antiwarp.</item>
/// </list>
/// Per-player override; the orchestrator applies it on placement / <c>?return</c> and clears it
/// when the player leaves the match.
/// </summary>
/// <remarks>
/// All eight ship sections are overridden (not just the ship the player is placed on) so an
/// in-match ship change within the grace window can't smuggle the capability back in.
/// </remarks>
public sealed class ShipSettingsZeroApplier
{
    private const string LogCategory = nameof(ShipSettingsZeroApplier);

    private static readonly string[] ItemMaxKeys =
    [
        "BurstMax", "RepelMax", "DecoyMax", "BrickMax", "ThorMax", "RocketMax", "PortalMax",
    ];

    private static readonly string[] AntiwarpKeys = ["AntiWarpStatus"];

    private const int ShipCount = 8;

    private readonly IClientSettings _clientSettings;
    private readonly ILogManager? _log;
    private readonly string _purpose;
    private readonly ClientSettingIdentifier[] _ids;

    /// <summary>
    /// True iff every <c>{Warbird..Shark}:{key}</c> identifier resolved at construction.
    /// When false, <see cref="Apply"/> / <see cref="Clear"/> are no-ops (the game type degrades
    /// to the arena's normal ship settings).
    /// </summary>
    public bool Ready { get; }

    /// <summary>The no-items override (<c>GameType&lt;i&gt;DisallowItems</c>).</summary>
    public static ShipSettingsZeroApplier NoItems(IClientSettings clientSettings, ILogManager? log = null) =>
        new(clientSettings, ItemMaxKeys, "no-items", log);

    /// <summary>The no-antiwarp override (<c>GameType&lt;i&gt;DisallowAntiwarp</c>).</summary>
    public static ShipSettingsZeroApplier NoAntiwarp(IClientSettings clientSettings, ILogManager? log = null) =>
        new(clientSettings, AntiwarpKeys, "no-antiwarp", log);

    private ShipSettingsZeroApplier(
        IClientSettings clientSettings, string[] keys, string purpose, ILogManager? log)
    {
        _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        _log = log;
        _purpose = purpose;
        _ids = new ClientSettingIdentifier[ShipCount * keys.Length];
        Ready = TryResolveIdentifiers(keys);
    }

    private bool TryResolveIdentifiers(string[] keys)
    {
        for (int ship = 0; ship < ShipCount; ship++)
        {
            string section = ((ShipType)ship).ToString();
            for (int k = 0; k < keys.Length; k++)
            {
                if (!_clientSettings.TryGetSettingsIdentifier(section, keys[k], out _ids[ship * keys.Length + k]))
                {
                    _log?.LogM(LogLevel.Warn, LogCategory,
                        $"Could not resolve {section}:{keys[k]} client-setting identifier; {_purpose} overrides disabled.");
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Override this applier's per-ship settings to 0 for <paramref name="player"/>. Pass
    /// <paramref name="send"/> = <see langword="false"/> when batching with other overrides;
    /// follow up with one <see cref="Send"/>. No-op when <see cref="Ready"/> is false.
    /// </summary>
    public void Apply(Player player, bool send = true)
    {
        if (!Ready || player is null) return;

        foreach (var id in _ids)
            _clientSettings.OverrideSetting(player, id, 0);

        if (send) _clientSettings.SendClientSettings(player);
    }

    /// <summary>
    /// Remove the overrides previously applied to <paramref name="player"/>, reverting the
    /// settings to the arena's configured values. Callers should only invoke this for players
    /// they actually applied an override to. Pass <paramref name="send"/> =
    /// <see langword="false"/> when batching; follow up with one <see cref="Send"/>. No-op when
    /// <see cref="Ready"/> is false.
    /// </summary>
    public void Clear(Player player, bool send = true)
    {
        if (!Ready || player is null) return;

        foreach (var id in _ids)
            _clientSettings.UnoverrideSetting(player, id);

        if (send) _clientSettings.SendClientSettings(player);
    }

    /// <summary>
    /// Push <paramref name="player"/>'s current client settings. The settings packet is large --
    /// when this applier's override lands together with another (e.g. the respawn-box override),
    /// batch with <c>send: false</c> and call this once at the end.
    /// </summary>
    public void Send(Player player)
    {
        if (player is null) return;
        _clientSettings.SendClientSettings(player);
    }
}
