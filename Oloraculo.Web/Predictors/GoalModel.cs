using Oloraculo.Web.Models;
using Oloraculo.Web.Probability;

namespace Oloraculo.Web.Predictors
{
    public class GoalModel : IPredictor
    {
        private const double DefaultAverageGoals = 1.25;
        private const double PriorMatches = 2.0;
        private const double GoalScale = 1.10;
        // Dixon-Coles low-score correction. Previously hardcoded to 0.00, which silently
        // disabled the correction (tau == 1 for every scoreline) and made the model
        // systematically under-predict draws and low-scoring games. We now estimate rho
        // from the historical window and only fall back to this prior when data is thin.
        private const double DefaultLowScoreRho = -0.05;
        private const double LowScoreRhoMin = -0.20;
        private const double LowScoreRhoMax = 0.10;
        private const double HomeAdvantageMultiplier = 1.08;
        // Sanity-checked against bundled historical results; rerun tooling/goal_strength_calibration.py when revisiting.
        private const double GoalStrengthMinMultiplier = 0.25;
        private const double GoalStrengthMaxMultiplier = 3.5;
        private const int MinimumTeamMatches = 3;
        private const int Iterations = 8;

        private readonly IReadOnlyDictionary<string, GoalStrength> _strengths;
        private readonly double _avgGoals;
        private readonly double _rho;
        protected readonly int _matchesUsed;
        protected readonly int _yearsWindow;

        public GoalModel(IReadOnlyList<MatchResult> results, int yearsWindow = 8)
        {
            _yearsWindow = yearsWindow;
            (_strengths, _avgGoals, _matchesUsed, _rho) = Fit(results, yearsWindow);
        }

        public virtual string Name => "Modelo de goles (Poisson)";
        public virtual int Priority => 4;

        public virtual MatchPrediction Predict(MatchContext context)
        {
            var (homeGoals, awayGoals, degraded) = ExpectedGoals(context);
            var scoreline = BuildScoreline(homeGoals, awayGoals);
            var mostLikely = scoreline.MostLikelyScoreline();

            return new MatchPrediction
            {
                PredictorName = Name,
                PredictorPriority = Priority,
                FixtureId = context.Fixture.Id,
                HomeTeamId = context.HomeTeamId,
                AwayTeamId = context.AwayTeamId,
                Outcome = scoreline.ToOutcome(),
                ExpectedHomeGoals = Math.Round(homeGoals, 2),
                ExpectedAwayGoals = Math.Round(awayGoals, 2),
                Scoreline = scoreline,
                MostLikelyScore = mostLikely,
                Explanation = $"Goles esperados: {context.HomeTeam.Name} {homeGoals:0.00} - {awayGoals:0.00} {context.AwayTeam.Name}, ajustado con {_matchesUsed} resultados históricos en una ventana de {_yearsWindow} años.",
                Drivers = [$"Marcador más probable: {mostLikely.Home}-{mostLikely.Away}"],
                FeaturesUsed =
                [
                    "Fuerza de ataque ajustada por rival",
                    "Vulnerabilidad defensiva ajustada por rival",
                    "Grilla de marcadores Dixon-Coles"
                ],
                FeaturesMissing = degraded ? ["historial de goles suficiente para ambos equipos"] : [],
                Sources = [SourceMetadata.HistoricalResultsCsv],
                Degraded = degraded
            };
        }

        public (double Home, double Away, bool Degraded) ExpectedGoals(MatchContext context)
        {
            var hasHome = _strengths.TryGetValue(context.HomeTeamId, out var home);
            var hasAway = _strengths.TryGetValue(context.AwayTeamId, out var away);
            home ??= new GoalStrength();
            away ??= new GoalStrength();

            var degraded = !hasHome || !hasAway || home.Matches < MinimumTeamMatches || away.Matches < MinimumTeamMatches;
            var homeGoals = _avgGoals * home.Attack * away.DefenseVulnerability * GoalScale;
            var awayGoals = _avgGoals * away.Attack * home.DefenseVulnerability * GoalScale;
            if (!context.Fixture.NeutralVenue)
                homeGoals *= HomeAdvantageMultiplier;

            return (
                Math.Clamp(homeGoals, 0.1, 5.5),
                Math.Clamp(awayGoals, 0.1, 5.5),
                degraded);
        }

        public ScorelineDistribution BuildScoreline(double homeGoals, double awayGoals) =>
            ProbabilityHelper.PoissonScoreline(homeGoals, awayGoals, lowScoreRho: _rho);

