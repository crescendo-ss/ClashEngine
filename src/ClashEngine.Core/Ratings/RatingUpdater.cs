using System;
using System.Collections.Generic;
using System.Linq;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using OpenSkillSharp;
using OpenSkillSharp.Models;
using OpenSkillSharp.Rating;
using OsRatingType = OpenSkillSharp.Rating.Rating;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// Applies a finished match's outcome to an <see cref="IRatingStore"/> using OpenSkill.
/// </summary>
/// <remarks>
/// <para>Cancelled matches (everyone freed because someone never showed) do not update ratings.</para>
/// <para>
/// Completed and Abandoned matches do update ratings; abandoners are still treated as participants
/// of their team with the team's actual rank, so they share the penalty for their team's loss.
/// </para>
/// <para>
/// When the outcome carries <see cref="MatchOutcome.PlayerStats"/>, <see cref="MatchOutcome.LivesPerPlayer"/>,
/// and <see cref="MatchOutcome.Duration"/>, the updater applies two refinements on top of OpenSkill's
/// base PlackettLuce update:
/// <list type="bullet">
/// <item><term>Margin-of-victory</term><description> a decisive stomp (e.g. 12-0 in a 4v4 lives=3 elimination)
/// is stronger evidence of skill differential than a close 12-11; the rating delta is extrapolated
/// up to 1.5x using a logarithmic scale on the kill bonus past one full enemy team.</description></item>
/// <item><term>Per-player OpenSkill weights</term><description> weight = "contribution to outcome",
/// driven by kills + survival time. Higher weight = bigger swing in the team's outcome direction:
/// carrying winners gain more, and high-contribution losers (long survival, lots of kills) lose
/// less because the model attributes the loss elsewhere on the team.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RatingUpdater
{
    private readonly IOpenSkillModel _model;

    public RatingUpdater(IOpenSkillModel? model = null)
    {
        _model = model ?? new PlackettLuce();
    }

    public IOpenSkillModel Model => _model;

    /// <summary>
    /// Updates each participating player's rating in <paramref name="store"/> based on the match
    /// outcome. <paramref name="weight"/> linearly scales the magnitude of every rating change:
    /// <c>1.0</c> = full impact (competitive), <c>0.5</c> = half impact (casual), <c>0.0</c> =
    /// no rating change at all (still increments GamesPlayed and updates LastSeen).
    /// </summary>
    public void ApplyOutcome(IRatingStore store, MatchOutcome outcome, DateTimeOffset at, double weight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(outcome);
        if (weight < 0 || weight > 1)
            throw new ArgumentOutOfRangeException(nameof(weight), "Must be in [0, 1].");

        if (outcome.FinalState == MatchState.Cancelled || outcome.RankedTeams.Count == 0)
            return;

        var teams = new List<ITeam>(outcome.RankedTeams.Count);
        var ranks = new List<double>(outcome.RankedTeams.Count);
        var playerRefs = new List<List<PlayerKey>>(outcome.RankedTeams.Count);
        var oldRatings = new List<List<Rating>>(outcome.RankedTeams.Count);

        foreach (var rt in outcome.RankedTeams)
        {
            var osRatings = new List<IRating>(rt.Players.Count);
            var keys = new List<PlayerKey>(rt.Players.Count);
            var olds = new List<Rating>(rt.Players.Count);

            foreach (var p in rt.Players)
            {
                var r = store.Get(p, outcome.GameType);
                osRatings.Add(new OsRatingType { Mu = r.Mu, Sigma = r.Sigma });
                keys.Add(p);
                olds.Add(r);
            }

            teams.Add(new Team { Players = osRatings });
            ranks.Add(rt.Rank);
            playerRefs.Add(keys);
            oldRatings.Add(olds);
        }

        // OpenSkill weights reshape the per-player update inside the model (carrying winners are
        // pulled further toward the win prediction; surviving losers are pulled less toward the
        // loss prediction). Null when the outcome lacks per-player stats -- model falls back to
        // uniform weights.
        var perPlayerWeights = TryBuildPerPlayerWeights(outcome);

        // Margin scales the magnitude of every player's rating delta. Stomp = 1.5x; close win = 1.0.
        double marginFactor = TryComputeMarginFactor(outcome);
        double effectiveScale = weight * marginFactor;

        var rated = _model.Rate(teams, ranks: ranks, weights: perPlayerWeights).ToList();

        for (int t = 0; t < rated.Count; t++)
        {
            var ratedPlayers = rated[t].Players.ToList();
            for (int j = 0; j < ratedPlayers.Count; j++)
            {
                var key = playerRefs[t][j];
                var existing = store.Get(key, outcome.GameType);
                var old = oldRatings[t][j];

                double newMu = old.Mu + effectiveScale * (ratedPlayers[j].Mu - old.Mu);
                double newSigma = old.Sigma + effectiveScale * (ratedPlayers[j].Sigma - old.Sigma);

                var updated = existing.WithGameRecorded(newMu, newSigma, at);
                store.Set(key, outcome.GameType, updated);
            }
        }
    }

    private static IList<IList<double>>? TryBuildPerPlayerWeights(MatchOutcome outcome)
    {
        if (outcome.PlayerStats is not { } stats) return null;
        if (outcome.Duration is not { } duration || duration <= TimeSpan.Zero) return null;

        int maxKills = 0;
        foreach (var s in stats.Values)
            if (s.Kills > maxKills) maxKills = s.Kills;

        double matchSeconds = duration.TotalSeconds;
        var weights = new IList<double>[outcome.RankedTeams.Count];
        for (int t = 0; t < outcome.RankedTeams.Count; t++)
        {
            var team = outcome.RankedTeams[t];
            var w = new double[team.Players.Count];
            for (int j = 0; j < team.Players.Count; j++)
            {
                var p = team.Players[j];
                double survivalNorm = 0;
                int kills = 0;
                if (stats.TryGetValue(p, out var ps))
                {
                    survivalNorm = Math.Clamp(ps.TimeAlive.TotalSeconds / matchSeconds, 0, 1);
                    kills = ps.Kills;
                }
                double killsNorm = maxKills > 0 ? (double)kills / maxKills : 0;

                // Symmetric formula: weight is "contribution to outcome". OpenSkill interprets
                // higher weight as "this player drove the result" -- which earns a winner more
                // reward AND shields a loser from penalty (the loss is attributed elsewhere on the
                // team). So survival + kills both boost weight regardless of side: a carrying
                // winner gains more, and a long-surviving loser loses less.
                w[j] = 1.0 + 0.5 * survivalNorm + 0.3 * killsNorm;
            }
            weights[t] = w;
        }
        return weights;
    }

    private static double TryComputeMarginFactor(MatchOutcome outcome)
    {
        if (outcome.LivesPerPlayer is not { } livesPerPlayer || livesPerPlayer < 1) return 1.0;
        if (outcome.RankedTeams.Count != 2) return 1.0;

        int winnerRank = int.MaxValue;
        int loserRank = int.MinValue;
        int teamSize = 0;
        int losingScore = 0;
        foreach (var rt in outcome.RankedTeams)
        {
            if (rt.Rank < winnerRank) winnerRank = rt.Rank;
            if (rt.Rank > loserRank)
            {
                loserRank = rt.Rank;
                losingScore = rt.Score;
            }
            if (rt.Players.Count > teamSize) teamSize = rt.Players.Count;
        }
        if (teamSize <= 0) return 1.0;

        // bonus_kills measures how decisively the winner team avoided losses: the loser dealt so
        // few kills that the winner kept more than a full team's worth of "spare" lives. Below
        // that threshold (close match) we don't scale at all.
        int totalLives = teamSize * livesPerPlayer;
        int bonusKills = Math.Max(totalLives - losingScore - teamSize, 0);
        if (bonusKills == 0) return 1.0;

        int maxBonus = Math.Max(totalLives - teamSize, 1);
        double scale = Math.Log(1.0 + bonusKills) / Math.Log(1.0 + maxBonus);
        return 1.0 + 0.5 * scale;
    }
}
