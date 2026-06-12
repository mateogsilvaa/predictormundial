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

public class PredictionServiceTests : TestFixtures
{
    [Fact]
    public async Task PredictionService_BulkPredictsImportedGroupFixtures()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures.AsNoTracking().ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var results = await service.PredictFixturesAsync(fixtures);

        Assert.Equal(72, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Fixture.Id));
            Assert.True(result.BestPrediction.Outcome.IsValid);
        });
    }

    [Fact]
    public async Task PredictionService_BulkPredictionsMatchSingleFixturePredictions()
    {
        await using var db = await ImportedDb();
        var fixtures = await db.Fixtures
            .AsNoTracking()
            .OrderBy(f => f.Group)
            .ThenBy(f => f.HomeTeamId)
            .ThenBy(f => f.AwayTeamId)
            .Take(3)
            .ToListAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var bulk = await service.PredictFixturesAsync(fixtures);

        foreach (var fixture in fixtures)
        {
            var expected = await service.PredictFixtureAsync(fixture.Id);
            var actual = bulk.Single(result => result.Fixture.Id == fixture.Id);

            Assert.NotNull(expected);
            AssertPredictionResultEqual(expected, actual);
        }
    }

    [Fact]
    public async Task PredictionService_BulkPredictionUsesFixtureIdsWhenTeamsAreMissing()
    {
        await using var db = await NewDb();
        var fixture = new Fixture { Id = "f1", Group = "A", HomeTeamId = "ghost-home", AwayTeamId = "ghost-away" };
        db.Fixtures.Add(fixture);
        await db.SaveChangesAsync();
        var service = new PredictionService(db, SimulationOptions(1, 1));

        var result = Assert.Single(await service.PredictFixturesAsync([fixture]));

        Assert.Equal("ghost-home", result.HomeTeamName);
        Assert.Equal("ghost-away", result.AwayTeamName);
        Assert.True(result.BestPrediction.Outcome.IsValid);
    }

}
