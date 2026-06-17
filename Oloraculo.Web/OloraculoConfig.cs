namespace Oloraculo.Web
{
    public class OloraculoConfig
    {
        public int SimulationCount { get; set; }
        public int? SimulationSeed { get; set; }
        public int RecentResultCount { get; set; }
        public int GoalModelYearsWindow { get; set; }
        public string ApiFootballBaseUrl { get; set; } = "https://v3.football.api-sports.io/";
        public string? ApiFootballApiKey { get; set; }
        public int ApiFootballLeagueId { get; set; }
        public int ApiFootballSeason { get; set; }

        // football-data.org is used to pull real final scores automatically once a match should
        // have ended, feeding the evaluation/learning loop without any manual input.
        public string FootballDataBaseUrl { get; set; } = "https://api.football-data.org/v4/";
        public string? FootballDataApiKey { get; set; }
        public string FootballDataCompetition { get; set; } = "WC";
        // How long after kickoff a match is assumed finished (90' + halftime + stoppage + safety
        // margin). The auto-ingest only queries a fixture's result once this much time has passed.
        public int ResultPollMatchDurationMinutes { get; set; } = 150;
        // How often the background ingester wakes up to check for matches that have just finished.
        public int ResultPollIntervalMinutes { get; set; } = 15;
        public bool ResultPollEnabled { get; set; } = true;
        public bool RankingRefreshOnStartup { get; set; } = true;
        public int EloRefreshMaxLookbackDays { get; set; } = 14;
        public string FifaRankingsRawUrl { get; set; } = "https://en.wikipedia.org/w/index.php?title=Module:SportsRankings/data/FIFA_World_Rankings&action=raw";
        public string EloRankingsBaseUrl { get; set; } = "https://www.international-football.net/elo-ratings-table";
        public string RankingRefreshUserAgent { get; set; } = "Oloraculo";
        public string GoalscorersRawUrl { get; set; } = "https://raw.githubusercontent.com/martj42/international_results/refs/heads/master/goalscorers.csv";
        public int GoalscorerLookbackYears { get; set; } = 6;
        public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1/";
        public string? OpenRouterApiKey { get; set; }
        public string OpenRouterModel { get; set; } = "openai/gpt-4o-mini";
        public string[] AvailabilitySourceUrls { get; set; } =
        [
            "https://www.espn.com/soccer/story/_/id/48572979/2026-fifa-world-cup-injuries-tracker-which-stars-miss-latest-info",
            "https://talksport.com/football/world-cup/4311921/world-cup-2026-injury-tracker-full-squads-messi/"
        ];
        public string AvailabilityRefreshUserAgent { get; set; } = "Oloraculo";
        public int AvailabilityMaxArticleChars { get; set; } = 24000;
        public bool AvailabilityRequireCrossCheck { get; set; } = true;
    }

    public static class OloraculoDataFiles
    {
        public const string GroupsCsv = "wc2026_groups.csv";
        public const string EloCsv = "elo_snapshot.csv";
        public const string FifaRankingsCsv = "fifa_rankings.csv";
        public const string HistoricalResultsCsv = "historical_results.csv";
        public const string GoalscorersCsv = "goalscorers.csv";
    }
}
