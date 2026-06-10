using System;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Ports SubspaceServer's <c>TeamVersusMatch.SendSpawnOverrides</c>: overrides a player's native
/// Continuum <c>[Spawn]</c> Team0-3 X/Y/Radius client settings (via <see cref="IClientSettings"/>)
/// so the <em>client</em> respawns the player inside the match's configured box on every death.
/// Per-player override; the orchestrator applies it on placement / <c>?return</c> and clears it
/// when the player leaves the match.
/// </summary>
/// <remarks>
/// The team's box is written into <b>all four</b> Team0-3 slots, not just the slot matching the
/// player's team. Continuum selects which slot to spawn from by <c>freq % 4</c>, and ClashEngine
/// parks every team's freq on a multiple of 100 (and every multiple of 100 is <c>== 0 mod 4</c>),
/// so each player would otherwise always read Team0. Filling all four makes the player land in
/// their own team's box regardless of the freq-to-slot mapping.
/// </remarks>
public sealed class SpawnSettingsApplier
{
    private const string LogCategory = nameof(SpawnSettingsApplier);

    /// <summary>Native Continuum <c>[Spawn]</c> radius is a 9-bit field; values cap at 511 tiles.</summary>
    private const int MaxRadiusTiles = 511;

    private readonly IClientSettings _clientSettings;
    private readonly ILogManager? _log;
    private readonly ClientSettingIdentifier[] _xIds = new ClientSettingIdentifier[4];
    private readonly ClientSettingIdentifier[] _yIds = new ClientSettingIdentifier[4];
    private readonly ClientSettingIdentifier[] _rIds = new ClientSettingIdentifier[4];

    /// <summary>
    /// True iff all twelve <c>Spawn:Team{0..3}-{X,Y,Radius}</c> identifiers resolved at
    /// construction. When false, <see cref="Apply"/> / <see cref="Clear"/> are no-ops (the engine
    /// degrades to whatever the arena's default spawn does).
    /// </summary>
    public bool Ready { get; }

    public SpawnSettingsApplier(IClientSettings clientSettings, ILogManager? log = null)
    {
        _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        _log = log;
        Ready = TryResolveIdentifiers();
    }

    private bool TryResolveIdentifiers()
    {
        for (int i = 0; i < 4; i++)
        {
            if (!_clientSettings.TryGetSettingsIdentifier("Spawn", $"Team{i}-X", out _xIds[i])
                || !_clientSettings.TryGetSettingsIdentifier("Spawn", $"Team{i}-Y", out _yIds[i])
                || !_clientSettings.TryGetSettingsIdentifier("Spawn", $"Team{i}-Radius", out _rIds[i]))
            {
                _log?.LogM(LogLevel.Warn, LogCategory,
                    $"Could not resolve Spawn:Team{i} client-setting identifiers; respawn overrides disabled.");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Override <paramref name="player"/>'s <c>[Spawn]</c> settings (all four Team slots) to
    /// <paramref name="area"/> and push the updated client settings. The center is converted from
    /// pixels to tiles (the native setting's unit); the radius is clamped to the 9-bit field.
    /// Pass <paramref name="send"/> = <see langword="false"/> when batching with other overrides;
    /// follow up with one <see cref="Send"/>. No-op when <see cref="Ready"/> is false.
    /// </summary>
    public void Apply(Player player, SpawnArea area, bool send = true)
    {
        if (!Ready || player is null) return;

        int tx = area.Center.TileX;
        int ty = area.Center.TileY;
        int r = Math.Clamp(area.RadiusTiles, 0, MaxRadiusTiles);

        for (int i = 0; i < 4; i++)
        {
            _clientSettings.OverrideSetting(player, _xIds[i], tx);
            _clientSettings.OverrideSetting(player, _yIds[i], ty);
            _clientSettings.OverrideSetting(player, _rIds[i], r);
        }

        // One settings packet after all overrides (the packet is large; batch then send once).
        if (send) _clientSettings.SendClientSettings(player);
    }

    /// <summary>
    /// Remove any <c>[Spawn]</c> override previously applied to <paramref name="player"/> and push
    /// the reverted settings, so they fall back to the arena's configured spawn. No-op when
    /// <see cref="Ready"/> is false. Callers should only invoke this for players they actually
    /// applied an override to, to avoid sending a redundant client-settings packet. Pass
    /// <paramref name="send"/> = <see langword="false"/> when batching with other overrides;
    /// follow up with one <see cref="Send"/>.
    /// </summary>
    public void Clear(Player player, bool send = true)
    {
        if (!Ready || player is null) return;

        for (int i = 0; i < 4; i++)
        {
            _clientSettings.UnoverrideSetting(player, _xIds[i]);
            _clientSettings.UnoverrideSetting(player, _yIds[i]);
            _clientSettings.UnoverrideSetting(player, _rIds[i]);
        }

        if (send) _clientSettings.SendClientSettings(player);
    }

    /// <summary>
    /// Push <paramref name="player"/>'s current client settings. The settings packet is large --
    /// when this applier's override lands together with another (e.g. the no-items override),
    /// batch with <c>send: false</c> and call this once at the end.
    /// </summary>
    public void Send(Player player)
    {
        if (player is null) return;
        _clientSettings.SendClientSettings(player);
    }
}
