using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using System.Globalization;
using System.Net;
using System.Text;

namespace Oloraculo.Web.Services
{
    /// <summary>
    /// Renders a self-contained static site (index.html + flags) from the current model state.
    /// Used by the GitHub Pages workflow: the model runs server-side in the Action runner, ingests
    /// real results, recalibrates, and this writes the page that gets published to Pages.
    /// </summary>
    public class SiteExportService
    {
        private readonly OloraculoDbContext _db;
        private readonly FootballDataService _footballData;
        private readonly PredictionService _prediction;
        private readonly Simulation.SimulationService _simulation;
        private readonly EvaluationService _evaluation;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<SiteExportService> _logger;

        public SiteExportService(
            OloraculoDbContext db,
            FootballDataService footballData,
            PredictionService prediction,
            Simulation.SimulationService simulation,
            EvaluationService evaluation,
            IWebHostEnvironment environment,
            ILogger<SiteExportService> logger)
        {
            _db = db;
            _footballData = footballData;
            _prediction = prediction;
            _simulation = simulation;
            _evaluation = evaluation;
            _environment = environment;
            _logger = logger;
        }

        public async Task ExportAsync(string outputDirectory, CancellationToken ct = default)
        {
            // Pull any finished results and recalibrate before predicting, so the page reflects the
            // latest learning. Best-effort: a missing API key or network blip must not break the build.
            try
            {
                var ingest = await _footballData.IngestDueResultsAsync(ct);
                foreach (var note in ingest.Notes)
                    _logger.LogInformation("Site export ingest: {Note}", note);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ingesta de resultados falló durante la exportación del sitio; se continúa.");
            }

            var fixtures = await _db.Fixtures.AsNoTracking().ToListAsync(ct);
            var ordered = fixtures
                .OrderBy(f => f.KickoffUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(f => f.Group)
                .ThenBy(f => f.HomeTeamId)
                .ToList();

            var predictions = await _prediction.PredictFixturesAsync(ordered, ct);
            var projection = await _simulation.RunAsync(saveSnapshot: false, ct: ct);
            var performance = await _evaluation.PerformanceAsync(ct);
            var names = await _db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);

            var html = RenderHtml(projection, predictions, performance, names, DateTimeOffset.UtcNow);

            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "index.html"),
                html,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ct);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, ".nojekyll"), "", ct);

            CopyFlags(outputDirectory);
            _logger.LogInformation("Sitio estático generado en {Output} ({Fixtures} partidos).", outputDirectory, ordered.Count);
        }

        private void CopyFlags(string outputDirectory)
        {
            var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(_environment.ContentRootPath, "wwwroot")
                : _environment.WebRootPath;
            var source = Path.Combine(webRoot, "flags");
            if (!Directory.Exists(source))
            {
                _logger.LogWarning("No se encontró la carpeta de banderas en {Source}.", source);
                return;
            }

            var target = Path.Combine(outputDirectory, "flags");
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, target));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, target), overwrite: true);
        }

        private static string RenderHtml(
            TournamentProjection projection,
            IReadOnlyList<MatchPredictionResult> predictions,
            IReadOnlyList<ModelPerformanceRow> performance,
            IReadOnlyDictionary<string, string> names,
            DateTimeOffset generatedAt)
        {
            var sb = new StringBuilder();
            sb.Append("""
                <!doctype html><html lang="es"><head><meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Oloráculo — Mundial 2026</title>
                <style>
                :root{--bg:#0e1525;--panel:#151d31;--ink:#eef2f8;--muted:#9aa6bd;--line:#26314a;--accent:#18a07f;--accent2:#3b6fd0;--home:#18a07f;--draw:#c98926;--away:#3b6fd0}
                *{box-sizing:border-box}body{margin:0;font-family:Inter,ui-sans-serif,system-ui,"Segoe UI",sans-serif;background:var(--bg);color:var(--ink);line-height:1.5}
                .wrap{max-width:1040px;margin:0 auto;padding:1.5rem 1.1rem 3rem}
                .hero{border-radius:16px;padding:2rem 1.6rem;background:radial-gradient(900px 320px at 88% -30%,rgba(201,137,38,.32),transparent 60%),linear-gradient(135deg,#172033,#126a5a 75%,#18a07f);box-shadow:0 18px 50px -28px rgba(24,160,127,.7)}
                .badge{display:inline-block;background:rgba(255,255,255,.14);padding:.25rem .7rem;border-radius:999px;font-size:.78rem;font-weight:700}
                h1{margin:.5rem 0 .15rem;font-size:2.2rem;letter-spacing:-.5px}
                .sub{opacity:.9;font-weight:600;margin:0 0 .5rem}.hero p{opacity:.82;max-width:680px;margin:.4rem 0 0}
                h2{margin:2rem 0 .8rem;font-size:1.25rem}
                .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:1.1rem}
                .grid{display:grid;gap:1rem}@media(min-width:780px){.split{grid-template-columns:1.3fr 1fr}.groups{grid-template-columns:1fr 1fr}}
                .crow{display:grid;grid-template-columns:minmax(120px,1fr) minmax(0,2.4fr) 3.6rem;align-items:center;gap:.7rem;margin:.45rem 0}
                .cbar{height:12px;background:#26314a;border-radius:999px;overflow:hidden}.cfill{height:100%;background:linear-gradient(90deg,var(--accent),var(--accent2));border-radius:999px}
                .cpct{text-align:right;font-weight:800;font-variant-numeric:tabular-nums}
                .team{display:inline-flex;align-items:center;gap:.45rem;min-width:0}.team img{width:1.3rem;height:.95rem;object-fit:cover;border-radius:2px;border:1px solid rgba(255,255,255,.18);background:#fff}
                .team span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
                table{width:100%;border-collapse:collapse;font-size:.9rem}th,td{padding:.4rem .5rem;border-bottom:1px solid var(--line);text-align:left}
                th.n,td.n{text-align:right;font-variant-numeric:tabular-nums}
                .match{padding:.55rem .2rem;border-bottom:1px solid var(--line)}
                .mline{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;gap:.5rem}
                .mline .away{justify-content:flex-end;text-align:right}.score{background:#26314a;border-radius:6px;padding:.15rem .55rem;font-weight:800;font-variant-numeric:tabular-nums}
                .mbar{display:flex;height:7px;border-radius:999px;overflow:hidden;background:#26314a;margin-top:.4rem}.mbar .h{background:var(--home)}.mbar .d{background:var(--draw)}.mbar .a{background:var(--away)}
                .meta{display:flex;justify-content:space-between;color:var(--muted);font-size:.76rem;margin-bottom:.25rem}
                .muted{color:var(--muted)}.foot{margin-top:2rem;color:var(--muted);font-size:.82rem;text-align:center}
                a{color:var(--accent)}
                </style></head><body><div class="wrap">
                """);

            sb.Append("<div class=\"hero\"><span class=\"badge\">Modelo auto-aprendiente</span>");
            sb.Append("<h1>Oloráculo</h1><p class=\"sub\">El oráculo del Mundial 2026 que aprende de cada resultado.</p>");
            sb.Append("<p>Ensemble de modelos (Poisson Dixon-Coles, Elo, FIFA, forma y contexto) ponderado por su precisión histórica real. Cuando termina un partido, el resultado se ingiere solo, se evalúa y los pesos se recalibran.</p></div>");

            // Champion contenders
            sb.Append("<h2>Favoritos al título</h2><div class=\"card\">");
            var top = projection.Teams.OrderByDescending(t => t.WinTournament).Take(16).ToList();
            var max = top.Count > 0 ? top.Max(t => t.WinTournament) : 0;
            foreach (var team in top)
            {
                var width = max <= 0 ? 0 : Math.Clamp(team.WinTournament / max * 100.0, 2, 100);
                sb.Append("<div class=\"crow\">");
                sb.Append(TeamCell(team.TeamId, Name(names, team.TeamId)));
                sb.Append($"<div class=\"cbar\"><div class=\"cfill\" style=\"width:{width.ToString("0.##", CultureInfo.InvariantCulture)}%\"></div></div>");
                sb.Append($"<span class=\"cpct\">{Pct(team.WinTournament, 1)}</span></div>");
            }
            sb.Append($"<p class=\"muted\">{projection.Simulations.ToString("N0", CultureInfo.InvariantCulture)} simulaciones Monte Carlo.</p></div>");

            // Model performance
            if (performance.Count > 0)
            {
                sb.Append("<h2>Aprendizaje del modelo</h2><div class=\"card\"><table><thead><tr><th>Modelo</th><th class=\"n\">Acierto</th><th class=\"n\">RPS</th><th class=\"n\">n</th></tr></thead><tbody>");
                foreach (var row in performance.Take(8))
                    sb.Append($"<tr><td>{Escape(row.ModelName)}</td><td class=\"n\">{Pct(row.TopPickAccuracy, 0)}</td><td class=\"n\">{row.MeanRps.ToString("0.000", CultureInfo.InvariantCulture)}</td><td class=\"n\">{row.Count}</td></tr>");
                sb.Append("</tbody></table><p class=\"muted\">Menor RPS = mejor. Estos aciertos reponderan el ensemble.</p></div>");
            }

            // Group picks
            sb.Append("<h2>Predicciones por grupo</h2><div class=\"grid groups\">");
            foreach (var group in predictions.GroupBy(p => p.Fixture.Group).OrderBy(g => g.Key))
            {
                sb.Append($"<div class=\"card\"><h3 style=\"margin:.1rem 0 .6rem\">Grupo {Escape(group.Key)}</h3>");
                foreach (var result in group.OrderBy(r => r.Fixture.KickoffUtc ?? DateTimeOffset.MaxValue))
                {
                    var f = result.Fixture;
                    var p = result.BestPrediction;
                    sb.Append("<div class=\"match\"><div class=\"meta\"><span>");
                    sb.Append(StatusText(f));
                    sb.Append("</span><span>");
                    sb.Append(Escape(PickText(f, p)));
                    sb.Append("</span></div><div class=\"mline\">");
                    sb.Append($"<span class=\"team\">{TeamInner(f.HomeTeamId, result.HomeTeamName)}</span>");
                    sb.Append($"<span class=\"score\">{ScoreText(f, p)}</span>");
                    sb.Append($"<span class=\"team away\">{TeamInner(f.AwayTeamId, result.AwayTeamName)}</span></div>");
                    var h = p.Outcome.HomeWin * 100; var d = p.Outcome.Draw * 100; var a = p.Outcome.AwayWin * 100;
                    sb.Append($"<div class=\"mbar\"><span class=\"h\" style=\"width:{h.ToString("0.#", CultureInfo.InvariantCulture)}%\"></span><span class=\"d\" style=\"width:{d.ToString("0.#", CultureInfo.InvariantCulture)}%\"></span><span class=\"a\" style=\"width:{a.ToString("0.#", CultureInfo.InvariantCulture)}%\"></span></div>");
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div>");

            sb.Append($"<p class=\"foot\">Generado {generatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC · Oloráculo</p>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static string PickText(Fixture fixture, MatchPrediction prediction)
        {
            if (fixture.IsPlayed)
                return "Final";
            return prediction.Outcome.TopPick switch
            {
                "Home" => "Pick: local",
                "Away" => "Pick: visitante",
                "Draw" => "Pick: empate",
                _ => ""
            };
        }

        private static string ScoreText(Fixture fixture, MatchPrediction prediction)
        {
            if (fixture.IsPlayed && fixture.HomeGoals.HasValue && fixture.AwayGoals.HasValue)
                return $"{fixture.HomeGoals}-{fixture.AwayGoals}";
            if (prediction.ExpectedHomeGoals.HasValue && prediction.ExpectedAwayGoals.HasValue)
                return $"{Math.Round(prediction.ExpectedHomeGoals.Value)}-{Math.Round(prediction.ExpectedAwayGoals.Value)}";
            if (prediction.MostLikelyScore is { } s)
                return $"{s.Home}-{s.Away}";
            return "—";
        }

        private static string StatusText(Fixture fixture)
        {
            if (fixture.IsPlayed)
                return "Jugado";
            if (fixture.KickoffUtc is { } k)
                return k.UtcDateTime.ToString("dd MMM HH:mm 'UTC'", CultureInfo.InvariantCulture);
            return "Programado";
        }

        private static string TeamCell(string teamId, string name) => $"<span class=\"team\">{TeamInner(teamId, name)}</span>";

        private static string TeamInner(string teamId, string name)
        {
            var flag = TeamFlagCatalog.CodeFor(teamId, name);
            var img = string.IsNullOrWhiteSpace(flag) ? "" : $"<img src=\"flags/4x3/{flag}.svg\" alt=\"\" loading=\"lazy\">";
            return $"{img}<span>{Escape(name)}</span>";
        }

        private static string Name(IReadOnlyDictionary<string, string> names, string id) =>
            names.TryGetValue(id, out var name) ? name : id;

        private static string Pct(double value, int digits) =>
            value.ToString($"P{digits}", CultureInfo.InvariantCulture);

        private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");
    }
}
