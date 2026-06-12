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

public class AvailabilityNewsServiceTests : TestFixtures
{
    [Fact]
    public void AvailabilityNews_ParsesStructuredClaims()
    {
        var claims = AvailabilityNewsService.ParseClaimsFromJson("""
            {
              "claims": [
                {
                  "player": "Example Star",
                  "team": "France",
                  "status": "ConfirmedOutInjury",
                  "reason": "knee injury",
                  "confidence": "high",
                  "evidenceLevel": "Official",
                  "supportingText": "France confirmed Example Star will miss the World Cup.",
                  "sourceUrl": "https://ignored.test",
                  "publishedOrObservedDate": "2026-06-09"
                }
              ]
            }
            """, "https://example.test/source", "example.test");

        var claim = Assert.Single(claims);
        Assert.Equal("Example Star", claim.Player);
        Assert.Equal("france", claim.TeamId);
        Assert.Equal(AvailabilityClaimStatus.ConfirmedOutInjury, claim.Status);
        Assert.Equal(AvailabilityEvidenceLevel.Official, claim.EvidenceLevel);
        Assert.Equal("https://example.test/source", claim.SourceUrl);
    }

    [Fact]
    public void AvailabilityNews_RejectsMalformedJson()
    {
        Assert.ThrowsAny<JsonException>(() => AvailabilityNewsService.ParseClaimsFromJson("not json", "https://example.test"));
    }

    [Fact]
    public void AvailabilityNews_DoesNotPromoteSoftFitnessLanguage()
    {
        var claims = AvailabilityNewsService.ParseClaimsFromJson("""
            {"claims":[{"player":"Careful Wording","team":"Argentina","status":"FitnessConcern","reason":"race to be fit","confidence":"medium","evidenceLevel":"ReportedUncertain","supportingText":"could miss","sourceUrl":"","publishedOrObservedDate":""}]}
            """, "https://example.test/source", "example.test");

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.False(Assert.Single(claims).AffectsPrediction);
    }

