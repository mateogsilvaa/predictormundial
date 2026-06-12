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

public class RankingRefreshServiceTests : TestFixtures
{
    [Fact]
    public void RankingRefresh_ParsesFifaLuaRows()
    {
        var rows = RankingRefreshService.ParseFifaRankings(SampleFifaRaw());

        Assert.Equal(2, rows.Count);
        Assert.Equal("France", rows[0].Team);
        Assert.Equal("1877.32", rows[0].Points);
        Assert.Equal("2026-04-01", rows[0].RankingDate);
    }

    [Fact]
    public void RankingRefresh_ParsesEloHtmlRowsAndCleansImageText()
    {
        var date = new DateOnly(2026, 6, 5);
        var rows = RankingRefreshService.ParseEloRankings(SampleEloHtml(), date, "https://example.test/elo");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Spain", rows[0].Team);
        Assert.Equal("2155", rows[0].Elo);
        Assert.Equal("2026-06-05", rows[0].RatingDate);
    }

    [Fact]
    public async Task RankingRefresh_WalksBackToLatestAvailableEloDateAndWritesParseableCsvs()
    {
        var root = NewTempRoot();
        try
        {
            var options = Options.Create(new OloraculoConfig
            {
                FifaRankingsRawUrl = "https://example.test/fifa",
                EloRankingsBaseUrl = "https://example.test/elo",
                EloRefreshMaxLookbackDays = 3
            });
            var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://example.test/fifa"] = SampleFifaRaw(),
                ["https://example.test/elo?day=09&month=06&year=2026"] = "no rankings today",
                ["https://example.test/elo?day=08&month=06&year=2026"] = "still no rankings",
                ["https://example.test/elo?day=07&month=06&year=2026"] = SampleEloHtml()
            });
            var service = new RankingRefreshService(new HttpClient(handler), new TestEnvironment(root), options);

            var report = await service.RefreshAsync(new DateOnly(2026, 6, 9));

            Assert.True(report.AnyFileUpdated);
            Assert.Equal(new DateOnly(2026, 6, 7), report.EloRatingDate);
            Assert.Equal(2, CsvParsingHelper.ReadCsv<FifaCsvRow>(Path.Combine(root, "Data", OloraculoDataFiles.FifaRankingsCsv)).Count);
            Assert.Equal(2, CsvParsingHelper.ReadCsv<EloCsvRow>(Path.Combine(root, "Data", OloraculoDataFiles.EloCsv)).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RankingRefresh_DoesNotOverwriteExistingCsvsWhenSourcesCannotParse()
    {
        var root = NewTempRoot();
        try
        {
            var data = Path.Combine(root, "Data");
            Directory.CreateDirectory(data);
            var fifaPath = Path.Combine(data, OloraculoDataFiles.FifaRankingsCsv);
            var eloPath = Path.Combine(data, OloraculoDataFiles.EloCsv);
            await File.WriteAllTextAsync(fifaPath, "existing fifa");
            await File.WriteAllTextAsync(eloPath, "existing elo");

            var options = Options.Create(new OloraculoConfig
            {
                FifaRankingsRawUrl = "https://example.test/fifa",
                EloRankingsBaseUrl = "https://example.test/elo",
                EloRefreshMaxLookbackDays = 0
            });
            var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
            {
                ["https://example.test/fifa"] = "not lua",
                ["https://example.test/elo?day=09&month=06&year=2026"] = "not elo"
            });
            var service = new RankingRefreshService(new HttpClient(handler), new TestEnvironment(root), options);

            var report = await service.RefreshAsync(new DateOnly(2026, 6, 9));

            Assert.False(report.AnyFileUpdated);
            Assert.Equal("existing fifa", await File.ReadAllTextAsync(fifaPath));
            Assert.Equal("existing elo", await File.ReadAllTextAsync(eloPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SampleFifaRaw() =>
        """
        local data = {}
        data.updated  = { day = 1, month = 'April', year =2026 }
        data.rankings = {
            { "France", 1, 2, 1877.32 },
            { "Spain", 2, -1, 1876.40 },
        }
        """;

    private static string SampleEloHtml() =>
        """
        <html><body>
        <h2>World football Elo ratings as on June 5th, 2026</h2>
        <p>1 . Image: Spain Spain 2155 2 . Argentina 2113</p>
        <p>About International-football.net</p>
        </body></html>
        """;

}
