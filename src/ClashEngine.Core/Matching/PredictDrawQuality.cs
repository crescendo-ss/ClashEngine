using System;
using System.Collections.Generic;
using ClashEngine.Core.Ratings;
using OpenSkillSharp;
using OpenSkillSharp.Models;
using OsRatingType = OpenSkillSharp.Rating.Rating;
using OsTeam = OpenSkillSharp.Rating.Team;
using IOsTeam = OpenSkillSharp.Rating.ITeam;
using IOsRating = OpenSkillSharp.Rating.IRating;

namespace ClashEngine.Core.Matching;

/// <summary>
/// OpenSkill-native match quality: scores a partition by how close the model believes the match is
/// to a coin flip. This is the principled fairness signal the engine is meant to optimize, and the
/// intended successor to <see cref="OrdinalSpreadQuality"/> (whose own doc-comment flags it as "a
/// pragmatic stand-in until OpenSkill's predict-draw quality is wired in").
/// </summary>
/// <remarks>
/// <para>
/// It is derived from <see cref="IOpenSkillModel.PredictWin"/>, <b>not</b> the raw
/// <see cref="IOpenSkillModel.PredictDraw"/> tie-probability, on purpose. <c>PredictDraw</c> peaks
/// well below 1.0 even for a perfectly even match (~0.06-0.12 for a 4v4) and that peak drifts with
/// the players' sigma, so it does not satisfy the <see cref="IMatchQualityFunction"/> /
/// <see cref="PartitionQualityPolicy"/> contract that <c>1.0 == perfectly balanced</c> on a fixed
/// scale -- a perfect match would score below a typical q-floor and never form. The win-probability
/// spread does satisfy it: it is exactly <c>1.0</c> when every team is equally likely to win and
/// falls to <c>0.0</c> for a certain blowout. Same argmax as maximizing draw probability (both peak
/// at equal team strength), but scale-stable.
/// </para>
/// <para>
/// Because <c>PredictWin</c> folds in sigma, an uncertain (high-sigma) roster correctly reads as
/// closer to even than a mu-only spread would -- the model is less sure of the outcome. Guarding
/// against genuinely lopsided <em>skill</em> is a separate concern handled up front by
/// <see cref="MatchShape.MaxMuSpread"/>, which vetoes an ineligible match roster before quality is
/// ever scored.
/// </para>
/// <para>
/// <c>quality = 1 - (maxTeamWinProb - minTeamWinProb)</c>, clamped to <c>[0, 1]</c>.
/// </para>
/// </remarks>
public sealed class PredictDrawQuality : IMatchQualityFunction
{
    private readonly IOpenSkillModel _model;

    /// <param name="model">
    /// The OpenSkill model to predict with. Defaults to a fresh <see cref="PlackettLuce"/> with
    /// library defaults (mu=25, sigma=25/3), matching <see cref="RatingUpdater"/>'s model so the
    /// balancer and the rating updates speak the same scale.
    /// </param>
    public PredictDrawQuality(IOpenSkillModel? model = null)
    {
        _model = model ?? new PlackettLuce();
    }

    public double Compute(IReadOnlyList<IReadOnlyList<Rating>> teams)
    {
        if (teams.Count == 0) return 0;

        var osTeams = new List<IOsTeam>(teams.Count);
        for (int t = 0; t < teams.Count; t++)
        {
            var src = teams[t];
            if (src.Count == 0) continue;
            var players = new List<IOsRating>(src.Count);
            for (int j = 0; j < src.Count; j++)
                players.Add(new OsRatingType { Mu = src[j].Mu, Sigma = src[j].Sigma });
            osTeams.Add(new OsTeam { Players = players });
        }

        // A single non-empty team has no opponent to be balanced against -- treat as undefined (0).
        if (osTeams.Count < 2) return 0;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var winProb in _model.PredictWin(osTeams))
        {
            if (winProb < min) min = winProb;
            if (winProb > max) max = winProb;
        }
        if (double.IsPositiveInfinity(min)) return 0;

        return Math.Clamp(1.0 - (max - min), 0.0, 1.0);
    }
}
