using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Probability;

namespace Oloraculo.Web.Services
{
    public class EvaluationService
    {
        private readonly OloraculoDbContext _db;

        public EvaluationService(OloraculoDbContext db) => _db = db;

        public async Task<int> EvaluateLatestSnapshotAsync(Fixture fixture, int homeGoals, int awayGoals, CancellationToken ct = default)
        {
            var snapshots = await _db.Snapshots
                .Where(s => s.Kind == "match"
                    && s.FixtureId == fixture.Id
                    && s.HomeWin.HasValue
                    && s.Draw.HasValue
                    && s.AwayWin.HasValue)
                .ToListAsync(ct);

            // Only score genuinely pre-game snapshots when we know kickoff, so the learning loop
            // never grades a model against a snapshot taken after the result was already known.
            var candidates = fixture.KickoffUtc is { } kickoff
                ? snapshots.Where(s => s.CreatedAt <= kickoff).ToList()
                : snapshots;

            // One evaluation per model: the most recent pre-game snapshot of each.
            var latestPerModel = candidates
                .GroupBy(s => s.ModelName, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(s => s.CreatedAt)
                    .ThenByDescending(s => s.Id)
                    .First())
                .ToList();
            if (latestPerModel.Count == 0)
                return 0;

            var alreadyEvaluated = (await _db.Evaluations
                .Where(e => e.FixtureId == fixture.Id)
                .Select(e => e.ModelName)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var actual = OutcomeFromGoals(homeGoals, awayGoals);
            var added = 0;
            foreach (var snapshot in latestPerModel)
            {
                if (!alreadyEvaluated.Add(snapshot.ModelName))
                    continue;

                var predicted = new OutcomeProbabilities(snapshot.HomeWin!.Value, snapshot.Draw!.Value, snapshot.AwayWin!.Value).Normalize();
                _db.Evaluations.Add(new PredictionEvaluation
                {
                    ModelName = snapshot.ModelName,
                    FixtureId = fixture.Id,
                    HomeTeamId = fixture.HomeTeamId,
                    AwayTeamId = fixture.AwayTeamId,
                    HomeGoals = homeGoals,
                    AwayGoals = awayGoals,
                    HomeWin = predicted.HomeWin,
                    Draw = predicted.Draw,
                    AwayWin = predicted.AwayWin,
                    Actual = actual,
                    BrierScore = ProbabilityHelper.BrierScore(predicted, actual),
                    RankedProbabilityScore = ProbabilityHelper.RankedProbabilityScore(predicted, actual),
                    LogLoss = ProbabilityHelper.LogLoss(predicted, actual),
                    TopPickCorrect = predicted.TopPick == actual,
                    PredictedAt = snapshot.CreatedAt
                });
                added++;
            }

            if (added == 0)
                return 0;

            // Record the played result once so it feeds future goal-strength fitting.
            var alreadyRecorded = await _db.Results.AnyAsync(r =>
                r.Source == "manual"
                && r.HomeTeamId == fixture.HomeTeamId
                && r.AwayTeamId == fixture.AwayTeamId
                && r.HomeGoals == homeGoals
                && r.AwayGoals == awayGoals, ct);
            if (!alreadyRecorded)
            {
                _db.Results.Add(new MatchResult
                {
                    Id = CryptoUtil.GetSha256($"manual|{DateTimeOffset.UtcNow:O}|{fixture.HomeTeamId}|{fixture.AwayTeamId}|{homeGoals}-{awayGoals}"),
                    HomeTeamId = fixture.HomeTeamId,
                    AwayTeamId = fixture.AwayTeamId,
                    HomeGoals = homeGoals,
                    AwayGoals = awayGoals,
                    Date = DateTimeOffset.UtcNow,
                    Tournament = "FIFA World Cup 2026",
                    Neutral = fixture.NeutralVenue,
                    Source = "manual"
                });
            }

            fixture.IsPlayed = true;
            fixture.HomeGoals = homeGoals;
            fixture.AwayGoals = awayGoals;
            await _db.SaveChangesAsync(ct);
            return added;
        }

        public async Task<FixtureEvaluationRefreshReport> EvaluateUnevaluatedPlayedFixturesAsync(CancellationToken ct = default)
        {
            var fixtures = await _db.Fixtures
                .Where(f => f.IsPlayed && f.HomeGoals.HasValue && f.AwayGoals.HasValue)
                .ToListAsync(ct);

            var evaluated = 0;
            var skippedAlreadyEvaluated = 0;
            var skippedWithoutSnapshot = 0;

            foreach (var fixture in fixtures)
            {
                var snapshotTimes = await _db.Snapshots
                    .Where(s => s.Kind == "match"
                        && s.FixtureId == fixture.Id
                        && s.HomeWin.HasValue
                        && s.Draw.HasValue
                        && s.AwayWin.HasValue)
                    .Select(s => s.CreatedAt)
                    .ToListAsync(ct);
                var kickoff = fixture.KickoffUtc;
                var hasUsableSnapshot = snapshotTimes.Any(createdAt => kickoff is null || createdAt <= kickoff.Value);
                if (!hasUsableSnapshot)
                {
                    skippedWithoutSnapshot++;
                    continue;
                }

                var count = await EvaluateLatestSnapshotAsync(fixture, fixture.HomeGoals!.Value, fixture.AwayGoals!.Value, ct);
                if (count == 0)
                    skippedAlreadyEvaluated++;
                else
                    evaluated += count;
            }

            return new FixtureEvaluationRefreshReport(
                evaluated,
                skippedAlreadyEvaluated,
                skippedWithoutSnapshot);
        }

        public async Task<IReadOnlyList<ModelPerformanceRow>> PerformanceAsync(CancellationToken ct = default)
        {
            var rows = await _db.Evaluations.AsNoTracking().ToListAsync(ct);
            return rows.GroupBy(e => e.ModelName)
                .Select(g => new ModelPerformanceRow
                {
                    ModelName = g.Key,
                    Count = g.Count(),
                    TopPickAccuracy = g.Average(e => e.TopPickCorrect ? 1.0 : 0.0),
                    MeanBrier = g.Average(e => e.BrierScore),
                    MeanRps = g.Average(e => e.RankedProbabilityScore),
                    MeanLogLoss = g.Average(e => e.LogLoss)
                })
                .OrderBy(r => r.MeanRps)
                .ToList();
        }

        public async Task<IReadOnlyList<PredictionEvaluation>> BestCallsAsync(int take = 8, CancellationToken ct = default) =>
            await _db.Evaluations.AsNoTracking().OrderBy(e => e.RankedProbabilityScore).Take(take).ToListAsync(ct);

        public async Task<IReadOnlyList<PredictionEvaluation>> OverconfidentFailuresAsync(int take = 8, CancellationToken ct = default) =>
            await _db.Evaluations.AsNoTracking()
                .Where(e => !e.TopPickCorrect)
                .OrderByDescending(e => Math.Max(e.HomeWin, Math.Max(e.Draw, e.AwayWin)))
                .Take(take)
                .ToListAsync(ct);

        public static string OutcomeFromGoals(int homeGoals, int awayGoals) =>
            homeGoals > awayGoals ? "Home" : awayGoals > homeGoals ? "Away" : "Draw";

        /// <summary>
        /// Per-model reliability multipliers derived from how accurate each model has been on
        /// played matches. This is what closes the learning loop: <see cref="Predictors.FinalPredictionSelector"/>
        /// multiplies every model's prior weight by its multiplier, so models that have been more
        /// accurate (lower Ranked Probability Score) gain influence and worse ones lose it.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, double>> ModelReliabilityWeightsAsync(CancellationToken ct = default) =>
            ComputeReliabilityWeights(await _db.Evaluations.AsNoTracking().ToListAsync(ct));

        /// <summary>
        /// Turns historical evaluations into reliability multipliers normalised so the average is ~1.
        /// Each model's mean RPS is shrunk toward the global mean by sample size, so a model with only
        /// a couple of lucky (or unlucky) calls does not swing the ensemble before it has earned it.
        /// </summary>
        public static IReadOnlyDictionary<string, double> ComputeReliabilityWeights(IReadOnlyList<PredictionEvaluation> evaluations)
        {
            // Pseudo-matches of the global prior mixed into every model, taming small samples.
            const double PriorStrength = 5.0;
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            if (evaluations.Count == 0)
                return weights;

            var globalMeanRps = evaluations.Average(e => e.RankedProbabilityScore);
            if (globalMeanRps <= 0 || double.IsNaN(globalMeanRps))
                return weights;

            var raw = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var group in evaluations.GroupBy(e => e.ModelName, StringComparer.Ordinal))
            {
                var count = group.Count();
                var meanRps = group.Average(e => e.RankedProbabilityScore);
                var shrunkRps = ((meanRps * count) + (globalMeanRps * PriorStrength)) / (count + PriorStrength);
                // Lower RPS is better, so a below-average error yields a multiplier above 1.
                raw[group.Key] = globalMeanRps / Math.Max(shrunkRps, 1e-6);
            }

            var mean = raw.Values.Average();
            if (mean <= 0 || double.IsNaN(mean))
                return weights;

            foreach (var (model, multiplier) in raw)
                weights[model] = multiplier / mean;

            return weights;
        }
    }

    public sealed record FixtureEvaluationRefreshReport(
        int Evaluated,
        int SkippedAlreadyEvaluated,
        int SkippedWithoutSnapshot);
}
