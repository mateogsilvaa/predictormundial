using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Models.FootballDataModels;

namespace Oloraculo.Web.Services
{
    /// <summary>
    /// Pulls real final scores from football-data.org and feeds them into the evaluation/learning
    /// loop automatically. A fixture is only queried once enough time has passed since kickoff for
    /// the match to have finished (see <see cref="OloraculoConfig.ResultPollMatchDurationMinutes"/>),
    /// so we never poll for a result that cannot exist yet.
    /// </summary>
    public class FootballDataService
    {
        private readonly HttpClient _http;
        private readonly OloraculoDbContext _db;
        private readonly OloraculoConfig _config;
        private readonly EvaluationService _evaluation;
        private readonly ILogger<FootballDataService> _logger;

        public FootballDataService(
            HttpClient http,
            OloraculoDbContext db,
            IOptions<OloraculoConfig> config,
            EvaluationService evaluation,
            ILogger<FootballDataService> logger)
        {
            _http = http;
            _db = db;
            _config = config.Value;
            _evaluation = evaluation;
            _logger = logger;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.FootballDataApiKey);

        /// <summary>
        /// Ingests final scores for every fixture that should already have finished, then evaluates
        /// the freshly played fixtures so the model's reliability weights recalibrate.
        /// </summary>
        public async Task<FootballDataIngestReport> IngestDueResultsAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
                return FootballDataIngestReport.NotConfigured();

            var notes = new List<string>();
            var errors = new List<string>();
            var now = DateTimeOffset.UtcNow;
            var assumedDuration = TimeSpan.FromMinutes(Math.Max(1, _config.ResultPollMatchDurationMinutes));

            var dueFixtures = (await _db.Fixtures
                .Where(f => !f.IsPlayed && f.KickoffUtc != null)
                .ToListAsync(ct))
                .Where(f => ShouldHaveFinished(f.KickoffUtc!.Value, now, assumedDuration))
                .ToList();

            if (dueFixtures.Count == 0)
                return new FootballDataIngestReport(true, 0, 0, 0, ["No hay partidos que ya debieran haber terminado."], errors);

            FootballDataMatchesResponse? response;
            try
            {
                response = await _http.GetFromJsonAsync<FootballDataMatchesResponse>(
                    $"competitions/{_config.FootballDataCompetition}/matches?status=FINISHED", ct);
            }
            catch (Exception ex)
            {
                errors.Add($"No se pudieron leer resultados de football-data.org: {ex.Message}");
                return new FootballDataIngestReport(true, dueFixtures.Count, 0, 0, notes, errors);
            }

            var finished = response?.Matches ?? [];
            var ingested = 0;
            foreach (var fixture in dueFixtures)
            {
                if (!TryResolveResult(fixture, finished, out var homeGoals, out var awayGoals))
                    continue;

                fixture.IsPlayed = true;
                fixture.HomeGoals = homeGoals;
                fixture.AwayGoals = awayGoals;
                fixture.Status = "FT";
                fixture.Source = "football-data.org";
                ingested++;
                notes.Add($"{fixture.HomeTeamId} {homeGoals}-{awayGoals} {fixture.AwayTeamId} ingerido automáticamente.");
            }

            if (ingested > 0)
                await _db.SaveChangesAsync(ct);

            // Score the newly played fixtures and recalibrate the reliability weights.
            var evaluation = await _evaluation.EvaluateUnevaluatedPlayedFixturesAsync(ct);
            notes.Add($"Partidos pendientes que debieron terminar: {dueFixtures.Count}. Resultados ingeridos: {ingested}. Evaluados: {evaluation.Evaluated}.");

            return new FootballDataIngestReport(true, dueFixtures.Count, ingested, evaluation.Evaluated, notes, errors);
        }

        /// <summary>True once <paramref name="now"/> is past kickoff plus the assumed match duration.</summary>
        public static bool ShouldHaveFinished(DateTimeOffset kickoff, DateTimeOffset now, TimeSpan assumedDuration) =>
            now >= kickoff + assumedDuration;

        /// <summary>
        /// Finds the finished football-data match for a fixture (matched by normalized team ids,
        /// either orientation) and returns the goals oriented to our home/away convention.
        /// </summary>
        public static bool TryResolveResult(
            Fixture fixture,
            IReadOnlyList<FootballDataMatch> finishedMatches,
            out int homeGoals,
            out int awayGoals)
        {
            homeGoals = 0;
            awayGoals = 0;

            foreach (var match in finishedMatches)
            {
                if (match.Score.FullTime.Home is not { } scoreHome || match.Score.FullTime.Away is not { } scoreAway)
                    continue;

                var matchHomeId = TeamNameNormalizer.ToId(match.HomeTeam.Name ?? "");
                var matchAwayId = TeamNameNormalizer.ToId(match.AwayTeam.Name ?? "");

                if (matchHomeId == fixture.HomeTeamId && matchAwayId == fixture.AwayTeamId)
                {
                    homeGoals = scoreHome;
                    awayGoals = scoreAway;
                    return true;
                }

                // Same pair, opposite orientation: swap the goals to our convention.
                if (matchHomeId == fixture.AwayTeamId && matchAwayId == fixture.HomeTeamId)
                {
                    homeGoals = scoreAway;
                    awayGoals = scoreHome;
                    return true;
                }
            }

            return false;
        }
    }
}
