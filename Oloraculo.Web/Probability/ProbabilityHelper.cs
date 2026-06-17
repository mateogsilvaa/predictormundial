namespace Oloraculo.Web.Probability
{
    public static class ProbabilityHelper
    {
        public static double EloExpectation(double a, double b)
        {
            return 1.0 / (1.0 + Math.Pow(10, (b - a) / 400.0));
        }

        public static OutcomeProbabilities OutcomeFromExpectation(double expectedHome, double strengthGap)
        {
            var closenessGap = Math.Abs(strengthGap);
            var drawProbability = 0.30 * Math.Exp(-closenessGap / 550.0) + 0.08;
            drawProbability = Math.Clamp(drawProbability, 0.08, 0.34);
            var remainingProbability = 1.0 - drawProbability;

            return new OutcomeProbabilities(
                expectedHome * remainingProbability,
                drawProbability,
                remainingProbability * (1.0 - expectedHome)).Normalize();
        }

        public static ScorelineDistribution PoissonScoreline(double lambdaHome, double lambdaAway, int maxGoals = 8, double lowScoreRho = -0.06)
        {
            lambdaHome = Math.Clamp(lambdaHome, 0.05, 6.0);
            lambdaAway = Math.Clamp(lambdaAway, 0.05, 6.0);
            var matrix = new double[maxGoals + 1, maxGoals + 1];
            double sum = 0;

            for (var h = 0; h <= maxGoals; h++)
            {
                for (var a = 0; a <= maxGoals; a++)
                {
                    var probability = Poisson(lambdaHome, h) *
                        Poisson(lambdaAway, a) *
                        DixonColesTau(h, a, lambdaHome, lambdaAway, lowScoreRho);
                    probability = Math.Max(probability, 0);
                    matrix[h, a] = probability;
                    sum += probability;
                }
            }

            if (sum > 0)
            {
                for (var h = 0; h <= maxGoals; h++)
                {
                    for (var a = 0; a <= maxGoals; a++)
                    {
                        matrix[h, a] /= sum;
                    }
                }
            }

            return new ScorelineDistribution { MaxGoals = maxGoals, Matrix = matrix };
        }

        public static (int Home, int Away) SampleScore(ScorelineDistribution distribution, Random rng)
        {
            var roll = rng.NextDouble();
            double cumulative = 0;
            for (var h = 0; h <= distribution.MaxGoals; h++)
            {
                for (var a = 0; a <= distribution.MaxGoals; a++)
                {
                    cumulative += distribution.Probability(h, a);
                    if (roll <= cumulative)
                        return (h, a);
                }
            }
            return distribution.MostLikelyScoreline();
        }

        public static double BrierScore(OutcomeProbabilities p, string actual)
        {
            var h = actual == "Home" ? 1 : 0;
            var d = actual == "Draw" ? 1 : 0;
            var a = actual == "Away" ? 1 : 0;
            return Math.Pow(p.HomeWin - h, 2) + Math.Pow(p.Draw - d, 2) + Math.Pow(p.AwayWin - a, 2);
        }

        public static double RankedProbabilityScore(OutcomeProbabilities p, string actual)
        {
            var o1 = actual == "Home" ? 1 : 0;
            var o2 = actual is "Home" or "Draw" ? 1 : 0;
            var p1 = p.HomeWin;
            var p2 = p.HomeWin + p.Draw;
            return (Math.Pow(p1 - o1, 2) + Math.Pow(p2 - o2, 2)) / 2.0;
        }

        public static double LogLoss(OutcomeProbabilities p, string actual)
        {
            var probability = actual switch
            {
                "Home" => p.HomeWin,
                "Draw" => p.Draw,
                _ => p.AwayWin
            };
            return -Math.Log(Math.Clamp(probability, 0.001, 0.999));
        }

        /// <summary>
        /// Dixon-Coles low-score correction factor (tau). Adjusts the independent-Poisson
        /// probabilities for the 0-0, 0-1, 1-0 and 1-1 scorelines, where real football
        /// results deviate most from the independence assumption.
        /// </summary>
        public static double DixonColesTau(int homeGoals, int awayGoals, double lambdaHome, double lambdaAway, double lowScoreRho)
        {
            return (homeGoals, awayGoals) switch
            {
                (0, 0) => 1.0 - lambdaHome * lambdaAway * lowScoreRho,
                (0, 1) => 1.0 + lambdaHome * lowScoreRho,
                (1, 0) => 1.0 + lambdaAway * lowScoreRho,
                (1, 1) => 1.0 - lowScoreRho,
                _ => 1.0
            };
        }

        /// <summary>
        /// Joint probability of an exact scoreline under the Dixon-Coles model:
        /// independent Poisson marginals multiplied by the low-score tau correction.
        /// Used both for the scoreline grid and for data-driven rho calibration.
        /// </summary>
        public static double DixonColesScoreProbability(int homeGoals, int awayGoals, double lambdaHome, double lambdaAway, double lowScoreRho) =>
            Math.Max(0, Poisson(lambdaHome, homeGoals)
                * Poisson(lambdaAway, awayGoals)
                * DixonColesTau(homeGoals, awayGoals, lambdaHome, lambdaAway, lowScoreRho));

        private static double Poisson(double lambda, int k)
        {
            var factorial = 1.0;
            for (var i = 2; i <= k; i++)
                factorial *= i;
            return Math.Pow(lambda, k) * Math.Exp(-lambda) / factorial;
        }
    }
}
