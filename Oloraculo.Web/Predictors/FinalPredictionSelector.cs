using Oloraculo.Web.Models;
using Oloraculo.Web.Probability;

namespace Oloraculo.Web.Predictors
{
    /// <summary>
    /// Combines the predictor ladder into a single "Oráculo final" prediction.
    ///
    /// Historically this just picked the highest-priority usable model and applied a small
    /// hardcoded Elo/FIFA bias. That threw away every other model and never learned from past
    /// results. It now builds a weighted ensemble of all usable models where each model's weight
    /// is its prior (priority) times a reliability multiplier derived from how accurate that model
    /// has been on previously played matches (see <see cref="Services.EvaluationService"/>). The
    /// scoreline shown is always made consistent with the blended 1X2 probabilities.
    /// </summary>
    public static class FinalPredictionSelector
    {
        private const string BaseModelName = "Modelo base";
        private const double MinReliabilityMultiplier = 0.25;
        private const double MaxReliabilityMultiplier = 4.0;

        public static MatchPrediction Select(
            IReadOnlyList<MatchPrediction> ladder,
            IReadOnlyDictionary<string, double>? reliabilityWeights = null)
        {
            if (ladder.Count == 0)
                return EmptyFinal();

            var ordered = ladder.OrderBy(p => p.PredictorPriority).ToList();

            // The richest usable model anchors the scoreline grid and expected goals.
            var anchor = ordered.LastOrDefault(p => !p.Degraded) ?? ordered.First();

            // Ensemble members: every usable model that carries real signal. The uniform base
            // model only exists as a last-resort fallback, so it never joins the blend.
            var members = ordered
                .Where(p => !p.Degraded && !IsBaseModel(p))
                .ToList();

            if (members.Count == 0)
                return BuildFromSingle(anchor);

            var blended = BlendOutcomes(members, reliabilityWeights, out var weightDrivers);

            // Keep the displayed scoreline consistent with the blended headline probabilities:
            // pick the most likely exact score whose result matches the ensemble's top pick.
            var mostLikely = anchor.Scoreline is { } distribution
                ? MostLikelyScorelineForOutcome(distribution, blended.TopPick)
                : anchor.MostLikelyScore;

            var drivers = new List<string>
            {
                $"Ensemble ponderado de {members.Count} modelo(s) usable(s), reponderado por precisión histórica."
            };
            drivers.AddRange(weightDrivers);
            drivers.AddRange(anchor.Drivers);

            var sources = anchor.Sources
                .Concat(members.SelectMany(member => member.Sources))
                // Notes carries the anchor model name so the README "Model:" column still works.
                .Concat([new SourceMetadata("model ladder", "derived", Notes: anchor.PredictorName)])
                .Distinct()
                .ToList();

            return new MatchPrediction
            {
                PredictorName = "Oráculo final",
                PredictorPriority = anchor.PredictorPriority,
                FixtureId = anchor.FixtureId,
                HomeTeamId = anchor.HomeTeamId,
                AwayTeamId = anchor.AwayTeamId,
                Outcome = blended,
                ExpectedHomeGoals = anchor.ExpectedHomeGoals,
                ExpectedAwayGoals = anchor.ExpectedAwayGoals,
                Scoreline = anchor.Scoreline,
                MostLikelyScore = mostLikely,
                Explanation = BuildExplanation(anchor, members, reliabilityWeights),
                Drivers = drivers,
                FeaturesUsed = anchor.FeaturesUsed,
                FeaturesMissing = anchor.FeaturesMissing,
                Sources = sources,
                Degraded = anchor.Degraded
            };
        }

        private static OutcomeProbabilities BlendOutcomes(
            IReadOnlyList<MatchPrediction> members,
            IReadOnlyDictionary<string, double>? reliabilityWeights,
            out List<string> weightDrivers)
        {
            weightDrivers = new List<string>();
            double home = 0, draw = 0, away = 0, totalWeight = 0;

            foreach (var member in members)
            {
                var prior = PriorWeight(member.PredictorPriority);
                var reliability = ReliabilityMultiplier(reliabilityWeights, member.PredictorName);
                var weight = prior * reliability;

                home += weight * member.Outcome.HomeWin;
                draw += weight * member.Outcome.Draw;
                away += weight * member.Outcome.AwayWin;
                totalWeight += weight;

                weightDrivers.Add(
                    $"{member.PredictorName}: peso {weight:0.00} (prior {prior:0.0} × fiabilidad {reliability:0.00}).");
            }

            if (totalWeight <= 0)
                return members[^1].Outcome;

            return new OutcomeProbabilities(home, draw, away).Normalize();
        }

