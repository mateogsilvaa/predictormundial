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

public class SimulationServiceTests : TestFixtures
{
    [Fact]
    public async Task Simulation_IsRepeatableWithSameSeed()
    {
        await using var db = await ImportedDb();
        var service = Simulation(db, simulations: 3, seed: 42);

        var one = await service.RunAsync(saveSnapshot: false);
        var two = await service.RunAsync(saveSnapshot: false);

        Assert.Equal(one.Teams.Select(t => t.WinTournament), two.Teams.Select(t => t.WinTournament));
        Assert.Equal(1.0, one.Teams.Sum(t => t.WinTournament), 6);
    }

    [Theory]
    [InlineData("argentina", "france")]
    [InlineData("france", "argentina")]
    [InlineData("mexico", "canada")]
    public async Task SimulationPredictionContext_MatchesPredictionServiceForPairs(string homeId, string awayId)
    {
        await using var db = await ImportedDb();
        var options = SimulationOptions(simulations: 1, seed: 42);
        var prediction = new PredictionService(db, options);
        var simulationPrediction = await SimulationPredictionContext.CreateAsync(db, options.Value);

        var expected = await prediction.PredictPairAsync(homeId, awayId);
        var actual = await simulationPrediction.PredictPairAsync(homeId, awayId);

        AssertPredictionResultEqual(expected, actual);
    }

    [Fact]
    public async Task Simulation_WithFixedSeedKeepsDeterministicTournamentOutput()
    {
        await using var db = await ImportedDb();
        var service = Simulation(db, simulations: 2, seed: 2026);

        var one = await service.RunAsync(saveSnapshot: false);
        var two = await service.RunAsync(saveSnapshot: false);

        Assert.Equal(2, one.Simulations);
        Assert.Equal(1.0, one.Teams.Sum(t => t.WinTournament), 6);
        Assert.Equal(one.Teams.Select(ProjectionKey), two.Teams.Select(ProjectionKey));
    }

    [Fact]
    public async Task Simulation_UsesKnownGroupFixtureScores()
    {
        await using var db = await ImportedDb();
        var mexicoFixtures = await db.Fixtures
            .Where(f => f.Group == "A" && (f.HomeTeamId == "mexico" || f.AwayTeamId == "mexico"))
            .ToListAsync();

        foreach (var fixture in mexicoFixtures)
        {
            fixture.IsPlayed = true;
            fixture.HomeGoals = fixture.HomeTeamId == "mexico" ? 10 : 0;
            fixture.AwayGoals = fixture.AwayTeamId == "mexico" ? 10 : 0;
        }
        await db.SaveChangesAsync();

        var projection = await Simulation(db, simulations: 5, seed: 7).RunAsync(saveSnapshot: false);
        var mexico = projection.Teams.Single(t => t.TeamId == "mexico");

        Assert.Equal(1.0, mexico.WinGroup, 6);
        Assert.Equal(1.0, mexico.Qualify, 6);
    }

    private static SimulationService Simulation(OloraculoDbContext db, int simulations, int seed)
    {
        var options = SimulationOptions(simulations, seed);
        var prediction = new PredictionService(db, options);
        var snapshots = new SnapshotService(db);
        return new SimulationService(db, prediction, snapshots, options);
    }

    private static object ProjectionKey(TeamTournamentProbability team) => new
    {
        team.TeamId,
        team.Group,
        team.WinGroup,
        team.Qualify,
        team.ReachRoundOf16,
        team.ReachQuarterFinal,
        team.ReachSemiFinal,
        team.ReachFinal,
        team.WinTournament,
        team.ExpectedGroupPoints
    };

}
