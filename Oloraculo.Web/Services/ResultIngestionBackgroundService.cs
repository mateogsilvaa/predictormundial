using Microsoft.Extensions.Options;

namespace Oloraculo.Web.Services
{
    /// <summary>
    /// Periodically asks <see cref="FootballDataService"/> to ingest final scores for matches that
    /// have just finished, which in turn evaluates them and recalibrates the model. This is what
    /// makes "learn from real results" automatic: no manual entry, no external cron needed.
    /// </summary>
    public class ResultIngestionBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly OloraculoConfig _config;
        private readonly ILogger<ResultIngestionBackgroundService> _logger;

        public ResultIngestionBackgroundService(
            IServiceProvider services,
            IOptions<OloraculoConfig> config,
            ILogger<ResultIngestionBackgroundService> logger)
        {
            _services = services;
            _config = config.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.ResultPollEnabled)
            {
                _logger.LogInformation("Ingesta automática de resultados deshabilitada (ResultPollEnabled=false).");
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.FootballDataApiKey))
            {
                _logger.LogInformation("Ingesta automática de resultados inactiva: falta FootballDataApiKey.");
                return;
            }

            var interval = TimeSpan.FromMinutes(Math.Max(1, _config.ResultPollIntervalMinutes));
            _logger.LogInformation("Ingesta automática de resultados activa. Intervalo: {Minutes} min.", interval.TotalMinutes);

            // Small initial delay so startup (CSV import, ranking refresh) settles first.
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await SafeWaitAsync(timer, stoppingToken));
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _services.CreateScope();
                var ingestor = scope.ServiceProvider.GetRequiredService<FootballDataService>();
                var report = await ingestor.SyncFixturesAsync(ct);

                foreach (var note in report.Notes)
                    _logger.LogInformation("Ingesta resultados: {Note}", note);
                foreach (var error in report.Errors)
                    _logger.LogWarning("Ingesta resultados: {Error}", error);

                if (report.Ingested > 0)
                    _logger.LogInformation(
                        "Ingesta resultados: {Ingested} resultado(s) nuevos, {Evaluated} evaluación(es). El modelo se recalibró.",
                        report.Ingested, report.Evaluated);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falló un ciclo de ingesta automática de resultados.");
            }
        }

        private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try
            {
                return await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
