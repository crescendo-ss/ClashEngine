using System;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Implements the <c>GameType&lt;i&gt;DisallowItems</c> no-items mode: overrides a player's
/// per-ship item-max client settings (<c>BurstMax</c>, <c>RepelMax</c>, <c>DecoyMax</c>,
/// <c>BrickMax</c>, <c>ThorMax</c>, <c>RocketMax</c>, <c>PortalMax</c> -- on all eight ships,
/// via <see cref="IClientSettings"/>) to 0, so ships spawn with no stockpilable items and
/// greens can't grant any. Per-player override; the orchestrator applies it on placement /
/// <c>?return</c> and clears it when the player leaves the match.
/// </summary>
/// <remarks>
/// All eight ship sections are overridden (not just the ship the player is placed on) so an
/// in-match ship change within the grace window can't smuggle items back in.
/// </remarks>
public sealed class NoItemsSettingsApplier
{
    private const string LogCategory = nameof(NoItemsSettingsApplier);

    private static readonly string[] ItemMaxKeys =
    [
        "BurstMax", "RepelMax", "DecoyMax", "BrickMax", "ThorMax", "RocketMax", "PortalMax",
    ];

    private const int ShipCount = 8;

    private readonly IClientSettings _clientSettings;
    private readonly ILogManager? _log;
    private readonly ClientSettingIdentifier[] _ids = new ClientSettingIdentifier[ShipCount * ItemMaxKeys.Length];

    /// <summary>
    /// True iff every <c>{Warbird..Shark}:{item}Max</c> identifier resolved at construction.
    /// When false, <see cref="Apply"/> / <see cref="Clear"/> are no-ops (the game type degrades
    /// to the arena's normal item settings).
    /// </summary>
    public bool Ready { get; }

    public NoItemsSettingsApplier(IClientSettings clientSettings, ILogManager? log = null)
    {
        _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        _log = log;
        Ready = TryResolveIdentifiers();
    }

    private bool TryResolveIdentifiers()
    {
        for (int ship = 0; ship < ShipCount; ship++)
        {
            string section = ((ShipType)ship).ToString();
            for (int k = 0; k < ItemMaxKeys.Length; k++)
            {
                if (!_clientSettings.TryGetSettingsIdentifier(section, ItemMaxKeys[k], out _ids[ship * ItemMaxKeys.Length + k]))
                {
                    _log?.LogM(LogLevel.Warn, LogCategory,
                        $"Could not resolve {section}:{ItemMaxKeys[k]} client-setting identifier; no-items overrides disabled.");
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Override every ship's item-max settings to 0 for <paramref name="player"/>. Pass
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
    /// Remove the item-max overrides previously applied to <paramref name="player"/>, reverting
    /// them to the arena's configured item settings. Callers should only invoke this for players
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
