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

public class ApiFootballServiceTests : TestFixtures
{
    [Fact]
    public void ApiFootball_SquadResponseParsesPlayerPositions()
    {
        var parsed = JsonSerializer.Deserialize<ApiSquadResponse>("""
            {
              "response": [{
                "team": { "id": 2, "name": "France" },
                "players": [
                  { "id": 278, "name": "Kylian Mbappé", "position": "Attacker" },
                  { "id": 22090, "name": "W. Saliba", "position": "Defender" }
                ]
              }]
            }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(parsed);
        Assert.Equal("Attacker", parsed.Response[0].Players[0].Position);
        Assert.Equal("Defender", parsed.Response[0].Players[1].Position);
    }

    [Fact]
    public void ApiFootball_PlayerRoleMatchingHandlesAccentsAndInitialLastNames()
    {
        var candidates = new[]
        {
            new PlayerRoleCandidate(278, "Kylian Mbappé", "Attacker", "test"),
            new PlayerRoleCandidate(22090, "William Saliba", "Defender", "test")
        };

        var accent = ApiFootballService.MatchPlayerRole("Kylian Mbappe", candidates);
        var initial = ApiFootballService.MatchPlayerRole("W. Saliba", candidates);

        Assert.Equal(278, accent?.Id);
        Assert.Equal("Attacker", accent?.Position);
        Assert.Equal(22090, initial?.Id);
        Assert.Equal("Defender", initial?.Position);
    }

    [Fact]
    public async Task ApiFootball_RefreshFixturesStoresFinalScores()
    {
        await using var db = await NewDb();
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "argentina", AwayTeamId = "france" });
        await db.SaveChangesAsync();
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.test/fixtures?league=1&season=2026&timezone=UTC"] = """
                {
                  "response": [{
                    "fixture": {
                      "id": 10,
                      "date": "2026-06-12T20:00:00+00:00",
                      "venue": { "name": "Test Stadium", "city": "Test City" },
                      "status": { "short": "FT" }
                    },
                    "teams": {
                      "home": { "id": 1, "name": "Argentina" },
                      "away": { "id": 2, "name": "France" }
                    },
                    "goals": { "home": 2, "away": 1 }
                  }]
                }
                """
        });
        var api = ApiService(db, handler);

        var report = await api.RefreshFixturesAsync();
        var fixture = await db.Fixtures.FindAsync("f1");

        Assert.Equal(1, report.FixturesMatched);
        Assert.NotNull(fixture);
        Assert.True(fixture.IsPlayed);
        Assert.Equal(2, fixture.HomeGoals);
        Assert.Equal(1, fixture.AwayGoals);
        Assert.Equal("FT", fixture.Status);
    }

    [Fact]
    public async Task ApiFootball_RoleEnrichmentUpdatesClaimsWithoutDeletingEvidence()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Kylian Mbappe",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Kylian Mbappe"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            SupportingQuote = "France confirmed Kylian Mbappe will miss the match.",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.test/teams?league=1&season=2026"] = """
                {"response":[{"team":{"id":2,"name":"France"}}]}
                """,
            ["https://api.test/players/squads?team=2"] = """
                {"response":[{"team":{"id":2,"name":"France"},"players":[{"id":278,"name":"Kylian Mbappé","position":"Attacker"}]}]}
                """
        });
        var api = ApiService(db, handler);

        var report = await api.EnrichAvailabilityRolesAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());

        Assert.Equal(1, report.RoleMatchedClaims);
        Assert.Equal(278, claim.ApiFootballPlayerId);
        Assert.Equal("Attacker", claim.Position);
        Assert.Equal("France confirmed Kylian Mbappe will miss the match.", claim.SupportingQuote);
    }

    [Fact]
    public async Task ApiFootball_SquadFailureLeavesClaimsUnknown()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Mystery Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Mystery Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.test/teams?league=1&season=2026"] = """
                {"response":[{"team":{"id":2,"name":"France"}}]}
                """
        });
        var api = ApiService(db, handler);

        var report = await api.EnrichAvailabilityRolesAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());

        Assert.Equal(1, report.RoleUnknownClaims);
        Assert.Equal("Unknown", claim.Position);
    }

    private static ApiFootballService ApiService(OloraculoDbContext db, HttpMessageHandler handler)
    {
        var options = Options.Create(new OloraculoConfig
        {
            ApiFootballApiKey = "test-key",
            ApiFootballBaseUrl = "https://api.test/",
            ApiFootballLeagueId = 1,
            ApiFootballSeason = 2026,
            OpenRouterApiKey = "test-key",
            OpenRouterBaseUrl = "https://openrouter.test/",
            AvailabilitySourceUrls = []
        });
        var availability = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())) { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            options);

        return new ApiFootballService(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, db, options, availability);
    }

}