        private static (IReadOnlyDictionary<string, GoalStrength> Strengths, double AvgGoals, int MatchesUsed, double Rho) Fit(IReadOnlyList<MatchResult> results, int yearsWindow)
        {
            if (results.Count == 0)
                return (new Dictionary<string, GoalStrength>(), DefaultAverageGoals, 0, DefaultLowScoreRho);

            var latest = results.Max(r => r.Date);
            var cutoff = yearsWindow > 0 ? latest.AddYears(-yearsWindow) : DateTimeOffset.MinValue;
            var window = results.Where(r => r.Date >= cutoff).ToList();
            if (window.Count == 0)
                window = results.ToList();

            var teams = window.SelectMany(r => new[] { r.HomeTeamId, r.AwayTeamId }).Distinct().ToList();
            var attacks = teams.ToDictionary(t => t, _ => 1.0);
            var vulnerabilities = teams.ToDictionary(t => t, _ => 1.0);
            var matches = teams.ToDictionary(t => t, _ => 0);

            foreach (var result in window)
            {
                matches[result.HomeTeamId]++;
                matches[result.AwayTeamId]++;
            }

            var weighted = window.Select(r =>
            {
                var yearsAgo = Math.Max(0, (latest - r.Date).TotalDays / 365.25);
                return (Result: r, Weight: Math.Pow(0.75, yearsAgo));
            }).ToList();

            var totalWeight = weighted.Sum(r => r.Weight);
            var avg = totalWeight <= 0
                ? DefaultAverageGoals
                : weighted.Sum(r => r.Weight * (r.Result.HomeGoals + r.Result.AwayGoals)) / (2.0 * totalWeight);
            avg = Math.Clamp(avg, 0.6, 2.4);

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var nextAttacks = new Dictionary<string, double>();
                var nextVulnerabilities = new Dictionary<string, double>();

                foreach (var team in teams)
                {
                    double goalsFor = 0;
                    double attackExpected = 0;
                    double goalsAgainst = 0;
                    double defenseExpected = 0;
                    double teamWeight = 0;

                    foreach (var (result, weight) in weighted)
                    {
                        if (result.HomeTeamId == team)
                        {
                            goalsFor += weight * result.HomeGoals;
                            attackExpected += weight * avg * vulnerabilities[result.AwayTeamId];
                            goalsAgainst += weight * result.AwayGoals;
                            defenseExpected += weight * avg * attacks[result.AwayTeamId];
                            teamWeight += weight;
                        }
                        else if (result.AwayTeamId == team)
                        {
                            goalsFor += weight * result.AwayGoals;
                            attackExpected += weight * avg * vulnerabilities[result.HomeTeamId];
                            goalsAgainst += weight * result.HomeGoals;
                            defenseExpected += weight * avg * attacks[result.HomeTeamId];
                            teamWeight += weight;
                        }
                    }

                    var rawAttack = attackExpected <= 0 ? 1.0 : goalsFor / attackExpected;
                    var rawVulnerability = defenseExpected <= 0 ? 1.0 : goalsAgainst / defenseExpected;
                    nextAttacks[team] = ShrinkToNeutral(rawAttack, teamWeight);
                    nextVulnerabilities[team] = ShrinkToNeutral(rawVulnerability, teamWeight);
                }

                NormalizeMean(nextAttacks);
                NormalizeMean(nextVulnerabilities);
                attacks = nextAttacks;
                vulnerabilities = nextVulnerabilities;
            }

            var map = teams.ToDictionary(team => team, team => new GoalStrength
            {
                Attack = Math.Clamp(attacks[team], GoalStrengthMinMultiplier, GoalStrengthMaxMultiplier),
                DefenseVulnerability = Math.Clamp(vulnerabilities[team], GoalStrengthMinMultiplier, GoalStrengthMaxMultiplier),
                Matches = matches[team]
            });

            var rho = FitRho(weighted, map, avg);

            return (map, avg, window.Count, rho);

            static double ShrinkToNeutral(double value, double weight) =>
                Math.Clamp(((value * weight) + PriorMatches) / (weight + PriorMatches), GoalStrengthMinMultiplier, GoalStrengthMaxMultiplier);

            static void NormalizeMean(Dictionary<string, double> values)
            {
                var mean = values.Count == 0 ? 1.0 : values.Values.Average();
                if (mean <= 0)
                    return;

                foreach (var key in values.Keys.ToList())
                    values[key] /= mean;
            }
        }

        /// <summary>
        /// Estimates the Dixon-Coles rho by maximum (recency-weighted) likelihood over the
        /// fitted window. Falls back to the prior when there are too few low-scoring games
        /// to estimate it reliably.
        /// </summary>
        private static double FitRho(
            IReadOnlyList<(MatchResult Result, double Weight)> weighted,
            IReadOnlyDictionary<string, GoalStrength> strengths,
            double avg)
        {
            var lowScoreMatches = weighted.Count(w => w.Result.HomeGoals <= 1 && w.Result.AwayGoals <= 1);
            if (lowScoreMatches < 30)
                return DefaultLowScoreRho;

            var bestRho = DefaultLowScoreRho;
            var bestLogLikelihood = double.NegativeInfinity;

            for (var rho = LowScoreRhoMin; rho <= LowScoreRhoMax + 1e-9; rho += 0.005)
            {
                var logLikelihood = 0.0;
                foreach (var (result, weight) in weighted)
                {
                    if (!strengths.TryGetValue(result.HomeTeamId, out var home) ||
                        !strengths.TryGetValue(result.AwayTeamId, out var away))
                        continue;

                    var lambdaHome = avg * home.Attack * away.DefenseVulnerability * GoalScale;
                    var lambdaAway = avg * away.Attack * home.DefenseVulnerability * GoalScale;
                    if (!result.Neutral)
                        lambdaHome *= HomeAdvantageMultiplier;
                    lambdaHome = Math.Clamp(lambdaHome, 0.05, 6.0);
                    lambdaAway = Math.Clamp(lambdaAway, 0.05, 6.0);

                    var probability = ProbabilityHelper.DixonColesScoreProbability(
                        result.HomeGoals, result.AwayGoals, lambdaHome, lambdaAway, rho);
                    logLikelihood += weight * Math.Log(Math.Max(probability, 1e-12));
                }

                if (logLikelihood > bestLogLikelihood)
                {
                    bestLogLikelihood = logLikelihood;
                    bestRho = rho;
                }
            }

            return Math.Clamp(bestRho, LowScoreRhoMin, LowScoreRhoMax);
        }
    }

    public sealed class GoalStrength
    {
        public double Attack { get; init; } = 1;
        public double DefenseVulnerability { get; init; } = 1;
        public int Matches { get; init; }
    }
}