    [Fact]
    public void AvailabilityNews_CuratedReputableOutClaimsAffectPredictions()
    {
        var singleReputable = new AvailabilityClaim
        {
            Player = "One Source",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("One Source"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://one.test",
            Publisher = "one.test"
        };
        var official = new AvailabilityClaim
        {
            Player = "Official Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Official Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://federation.test",
            Publisher = "federation.test"
        };
        var crossA = new AvailabilityClaim
        {
            Player = "Cross Checked",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Cross Checked"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://a.test",
            Publisher = "a.test"
        };
        var crossB = new AvailabilityClaim
        {
            Player = "Cross Checked",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Cross Checked"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = "https://b.test",
            Publisher = "b.test"
        };
        var claims = new[] { singleReputable, official, crossA, crossB };

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.True(singleReputable.AffectsPrediction);
        Assert.True(official.AffectsPrediction);
        Assert.True(crossA.AffectsPrediction);
        Assert.True(crossB.AffectsPrediction);
    }

    [Fact]
    public void AvailabilityNews_ParsesTalkSportTrackerRows()
    {
        var claims = AvailabilityNewsService.ParseTrackerClaims(TalkSportSample(), "https://talksport.test/tracker", "talksport.com").ToList();

        AvailabilityNewsService.ApplyPredictionFlags(claims, requireCrossCheck: true);

        Assert.Equal(19, claims.Count);
        AssertClaim(claims, "Moïse Bombito", "canada", AvailabilityClaimStatus.ConfirmedOutInjury, affects: true);
        AssertClaim(claims, "Wesley França", "brazil", AvailabilityClaimStatus.ConfirmedOutInjury, affects: true);
        AssertClaim(claims, "Nayef Aguerd", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Julio Enciso", "paraguay", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Denil Castillo", "ecuador", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Sebastian Caceres", "uruguay", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Chris Richards", "united-states", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Edson Alvarez", "mexico", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Alfie Jones", "canada", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Wataru Endo", "japan", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Abde Ezzalzouli", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Noussair Mazraoui", "morocco", AvailabilityClaimStatus.Doubtful, affects: false);
        AssertClaim(claims, "Lamine Yamal", "spain", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Nico Williams", "spain", AvailabilityClaimStatus.Available, affects: false);
        AssertClaim(claims, "Victor Munoz", "spain", AvailabilityClaimStatus.Available, affects: false);
    }

    [Fact]
    public async Task AvailabilityNews_SourceFetchFailureRecordsWarningAndContinues()
    {
        await using var db = await NewDb();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())) { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://missing.test/article"]));

        var report = await service.RefreshAsync();

        Assert.Equal(1, report.SourcesSkipped);
        Assert.NotEmpty(report.Errors);
        Assert.Empty(await db.AvailabilityClaims.ToListAsync());
    }

    [Fact]
    public async Task AvailabilityNews_BotGateIsSkippedWithWarning()
    {
        await using var db = await NewDb();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://espn.test/article"] = "<html><title>Blocked</title><body>Please enable JavaScript to continue.</body></html>"
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://espn.test/article"]));

        var report = await service.RefreshAsync();

        Assert.Equal(1, report.SourcesSkipped);
        Assert.Contains(report.Errors, e => e.Contains("bloqueada", StringComparison.OrdinalIgnoreCase) || e.Contains("JavaScript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AvailabilityNews_DeterministicTrackerRowsAreSavedWhenModelMisses()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml(TalkSportSample()),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        var report = await service.RefreshAsync();
        var claims = await db.AvailabilityClaims.ToListAsync();

        Assert.Equal(19, report.ClaimsSaved);
        Assert.Equal(2, report.ConfirmedOutClaims);
        Assert.Equal(8, report.DoubtfulClaims);
        Assert.Equal(9, report.AvailableClaims);
        Assert.Contains(claims, c => c.Player == "Moïse Bombito" && c.AffectsPrediction);
        Assert.Contains(claims, c => c.Player == "Edson Alvarez" && c.Status == AvailabilityClaimStatus.Available && !c.AffectsPrediction);
    }

    [Fact]
    public async Task AvailabilityNews_MalformedModelJsonStillSavesDeterministicTrackerRows()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml(TalkSportSample()),
                ["https://openrouter.test/chat/completions"] = "not json"
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        var report = await service.RefreshAsync();

        Assert.NotEmpty(report.Errors);
        Assert.Equal(19, await db.AvailabilityClaims.CountAsync());
        Assert.Contains(await db.AvailabilityClaims.ToListAsync(), c => c.Player == "Wesley França" && c.AffectsPrediction);
    }

    [Fact]
    public async Task AvailabilityNews_SourceReplacementRemovesStaleClaimsAndKeepsFreshLedger()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Stale Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Stale Player"),
            TeamId = "canada",
            TeamName = "Canada",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.ReputableReported,
            SourceUrl = sourceUrl,
            Publisher = "talksport.com",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var claims = await db.AvailabilityClaims.ToListAsync();

        Assert.DoesNotContain(claims, c => c.Player == "Stale Player");
        Assert.Contains(claims, c => c.Player == "Moïse Bombito");
    }

    [Fact]
    public async Task AvailabilityNews_OpenRouterFailureKeepsExistingClaims()
    {
        await using var db = await NewDb();
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Existing",
            PlayerKey = "existing",
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test/article"
        });
        await db.SaveChangesAsync();

        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://source.test/article"] = LongArticleHtml("France confirmed Existing will miss the World Cup.")
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions(["https://source.test/article"]));

        var report = await service.RefreshAsync();

