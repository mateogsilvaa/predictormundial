namespace Oloraculo.Web.Models.FootballDataModels
{
    // Minimal DTOs for the football-data.org v4 "matches" endpoint. JSON is camelCase; the
    // System.Text.Json web defaults used by HttpClient map it case-insensitively to these.

    public sealed class FootballDataMatchesResponse
    {
        public List<FootballDataMatch> Matches { get; set; } = new();
    }

    public sealed class FootballDataMatch
    {
        public long Id { get; set; }
        public DateTimeOffset UtcDate { get; set; }
        public string? Status { get; set; }
        public FootballDataTeam HomeTeam { get; set; } = new();
        public FootballDataTeam AwayTeam { get; set; } = new();
        public FootballDataScore Score { get; set; } = new();
    }

    public sealed class FootballDataTeam
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? ShortName { get; set; }
        public string? Tla { get; set; }
    }

    public sealed class FootballDataScore
    {
        public string? Winner { get; set; }
        public FootballDataScoreTime FullTime { get; set; } = new();
    }

    public sealed class FootballDataScoreTime
    {
        public int? Home { get; set; }
        public int? Away { get; set; }
    }

    public sealed record FootballDataIngestReport(
        bool IsConfigured,
        int DueFixtures,
        int Ingested,
        int Evaluated,
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Errors)
    {
        public static FootballDataIngestReport NotConfigured() =>
            new(false, 0, 0, 0, ["La clave de football-data.org no está configurada."], []);
    }
}
