using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;
using Oloraculo.Web.Probability;
using Oloraculo.Web.Services.Simulation;
using System.Globalization;
using System.Net;
using System.Text;

namespace Oloraculo.Web.Services
{
    /// <summary>
    /// Renders a self-contained static site (index.html + flags) from the current model state:
    /// a tabbed page with full tournament probabilities, chronological group fixtures, and the
    /// single most-likely outcome (predicted group tables + knockout bracket). The model runs
    /// server-side (in the GitHub Pages Action runner), so the page is just a snapshot of it.
    /// </summary>
    public class SiteExportService
    {
        private readonly OloraculoDbContext _db;
        private readonly FootballDataService _footballData;
        private readonly PredictionService _prediction;
        private readonly SimulationService _simulation;
        private readonly EvaluationService _evaluation;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<SiteExportService> _logger;

        public SiteExportService(
            OloraculoDbContext db,
            FootballDataService footballData,
            PredictionService prediction,
            SimulationService simulation,
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
            // Sync schedule + results and recalibrate before predicting. Best-effort.
            try
            {
                var sync = await _footballData.SyncFixturesAsync(ct);
                foreach (var note in sync.Notes)
                    _logger.LogInformation("Site export sync: {Note}", note);
                foreach (var error in sync.Errors)
                    _logger.LogWarning("Site export sync: {Error}", error);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sincronización con football-data falló durante la exportación; se continúa.");
            }

            var fixtures = await _db.Fixtures.AsNoTracking().ToListAsync(ct);
            var ordered = fixtures
                .OrderBy(f => f.Group)
                .ThenBy(f => f.KickoffUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(f => f.HomeTeamId)
                .ToList();

            var predictions = await _prediction.PredictFixturesAsync(ordered, ct);
            var projection = await _simulation.RunAsync(saveSnapshot: false, ct: ct);
            var performance = await _evaluation.PerformanceAsync(ct);
            var names = await _db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            var outcome = await BuildMostLikelyOutcomeAsync(projection, names, ct);

            var html = RenderHtml(projection, predictions, performance, outcome, names, DateTimeOffset.UtcNow);

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

        // ---- Most-likely outcome (deterministic) ----

        private async Task<MostLikelyOutcome> BuildMostLikelyOutcomeAsync(
            TournamentProjection projection,
            IReadOnlyDictionary<string, string> names,
            CancellationToken ct)
        {
            var standings = projection.Teams
                .GroupBy(t => t.Group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(t => t.ExpectedGroupPoints).ThenByDescending(t => t.WinTournament).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var bestThirdGroups = standings
                .Where(kv => kv.Value.Count >= 3)
                .Select(kv => kv.Value[2])
                .OrderByDescending(t => t.ExpectedGroupPoints).ThenByDescending(t => t.WinTournament)
                .Take(8)
                .Select(t => t.Group)
                .ToList();
            var qualifyingThirdGroups = bestThirdGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var groupViews = new List<GroupStandingTable>();
            foreach (var kv in standings.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var rows = new List<GroupStandingRow>();
                for (var i = 0; i < kv.Value.Count; i++)
                {
                    var team = kv.Value[i];
                    var qualifies = i < 2 || (i == 2 && qualifyingThirdGroups.Contains(team.Group));
                    rows.Add(new GroupStandingRow(i + 1, team.TeamId, Name(names, team.TeamId), team.ExpectedGroupPoints, qualifies));
                }
                groupViews.Add(new GroupStandingTable(kv.Key, rows));
            }

            var rounds = new List<BracketRound>();
            string? championId = null;
            try
            {
                var thirdAssignments = WorldCup2026Bracket.AssignThirdPlaceGroups(bestThirdGroups);
                var tieWinners = new Dictionary<int, string>();

                async Task<IReadOnlyList<BracketTieView>> ResolveRound(IReadOnlyList<SimulationService.BracketTie> ties)
                {
                    var views = new List<BracketTieView>();
                    foreach (var tie in ties)
                    {
                        var home = ResolveSlot(tie, tie.Home, standings, thirdAssignments, tieWinners);
                        var away = ResolveSlot(tie, tie.Away, standings, thirdAssignments, tieWinners);

                        string winner;
                        if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(away))
                        {
                            winner = string.IsNullOrEmpty(home) ? away : home;
                        }
                        else
                        {
                            var prediction = await _prediction.PredictPairAsync(home, away, ct);
                            var o = prediction.BestPrediction.Outcome;
                            winner = o.HomeWin >= o.AwayWin ? home : away;
                        }

                        tieWinners[tie.Id] = winner;
                        views.Add(new BracketTieView(home, Name(names, home), away, Name(names, away), winner, Name(names, winner)));
                    }
                    return views;
                }

                rounds.Add(new BracketRound("Dieciseisavos", await ResolveRound(WorldCup2026Bracket.RoundOf32)));
                rounds.Add(new BracketRound("Octavos", await ResolveRound(WorldCup2026Bracket.RoundOf16)));
                rounds.Add(new BracketRound("Cuartos", await ResolveRound(WorldCup2026Bracket.QuarterFinals)));
                rounds.Add(new BracketRound("Semifinales", await ResolveRound(WorldCup2026Bracket.SemiFinals)));
                rounds.Add(new BracketRound("Final", await ResolveRound([WorldCup2026Bracket.Final])));
                championId = tieWinners.GetValueOrDefault(WorldCup2026Bracket.Final.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo construir el bracket más probable; se muestran solo las clasificaciones.");
                rounds.Clear();
                championId = null;
            }

            return new MostLikelyOutcome(groupViews, rounds, championId, championId is null ? null : Name(names, championId));
        }

        private static string ResolveSlot(
            SimulationService.BracketTie tie,
            SimulationService.BracketSlot slot,
            IReadOnlyDictionary<string, List<TeamTournamentProbability>> standings,
            IReadOnlyDictionary<int, string> thirdAssignments,
            IReadOnlyDictionary<int, string> tieWinners) =>
            slot.Kind switch
            {
                BracketSlotKindEnum.GroupWinner => standings[slot.Group!][0].TeamId,
                BracketSlotKindEnum.GroupRunnerUp => standings[slot.Group!][1].TeamId,
                BracketSlotKindEnum.GroupThird => standings[thirdAssignments[tie.Id]][2].TeamId,
                BracketSlotKindEnum.WinnerOfTie => tieWinners.GetValueOrDefault(slot.TieId!.Value, ""),
                _ => ""
            };

        // ---- Rendering ----

        private static string RenderHtml(
            TournamentProjection projection,
            IReadOnlyList<MatchPredictionResult> predictions,
            IReadOnlyList<ModelPerformanceRow> performance,
            MostLikelyOutcome outcome,
            IReadOnlyDictionary<string, string> names,
            DateTimeOffset generatedAt)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>Oloráculo — Mundial 2026</title>");
            sb.Append(Styles());
            sb.Append("</head><body><div class=\"wrap\">");

            sb.Append("<div class=\"hero\"><span class=\"badge\">Modelo auto-aprendiente</span>");
            sb.Append("<h1>Oloráculo</h1><p class=\"sub\">El oráculo del Mundial 2026 que aprende de cada resultado.</p>");
            sb.Append("<p>Ensemble de modelos (Poisson Dixon-Coles, Elo, FIFA, forma y contexto) ponderado por su precisión histórica real.</p></div>");

            sb.Append("<div class=\"tabs\">");
            sb.Append("<button class=\"tab-btn active\" data-tab=\"prob\">Probabilidades</button>");
            sb.Append("<button class=\"tab-btn\" data-tab=\"partidos\">Partidos</button>");
            sb.Append("<button class=\"tab-btn\" data-tab=\"probable\">Resultado más probable</button>");
            sb.Append("</div>");

            RenderProbabilitiesTab(sb, projection, performance, names);
            RenderMatchesTab(sb, predictions);
            RenderMostLikelyTab(sb, outcome);

            sb.Append($"<p class=\"foot\">Generado {generatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC · {projection.Simulations.ToString("N0", CultureInfo.InvariantCulture)} simulaciones · Oloráculo</p>");
            sb.Append("</div>");
            sb.Append("<script>document.querySelectorAll('.tab-btn').forEach(b=>b.addEventListener('click',()=>{document.querySelectorAll('.tab-btn').forEach(x=>x.classList.remove('active'));document.querySelectorAll('.tab-panel').forEach(x=>x.classList.remove('active'));b.classList.add('active');document.getElementById(b.dataset.tab).classList.add('active');}));</script>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static void RenderProbabilitiesTab(
            StringBuilder sb,
            TournamentProjection projection,
            IReadOnlyList<ModelPerformanceRow> performance,
            IReadOnlyDictionary<string, string> names)
        {
            sb.Append("<div class=\"tab-panel active\" id=\"prob\">");

            // Champion bars (top 12).
            sb.Append("<h2>Favoritos al título</h2><div class=\"card\">");
            var top = projection.Teams.OrderByDescending(t => t.WinTournament).Take(12).ToList();
            var max = top.Count > 0 ? top.Max(t => t.WinTournament) : 0;
            foreach (var team in top)
            {
                var width = max <= 0 ? 0 : Math.Clamp(team.WinTournament / max * 100.0, 2, 100);
                sb.Append("<div class=\"crow\">");
                sb.Append(TeamCell(team.TeamId, Name(names, team.TeamId)));
                sb.Append($"<div class=\"cbar\"><div class=\"cfill\" style=\"width:{Num(width)}%\"></div></div>");
                sb.Append($"<span class=\"cpct\">{Pct(team.WinTournament, 1)}</span></div>");
            }
            sb.Append("</div>");

            // Full stage-by-stage table.
            sb.Append("<h2>Probabilidades por ronda</h2><div class=\"card scroll\"><table><thead><tr>");
            sb.Append("<th>Equipo</th><th>Gr.</th><th class=\"n\">Dieciseisavos</th><th class=\"n\">Octavos</th><th class=\"n\">Cuartos</th><th class=\"n\">Semis</th><th class=\"n\">Final</th><th class=\"n\">Campeón</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var team in projection.Teams.OrderByDescending(t => t.WinTournament).ThenByDescending(t => t.ReachFinal))
            {
                sb.Append("<tr><td>").Append(TeamCell(team.TeamId, Name(names, team.TeamId))).Append("</td>");
                sb.Append($"<td>{Escape(team.Group)}</td>");
                sb.Append($"<td class=\"n\">{Pct(team.Qualify, 0)}</td>");
                sb.Append($"<td class=\"n\">{Pct(team.ReachRoundOf16, 0)}</td>");
                sb.Append($"<td class=\"n\">{Pct(team.ReachQuarterFinal, 0)}</td>");
                sb.Append($"<td class=\"n\">{Pct(team.ReachSemiFinal, 0)}</td>");
                sb.Append($"<td class=\"n\">{Pct(team.ReachFinal, 0)}</td>");
                sb.Append($"<td class=\"n strong\">{Pct(team.WinTournament, 1)}</td></tr>");
            }
            sb.Append("</tbody></table></div>");

            if (performance.Count > 0)
            {
                sb.Append("<h2>Aprendizaje del modelo</h2><div class=\"card\"><table><thead><tr><th>Modelo</th><th class=\"n\">Acierto</th><th class=\"n\">RPS</th><th class=\"n\">n</th></tr></thead><tbody>");
                foreach (var row in performance.Take(8))
                    sb.Append($"<tr><td>{Escape(row.ModelName)}</td><td class=\"n\">{Pct(row.TopPickAccuracy, 0)}</td><td class=\"n\">{row.MeanRps.ToString("0.000", CultureInfo.InvariantCulture)}</td><td class=\"n\">{row.Count}</td></tr>");
                sb.Append("</tbody></table><p class=\"muted\">Menor RPS = mejor. Estos aciertos reponderan el ensemble.</p></div>");
            }

            sb.Append("</div>");
        }

        private static void RenderMatchesTab(StringBuilder sb, IReadOnlyList<MatchPredictionResult> predictions)
        {
            sb.Append("<div class=\"tab-panel\" id=\"partidos\">");
            sb.Append("<h2>Partidos por grupo</h2><div class=\"grid groups\">");
            foreach (var group in predictions.GroupBy(p => p.Fixture.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append($"<div class=\"card\"><h3>Grupo {Escape(group.Key)}</h3>");
                foreach (var result in group.OrderBy(r => r.Fixture.KickoffUtc ?? DateTimeOffset.MaxValue).ThenBy(r => r.HomeTeamName))
                {
                    var f = result.Fixture;
                    var p = result.BestPrediction;
                    sb.Append("<div class=\"match\"><div class=\"meta\"><span>").Append(StatusText(f)).Append("</span><span>").Append(Escape(PickText(f, p))).Append("</span></div><div class=\"mline\">");
                    sb.Append($"<span class=\"team\">{TeamInner(f.HomeTeamId, result.HomeTeamName)}</span>");
                    sb.Append($"<span class=\"score\">{ScoreText(f, p)}</span>");
                    sb.Append($"<span class=\"team away\">{TeamInner(f.AwayTeamId, result.AwayTeamName)}</span></div>");
                    var h = p.Outcome.HomeWin * 100; var d = p.Outcome.Draw * 100; var a = p.Outcome.AwayWin * 100;
                    sb.Append($"<div class=\"mbar\"><span class=\"h\" style=\"width:{Num(h)}%\"></span><span class=\"d\" style=\"width:{Num(d)}%\"></span><span class=\"a\" style=\"width:{Num(a)}%\"></span></div></div>");
                }
                sb.Append("</div>");
            }
            sb.Append("</div></div>");
        }

        private static void RenderMostLikelyTab(StringBuilder sb, MostLikelyOutcome outcome)
        {
            sb.Append("<div class=\"tab-panel\" id=\"probable\">");

            if (outcome.ChampionName is not null)
                sb.Append($"<div class=\"champion-banner\">🏆 Campeón más probable: <strong>{Escape(outcome.ChampionName)}</strong></div>");

            sb.Append("<h2>Clasificación de grupos (más probable)</h2><div class=\"grid groups\">");
            foreach (var table in outcome.Groups)
            {
                sb.Append($"<div class=\"card\"><h3>Grupo {Escape(table.Group)}</h3><table class=\"standings\"><tbody>");
                foreach (var row in table.Rows)
                {
                    var cls = row.Qualifies ? " class=\"qualifies\"" : "";
                    sb.Append($"<tr{cls}><td class=\"pos\">{row.Position}</td><td>{TeamInner(row.TeamId, row.Name)}</td><td class=\"n\">{row.Points.ToString("0.#", CultureInfo.InvariantCulture)} pts</td></tr>");
                }
                sb.Append("</tbody></table></div>");
            }
            sb.Append("</div>");

            if (outcome.Rounds.Count > 0)
            {
                sb.Append("<h2>Cuadro más probable</h2><div class=\"bracket scroll\">");
                foreach (var round in outcome.Rounds)
                {
                    sb.Append($"<div class=\"round\"><div class=\"round-title\">{Escape(round.Title)}</div>");
                    foreach (var tie in round.Ties)
                    {
                        sb.Append("<div class=\"tie\">");
                        sb.Append($"<div class=\"slot{(tie.WinnerId == tie.HomeId ? " win" : "")}\">{TeamInner(tie.HomeId, tie.HomeName)}</div>");
                        sb.Append($"<div class=\"slot{(tie.WinnerId == tie.AwayId ? " win" : "")}\">{TeamInner(tie.AwayId, tie.AwayName)}</div>");
                        sb.Append("</div>");
                    }
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }

            sb.Append("</div>");
        }

        // ---- Helpers ----

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

        // Uses the scoreline that is consistent with the predicted outcome (set by the ensemble),
        // not the rounded expected goals — otherwise a "home win" pick could show a 1-1 draw.
        private static string ScoreText(Fixture fixture, MatchPrediction prediction)
        {
            if (fixture.IsPlayed && fixture.HomeGoals.HasValue && fixture.AwayGoals.HasValue)
                return $"{fixture.HomeGoals}-{fixture.AwayGoals}";
            if (prediction.MostLikelyScore is { } score)
                return $"{score.Home}-{score.Away}";
            if (prediction.ExpectedHomeGoals.HasValue && prediction.ExpectedAwayGoals.HasValue)
                return $"{Math.Round(prediction.ExpectedHomeGoals.Value)}-{Math.Round(prediction.ExpectedAwayGoals.Value)}";
            return "—";
        }

        private static string StatusText(Fixture fixture)
        {
            if (fixture.IsPlayed)
                return "Jugado";
            if (fixture.KickoffUtc is { } k)
                return k.UtcDateTime.ToString("dd MMM HH:mm 'UTC'", CultureInfo.InvariantCulture);
            return "Por confirmar";
        }

        private static string TeamCell(string teamId, string name) => $"<span class=\"team\">{TeamInner(teamId, name)}</span>";

        private static string TeamInner(string teamId, string name)
        {
            if (string.IsNullOrEmpty(teamId))
                return "<span>—</span>";
            var flag = TeamFlagCatalog.CodeFor(teamId, name);
            var img = string.IsNullOrWhiteSpace(flag) ? "" : $"<img src=\"flags/4x3/{flag}.svg\" alt=\"\" loading=\"lazy\">";
            return $"{img}<span>{Escape(name)}</span>";
        }

        private static string Name(IReadOnlyDictionary<string, string> names, string id) =>
            string.IsNullOrEmpty(id) ? "" : names.TryGetValue(id, out var name) ? name : id;

        private static string Pct(double value, int digits) => value.ToString($"P{digits}", CultureInfo.InvariantCulture);
        private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");

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

        private static string Styles() => """
            <style>
            :root{--bg:#0e1525;--panel:#151d31;--ink:#eef2f8;--muted:#9aa6bd;--line:#26314a;--accent:#18a07f;--accent2:#3b6fd0;--home:#18a07f;--draw:#c98926;--away:#3b6fd0;--gold:#e3b341}
            *{box-sizing:border-box}body{margin:0;font-family:Inter,ui-sans-serif,system-ui,"Segoe UI",sans-serif;background:var(--bg);color:var(--ink);line-height:1.5}
            .wrap{max-width:1100px;margin:0 auto;padding:1.5rem 1.1rem 3rem}
            .hero{border-radius:16px;padding:1.8rem 1.6rem;background:radial-gradient(900px 320px at 88% -30%,rgba(201,137,38,.32),transparent 60%),linear-gradient(135deg,#172033,#126a5a 75%,#18a07f);box-shadow:0 18px 50px -28px rgba(24,160,127,.7)}
            .badge{display:inline-block;background:rgba(255,255,255,.14);padding:.25rem .7rem;border-radius:999px;font-size:.78rem;font-weight:700}
            h1{margin:.4rem 0 .1rem;font-size:2rem;letter-spacing:-.5px}h2{margin:1.6rem 0 .7rem;font-size:1.2rem}h3{margin:.1rem 0 .6rem;font-size:1rem}
            .sub{opacity:.9;font-weight:600;margin:0 0 .4rem}.hero p{opacity:.82;max-width:680px;margin:.3rem 0 0}
            .tabs{display:flex;gap:.4rem;margin:1.4rem 0 .4rem;flex-wrap:wrap;position:sticky;top:0;background:var(--bg);padding:.5rem 0;z-index:5}
            .tab-btn{background:var(--panel);color:var(--muted);border:1px solid var(--line);border-radius:999px;padding:.45rem 1rem;font-weight:700;cursor:pointer;font-size:.92rem}
            .tab-btn.active{background:var(--accent);color:#06281f;border-color:var(--accent)}
            .tab-panel{display:none}.tab-panel.active{display:block}
            .card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:1.1rem;margin-bottom:1rem}
            .scroll{overflow-x:auto}
            .grid{display:grid;gap:1rem}@media(min-width:760px){.groups{grid-template-columns:1fr 1fr}}@media(min-width:1040px){.groups{grid-template-columns:1fr 1fr 1fr}}
            .crow{display:grid;grid-template-columns:minmax(110px,1fr) minmax(0,2.4fr) 3.4rem;align-items:center;gap:.7rem;margin:.4rem 0}
            .cbar{height:11px;background:#26314a;border-radius:999px;overflow:hidden}.cfill{height:100%;background:linear-gradient(90deg,var(--accent),var(--accent2));border-radius:999px}
            .cpct{text-align:right;font-weight:800;font-variant-numeric:tabular-nums}
            .team{display:inline-flex;align-items:center;gap:.4rem;min-width:0}.team img{width:1.25rem;height:.92rem;object-fit:cover;border-radius:2px;border:1px solid rgba(255,255,255,.18);background:#fff;flex:0 0 auto}
            .team span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            table{width:100%;border-collapse:collapse;font-size:.88rem}th,td{padding:.4rem .5rem;border-bottom:1px solid var(--line);text-align:left;white-space:nowrap}
            th.n,td.n{text-align:right;font-variant-numeric:tabular-nums}.strong{font-weight:800}.muted{color:var(--muted)}
            .match{padding:.5rem .1rem;border-bottom:1px solid var(--line)}.match:last-child{border-bottom:0}
            .mline{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;gap:.5rem}.mline .away{justify-content:flex-end;text-align:right}
            .score{background:#26314a;border-radius:6px;padding:.15rem .55rem;font-weight:800;font-variant-numeric:tabular-nums}
            .mbar{display:flex;height:6px;border-radius:999px;overflow:hidden;background:#26314a;margin-top:.4rem}.mbar .h{background:var(--home)}.mbar .d{background:var(--draw)}.mbar .a{background:var(--away)}
            .meta{display:flex;justify-content:space-between;color:var(--muted);font-size:.76rem;margin-bottom:.25rem}
            .standings td{border-bottom:1px solid var(--line)}.standings .pos{color:var(--muted);width:1.4rem;text-align:center}
            .standings tr.qualifies td{background:rgba(24,160,127,.10)}.standings tr.qualifies .pos{color:var(--accent);font-weight:800}
            .champion-banner{background:linear-gradient(135deg,rgba(227,179,65,.18),rgba(227,179,65,.05));border:1px solid rgba(227,179,65,.4);color:var(--gold);border-radius:12px;padding:.9rem 1.1rem;font-size:1.05rem;margin-bottom:1rem}
            .bracket{display:flex;gap:1rem;align-items:flex-start;padding-bottom:.6rem}
            .round{min-width:170px;flex:0 0 auto}.round-title{font-weight:800;color:var(--muted);font-size:.82rem;text-transform:uppercase;letter-spacing:.5px;margin-bottom:.5rem}
            .tie{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:.35rem;margin-bottom:.55rem;display:grid;gap:.25rem}
            .slot{padding:.2rem .35rem;border-radius:5px;font-size:.84rem;opacity:.6}.slot.win{opacity:1;background:rgba(24,160,127,.14);font-weight:700}
            .foot{margin-top:2rem;color:var(--muted);font-size:.82rem;text-align:center}
            </style>
            """;

        // ---- View models ----

        private sealed record MostLikelyOutcome(
            IReadOnlyList<GroupStandingTable> Groups,
            IReadOnlyList<BracketRound> Rounds,
            string? ChampionId,
            string? ChampionName);

        private sealed record GroupStandingTable(string Group, IReadOnlyList<GroupStandingRow> Rows);

        private sealed record GroupStandingRow(int Position, string TeamId, string Name, double Points, bool Qualifies);

        private sealed record BracketRound(string Title, IReadOnlyList<BracketTieView> Ties);

        private sealed record BracketTieView(string HomeId, string HomeName, string AwayId, string AwayName, string WinnerId, string WinnerName);
    }
}