        Assert.NotEmpty(report.Errors);
        Assert.Equal("Existing", Assert.Single(await db.AvailabilityClaims.ToListAsync()).Player);
    }

    [Fact]
    public async Task AvailabilityNews_SingleCuratedOutClaimUpdatesFixtureContextAndSources()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.Teams.AddRange(new Team { Id = "mexico", Name = "Mexico" }, new Team { Id = "canada", Name = "Canada" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "mexico", AwayTeamId = "canada" });
        db.Results.AddRange(
            Result("mexico", "canada", 2, 0),
            Result("mexico", "canada", 1, 0),
            Result("canada", "mexico", 1, 2));
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var context = await db.FixtureContexts.FindAsync("f1");
        var prediction = await new PredictionService(db, SimulationOptions(1, 1)).PredictFixtureAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(1, context.UnavailableAwayPlayers);
        Assert.Contains(SourceMetadata.AvailabilityNews, prediction!.Predictions.Single(p => p.PredictorPriority == 5).Sources);
    }

    [Fact]
    public async Task AvailabilityNews_AvailableRowsAreStoredButDoNotAlterUnavailableCounts()
    {
        await using var db = await NewDb();
        var sourceUrl = "https://talksport.test/tracker";
        db.Teams.AddRange(new Team { Id = "mexico", Name = "Mexico" }, new Team { Id = "canada", Name = "Canada" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "mexico", AwayTeamId = "canada" });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(
            new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                [sourceUrl] = LongArticleHtml("Edson Alvarez, Mexico - West Ham player underwent ankle surgery in February but is now back fit. IN."),
                ["https://openrouter.test/chat/completions"] = EmptyOpenRouterResponse()
            }))
            { BaseAddress = new Uri("https://openrouter.test/") },
            db,
            AvailabilityOptions([sourceUrl]));

        await service.RefreshAsync();
        var claim = Assert.Single(await db.AvailabilityClaims.ToListAsync());
        var context = await db.FixtureContexts.FindAsync("f1");

        Assert.Equal(AvailabilityClaimStatus.Available, claim.Status);
        Assert.False(claim.AffectsPrediction);
        Assert.NotNull(context);
        Assert.Equal(0, context.UnavailableHomePlayers);
        Assert.Equal(0, context.UnavailableAwayPlayers);
    }

    [Fact]
    public async Task AvailabilityNews_RefreshUpdatesFixtureContextAndPredictionSources()
    {
        await using var db = await NewDb();
        db.Teams.AddRange(new Team { Id = "france", Name = "France" }, new Team { Id = "argentina", Name = "Argentina" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "france", AwayTeamId = "argentina" });
        db.Results.AddRange(
            Result("france", "argentina", 2, 0),
            Result("france", "argentina", 1, 0),
            Result("argentina", "france", 1, 2));
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Official Player",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Official Player"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://federation.test",
            Publisher = "federation.test",
            AffectsPrediction = true
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())), db, AvailabilityOptions([]));

        await service.RefreshFixtureContextAsync("f1");
        var context = await db.FixtureContexts.FindAsync("f1");
        var prediction = await new PredictionService(db, SimulationOptions(1, 1)).PredictFixtureAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(1, context.UnavailableHomePlayers);
        Assert.True(context.HasAvailabilityNews);
        Assert.Contains(SourceMetadata.AvailabilityNews, prediction!.Predictions.Single(p => p.PredictorPriority == 5).Sources);
    }


    [Fact]
    public void AvailabilityNews_PositionImpactsUseUnknownFallbackAndClampTotals()
    {
        Assert.Equal((0.020, 0.000), AvailabilityNewsService.ImpactForPosition("Unknown"));

        var clamped = AvailabilityNewsService.SumImpacts(Enumerable.Repeat("Goalkeeper", 10));

        Assert.Equal(0.0, clamped.Attack);
        Assert.Equal(0.18, clamped.Defense);
    }

    [Fact]
    public void ContextModel_AttackerAbsenceReducesOwnXgMoreThanDefenderAbsence()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var attackerContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Attacker").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Attacker").Defense
        });
        var defenderContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Defender").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Defender").Defense
        });

        var attackerPrediction = new GoalPlusRecentContextModel(goal).Predict(attackerContext);
        var defenderPrediction = new GoalPlusRecentContextModel(goal).Predict(defenderContext);

        Assert.True(attackerPrediction.ExpectedHomeGoals < defenderPrediction.ExpectedHomeGoals);
    }

    [Fact]
    public void ContextModel_DefenderAbsenceRaisesOpponentXg()
    {
        var goal = new GoalModel(
        [
            Result("a", "b", 2, 0),
            Result("a", "b", 1, 0),
            Result("b", "a", 1, 2)
        ]);
        var baseline = new GoalPlusRecentContextModel(goal).Predict(TestContext());
        var defenderContext = TestContext(fixtureContext: new FixtureContext
        {
            FixtureId = "test",
            UnavailableHomePlayers = 1,
            UnavailableHomeAttackImpact = AvailabilityNewsService.ImpactForPosition("Defender").Attack,
            UnavailableHomeDefenseImpact = AvailabilityNewsService.ImpactForPosition("Defender").Defense
        });

        var prediction = new GoalPlusRecentContextModel(goal).Predict(defenderContext);

        Assert.True(prediction.ExpectedAwayGoals > baseline.ExpectedAwayGoals);
    }


    [Fact]
    public async Task AvailabilityNews_RoleAwareFixtureContextStoresImpacts()
    {
        await using var db = await NewDb();
        db.Teams.AddRange(new Team { Id = "france", Name = "France" }, new Team { Id = "argentina", Name = "Argentina" });
        db.Fixtures.Add(new Fixture { Id = "f1", Group = "A", HomeTeamId = "france", AwayTeamId = "argentina" });
        db.AvailabilityClaims.Add(new AvailabilityClaim
        {
            Player = "Kylian Mbappe",
            PlayerKey = AvailabilityNewsService.NormalizePlayerKey("Kylian Mbappe"),
            TeamId = "france",
            TeamName = "France",
            Status = AvailabilityClaimStatus.ConfirmedOutInjury,
            EvidenceLevel = AvailabilityEvidenceLevel.Official,
            SourceUrl = "https://source.test",
            AffectsPrediction = true,
            Position = "Attacker"
        });
        await db.SaveChangesAsync();
        var service = new AvailabilityNewsService(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>())), db, AvailabilityOptions([]));

        await service.RefreshFixtureContextAsync("f1");
        var context = await db.FixtureContexts.FindAsync("f1");

        Assert.NotNull(context);
        Assert.Equal(0.035, context.UnavailableHomeAttackImpact, 3);
        Assert.Equal(0.003, context.UnavailableHomeDefenseImpact, 3);
    }

    private static string LongArticleHtml(string body) =>
        $"""
        <html><head><title>Availability tracker</title></head><body>
        <article>
        {body}
        This article contains enough surrounding text to be accepted by the parser for testing purposes. It repeats the availability details with clear sourcing and additional tournament context so the stripped text is long enough for the service to send to the model.
        </article>
        </body></html>
        """;

    private static string EmptyOpenRouterResponse() =>
        """
        {"choices":[{"message":{"content":"{\"claims\":[]}"}}]}
        """;

    private static string TalkSportSample() =>
        """
        World Cup 2026 injury tracker
        Edson Alvarez, Mexico - West Ham player underwent ankle surgery in February but is now back fit. IN.
        Alfie Jones, Canada - English-born defender underwent ankle surgery playing for Middlesbrough but is now back fit. IN.
        Moïse Bombito, Canada - Leg soreness following return from broken leg in October 2025 to rule him out of World Cup. OUT.
        Amir Hadziahmetovic, Bosnia - Hull City loanee underwent surgery for a meniscus injury in April but is now back fit. IN.
        Wesley França, Brazil - Thigh injury suffered in 2-1 win over Egypt and now replaced by Manchester United-bound Ederson. OUT.
        Nayef Aguerd, Morocco - Ex-West Ham defender has not played since March 4 due to groin issues. MAJOR DOUBT.
        Abde Ezzalzouli and Noussair Mazraoui, Morocco - Both players forced off with injuries in Sunday's 1-1 draw with Norway. MAJOR DOUBTS.
        Julio Enciso, Paraguay - Former Brighton player stretchered off in tears during 4-0 win over Nicaragua. MAJOR DOUBT.
        Denil Castillo, Ecuador - Midtjylland midfielder withdrew from the March squad through injury but featured in a 2-1 win over Saudi Arabia last month. He was then left out of the squad for 3-0 victory against Guatemala. DOUBT.
        Jurrien Timber, Netherlands - Arsenal star returned off the bench in the Champions League final from an ankle injury suffered on March 13 but still lacking match fitness. DOUBT.
        Wataru Endo, Japan - Liverpool midfielder sustained an ankle ligament injury on February 11 but returned for Japan's pre-World Cup friendly win over Iceland. IN.
        Tyler Bindon, New Zealand - Ankle injury saw him miss Sheffield United's last two games of the season but featured off the bench in his country's 1-0 defeat to England. IN.
        Lamine Yamal, Nico Williams and Victor Munoz, Spain - See above for further details. IN.
        Sebastian Caceres, Uruguay - Called up by Marcelo Bielsa despite suffering a facial fracture playing for Mexican club America. DOUBT.
        Cristian Romero, Argentina - Tottenham captain missed remainder of club season with a knee injury suffered in a 1-0 loss at Sunderland on April 12 but returned off the bench in his country's 2-0 win over Honduras. IN.
        Chris Richards, USMNT - Unused substitute for Conference League final after injuring ankle ligaments for Crystal Palace against Brentford on May 17. DOUBT.
        """;

    private static void AssertClaim(IReadOnlyList<AvailabilityClaim> claims, string player, string teamId, AvailabilityClaimStatus status, bool affects)
    {
        var claim = Assert.Single(claims, c => c.Player == player);
        Assert.Equal(teamId, claim.TeamId);
        Assert.Equal(status, claim.Status);
        Assert.Equal(affects, claim.AffectsPrediction);
    }

}
