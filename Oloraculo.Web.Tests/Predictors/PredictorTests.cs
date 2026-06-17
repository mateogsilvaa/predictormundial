using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Oloraculo.Web;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.ApiFootballModels;
using Oloraculo.Web.Models.CsvModels;
using Oloraculo.Web.Predictors;
using Oloraculo.Web.Probability;
using Oloraculo.Web.Services;
using Oloraculo.Web.Services.Simulation;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Oloraculo.Web.Tests;

public class PredictorTests : TestFixtures
{
    [Fact]
    public void GoalModel_ProducesUsableScorelineWhenTeamsHaveEnoughHistory()
    {
        var model = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);

        var prediction = model.Predict(TestContext());

        Assert.False(prediction.Degraded);
        Assert.NotNull(prediction.Scoreline);
        Assert.True(prediction.ExpectedHomeGoals > 0.1);
        Assert.True(prediction.Outcome.IsValid);
    }

    [Fact]
    public void GoalModel_ExtremeHistoryKeepsExpectedGoalsBoundedAndValid()
    {
        var now = DateTimeOffset.UtcNow;
        var results = Enumerable.Range(0, 6)
            .Select(i => new MatchResult
            {
                Id = $"extreme-{i}",
                HomeTeamId = "a",
                AwayTeamId = "b",
                HomeGoals = 20,
                AwayGoals = 0,
                Date = now.AddDays(-i),
                Tournament = "test",
                Neutral = true,
                Source = "test"
            })
            .ToList();
        var model = new GoalModel(results);

        var (homeGoals, awayGoals, degraded) = model.ExpectedGoals(TestContext());
        var prediction = model.Predict(TestContext());

        Assert.False(degraded);
        Assert.InRange(homeGoals, 0.1, 5.5);
        Assert.InRange(awayGoals, 0.1, 5.5);
        Assert.True(double.IsFinite(homeGoals));
        Assert.True(double.IsFinite(awayGoals));
        Assert.True(prediction.Outcome.IsValid);
    }

    [Fact]
    public void ContextModel_DoesNotClaimLineupsOrOddsWereUsedWithoutConversionLogic()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            HasLineups = true,
            HasOdds = true
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.DoesNotContain(nameof(FeaturesEnum.Lineups), prediction.FeaturesUsed);
        Assert.DoesNotContain(nameof(FeaturesEnum.Odds), prediction.FeaturesUsed);
        Assert.Contains("modelo de impacto de alineaciones", prediction.FeaturesMissing);
        Assert.Contains("calibración por cuotas", prediction.FeaturesMissing);
        Assert.True(prediction.Degraded);
    }

    [Fact]
    public void ContextModel_BecomesUsableWhenAvailabilityActuallyAdjustsGoals()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var context = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 2
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(context);

        Assert.False(prediction.Degraded);
        Assert.Contains("Disponibilidad de jugadores", prediction.FeaturesUsed);
    }

    [Fact]
    public void FinalSelector_BlendsUsableModelsWeightedByPriorPriority()
    {
        // form (priority 3) and goal (priority 4) are blended with prior weights 3 and 4.
        var form = Prediction(3, "Forma reciente", .05, .05, .90);
        var goal = Prediction(4, "Goal", .90, .05, .05, scoreline: ProbabilityHelper.PoissonScoreline(3.0, .4));

        var final = FinalPredictionSelector.Select([form, goal]);

        Assert.Equal("Oráculo final", final.PredictorName);
        Assert.Equal(4, final.PredictorPriority);
        // home = (3*.05 + 4*.90)/7 = 3.75/7; draw = .35/7; away = 2.90/7.
        Assert.Equal(0.5357, final.Outcome.HomeWin, 4);
        Assert.Equal(0.0500, final.Outcome.Draw, 4);
        Assert.Equal(0.4143, final.Outcome.AwayWin, 4);
        Assert.True(final.Outcome.IsValid);
    }

    [Fact]
    public void FinalSelector_ExcludesBaseAndDegradedModelsFromTheBlend()
    {
        var baseModel = Prediction(0, "Modelo base", 1.0 / 3, 1.0 / 3, 1.0 / 3);
        var form = Prediction(3, "Forma reciente", .05, .05, .90);
        var goal = Prediction(4, "Goal", .90, .05, .05);
        var degradedContext = Prediction(5, "Context", .10, .80, .10, degraded: true, missing: ["availability"]);

        var final = FinalPredictionSelector.Select([baseModel, form, goal, degradedContext]);

        // Identical to blending only form + goal: the base and degraded models do not move it.
        Assert.Equal(0.5357, final.Outcome.HomeWin, 4);
        Assert.Equal(0.0500, final.Outcome.Draw, 4);
        Assert.Equal(0.4143, final.Outcome.AwayWin, 4);
        Assert.Equal(4, final.PredictorPriority);
    }

    [Fact]
    public void FinalSelector_ReliabilityWeightsReweightTheBlend_TheLearningLoop()
    {
        var form = Prediction(3, "Forma reciente", .05, .05, .90);
        var goal = Prediction(4, "Goal", .90, .05, .05);

        // Without learning, the goal model dominates (home pick). Once the recent-form model is
        // shown to be far more accurate, the ensemble must shift toward its away pick.
        var withoutLearning = FinalPredictionSelector.Select([form, goal]);
        var withLearning = FinalPredictionSelector.Select(
            [form, goal],
            new Dictionary<string, double> { ["Forma reciente"] = 3.0, ["Goal"] = 0.5 });

        Assert.Equal("Home", withoutLearning.Outcome.TopPick);
        Assert.Equal("Away", withLearning.Outcome.TopPick);
        Assert.True(withLearning.Outcome.AwayWin > withoutLearning.Outcome.AwayWin);
    }

    [Fact]
    public void FinalSelector_ScorelineStaysConsistentWithBlendedTopPick()
    {
        // The goal model's own grid is strongly home-leaning, but the ensemble (driven by a very
        // reliable away-leaning form model) picks away. The displayed scoreline must follow the
        // blended outcome, not the anchor's raw most-likely score. This is the regression test for
        // the old bug where the headline probabilities and the shown scoreline could contradict.
        var form = Prediction(3, "Forma reciente", .02, .03, .95);
        var goal = Prediction(4, "Goal", .85, .10, .05, scoreline: ProbabilityHelper.PoissonScoreline(2.8, 0.4));

        var final = FinalPredictionSelector.Select(
            [form, goal],
            new Dictionary<string, double> { ["Forma reciente"] = 5.0 });

        Assert.Equal("Away", final.Outcome.TopPick);
        Assert.NotNull(final.MostLikelyScore);
        Assert.True(final.MostLikelyScore!.Value.Away > final.MostLikelyScore.Value.Home);
    }

}
