using Oloraculo.Web.Models;
using Oloraculo.Web.Models.FootballDataModels;
using Oloraculo.Web.Services;

namespace Oloraculo.Web.Tests;

public class FootballDataServiceTests
{
    [Fact]
    public void ShouldHaveFinished_OnlyTrueAfterKickoffPlusDuration()
    {
        var kickoff = DateTimeOffset.Parse("2026-06-17T16:00:00Z");
        var duration = TimeSpan.FromMinutes(150);

        Assert.False(FootballDataService.ShouldHaveFinished(kickoff, kickoff.AddMinutes(90), duration));
        Assert.False(FootballDataService.ShouldHaveFinished(kickoff, kickoff.AddMinutes(149), duration));
        Assert.True(FootballDataService.ShouldHaveFinished(kickoff, kickoff.AddMinutes(150), duration));
        Assert.True(FootballDataService.ShouldHaveFinished(kickoff, kickoff.AddHours(3), duration));
    }

    [Fact]
    public void TryResolveResult_MatchesByTeamIdsInGivenOrientation()
    {
        var fixture = new Fixture { HomeTeamId = "mexico", AwayTeamId = "south-africa" };
        var matches = new List<FootballDataMatch>
        {
            FinishedMatch("Mexico", "South Africa", 2, 0)
        };

        Assert.True(FootballDataService.TryResolveResult(fixture, matches, out var home, out var away));
        Assert.Equal(2, home);
        Assert.Equal(0, away);
    }

    [Fact]
    public void TryResolveResult_SwapsGoalsWhenOrientationIsReversed()
    {
        var fixture = new Fixture { HomeTeamId = "mexico", AwayTeamId = "south-africa" };
        var matches = new List<FootballDataMatch>
        {
            // football-data lists the same pair the other way around.
            FinishedMatch("South Africa", "Mexico", 0, 2)
        };

        Assert.True(FootballDataService.TryResolveResult(fixture, matches, out var home, out var away));
        Assert.Equal(2, home);
        Assert.Equal(0, away);
    }

    [Fact]
    public void TryResolveResult_IgnoresUnrelatedOrScorelessMatches()
    {
        var fixture = new Fixture { HomeTeamId = "mexico", AwayTeamId = "south-africa" };
        var matches = new List<FootballDataMatch>
        {
            FinishedMatch("Brazil", "Morocco", 1, 1),
            new() { HomeTeam = new() { Name = "Mexico" }, AwayTeam = new() { Name = "South Africa" }, Score = new() }
        };

        Assert.False(FootballDataService.TryResolveResult(fixture, matches, out _, out _));
    }

    private static FootballDataMatch FinishedMatch(string home, string away, int homeGoals, int awayGoals) => new()
    {
        Status = "FINISHED",
        HomeTeam = new FootballDataTeam { Name = home },
        AwayTeam = new FootballDataTeam { Name = away },
        Score = new FootballDataScore { FullTime = new FootballDataScoreTime { Home = homeGoals, Away = awayGoals } }
    };
}
