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

        var rated = _model.Rate(teams, ranks: ranks).ToList();

        for (int t = 0; t < rated.Count; t++)
        {
            var ratedPlayers = rated[t].Players.ToList();
            for (int j = 0; j < ratedPlayers.Count; j++)
            {
                var key = playerRefs[t][j];
                var existing = store.Get(key, outcome.GameType);
                var old = oldRatings[t][j];

                // Linearly interpolate the OpenSkill update by `weight`. Mu and sigma both scale.
                double newMu = old.Mu + weight * (ratedPlayers[j].Mu - old.Mu);
                double newSigma = old.Sigma + weight * (ratedPlayers[j].Sigma - old.Sigma);

                var updated = existing.WithGameRecorded(newMu, newSigma, at);
                store.Set(key, outcome.GameType, updated);
            }
        }
    }
}