        /// <summary>
        /// Prior weight before any learning kicks in. Richer models (higher priority) start with
        /// more influence; the reliability multiplier then nudges this up or down based on results.
        /// </summary>
        private static double PriorWeight(int priority) => Math.Max(1.0, priority);

        private static double ReliabilityMultiplier(
            IReadOnlyDictionary<string, double>? reliabilityWeights,
            string modelName)
        {
            if (reliabilityWeights is null || !reliabilityWeights.TryGetValue(modelName, out var multiplier))
                return 1.0;

            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0)
                return 1.0;

            return Math.Clamp(multiplier, MinReliabilityMultiplier, MaxReliabilityMultiplier);
        }

        /// <summary>
        /// Most likely exact scoreline consistent with a target 1X2 outcome, so the headline
        /// probabilities and the displayed score can never contradict each other.
        /// </summary>
        public static (int Home, int Away) MostLikelyScorelineForOutcome(ScorelineDistribution distribution, string outcome)
        {
            var best = (Home: -1, Away: -1, Probability: -1.0);
            for (var h = 0; h <= distribution.MaxGoals; h++)
            {
                for (var a = 0; a <= distribution.MaxGoals; a++)
                {
                    var matches = outcome switch
                    {
                        "Home" => h > a,
                        "Away" => a > h,
                        "Draw" => h == a,
                        _ => true
                    };
                    if (matches && distribution.Probability(h, a) > best.Probability)
                        best = (h, a, distribution.Probability(h, a));
                }
            }

            return best.Home < 0 ? distribution.MostLikelyScoreline() : (best.Home, best.Away);
        }

        private static MatchPrediction BuildFromSingle(MatchPrediction anchor) => new()
        {
            PredictorName = "Oráculo final",
            PredictorPriority = anchor.PredictorPriority,
            FixtureId = anchor.FixtureId,
            HomeTeamId = anchor.HomeTeamId,
            AwayTeamId = anchor.AwayTeamId,
            Outcome = anchor.Outcome,
            ExpectedHomeGoals = anchor.ExpectedHomeGoals,
            ExpectedAwayGoals = anchor.ExpectedAwayGoals,
            Scoreline = anchor.Scoreline,
            MostLikelyScore = anchor.MostLikelyScore,
            Explanation = $"El Oráculo final usó {anchor.PredictorName}, el único escalón usable. {anchor.Explanation}",
            Drivers = new List<string> { $"Sin ensemble: solo {anchor.PredictorName} era usable." }
                .Concat(anchor.Drivers)
                .ToList(),
            FeaturesUsed = anchor.FeaturesUsed,
            FeaturesMissing = anchor.FeaturesMissing,
            Sources = anchor.Sources
                .Concat([new SourceMetadata("model ladder", "derived", Notes: anchor.PredictorName)])
                .Distinct()
                .ToList(),
            Degraded = anchor.Degraded
        };

        private static string BuildExplanation(
            MatchPrediction anchor,
            IReadOnlyList<MatchPrediction> members,
            IReadOnlyDictionary<string, double>? reliabilityWeights)
        {
            var names = string.Join(", ", members.Select(m => m.PredictorName));
            var learning = reliabilityWeights is { Count: > 0 }
                ? " Los pesos se reponderaron con la precisión histórica medida (Brier/RPS) de cada modelo."
                : " Todavía no hay suficiente historial evaluado, así que se usan los pesos a priori por modelo.";
            return $"El Oráculo final combinó {members.Count} modelo(s) usable(s) ({names}) en un ensemble ponderado, " +
                $"anclando el marcador en {anchor.PredictorName}.{learning} {anchor.Explanation}";
        }

        private static bool IsBaseModel(MatchPrediction prediction) =>
            prediction.PredictorPriority <= 0 ||
            string.Equals(prediction.PredictorName, BaseModelName, StringComparison.Ordinal);

        private static MatchPrediction EmptyFinal() => new()
        {
            PredictorName = "Oráculo final",
            PredictorPriority = 0,
            Outcome = OutcomeProbabilities.Uniform,
            Explanation = "El Oráculo final no tenía predicciones de la escalera, así que devolvió la base.",
            Drivers = ["No había predicciones disponibles en la escalera."],
            FeaturesMissing = ["predicciones de la escalera"],
            Sources = [new SourceMetadata("model ladder", "derived")],
            Degraded = true
        };
    }
}
