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

public class TeamNameAndFlagTests : TestFixtures
{
    [Theory]
    [InlineData("Korea Republic", "south-korea")]
    [InlineData("Türkiye", "turkey")]
    [InlineData("USA", "united-states")]
    public void TeamNameNormalizer_HandlesAliases(string input, string expected)
    {
        Assert.Equal(expected, TeamNameNormalizer.ToId(input));
    }

    [Theory]
    [InlineData("argentina", "Argentina", "ar")]
    [InlineData("brazil", "Brazil", "br")]
    [InlineData("france", "France", "fr")]
    [InlineData("japan", "Japan", "jp")]
    [InlineData("united-states", "United States", "us")]
    [InlineData("south-korea", "South Korea", "kr")]
    [InlineData("turkey", "Turkey", "tr")]
    [InlineData("ivory-coast", "Ivory Coast", "ci")]
    [InlineData("congo-dr", "Congo DR", "cd")]
    [InlineData("curacao", "Curacao", "cw")]
    [InlineData("cape-verde", "Cape Verde", "cv")]
    [InlineData("england", "England", "gb-eng")]
    [InlineData("scotland", "Scotland", "gb-sct")]
    [InlineData("wales", "Wales", "gb-wls")]
    [InlineData("northern-ireland", "Northern Ireland", "gb-nir")]
    [InlineData("china-pr", "China PR", "cn")]
    [InlineData("chinese-taipei", "Chinese Taipei", "tw")]
    [InlineData("korea-dpr", "Korea DPR", "kp")]
    [InlineData("republic-of-ireland", "Republic of Ireland", "ie")]
    [InlineData("palestine", "Palestine", "ps")]
    [InlineData("faroe-islands", "Faroe Islands", "fo")]
    public void TeamFlagCatalog_ResolvesStandardAndFootballTeamNames(string id, string name, string expected)
    {
        Assert.Equal(expected, TeamFlagCatalog.CodeFor(id, name));
    }

    [Fact]
    public void TeamFlagCatalog_ReturnsNoFlagForUnknownTeams()
    {
        Assert.Null(TeamFlagCatalog.CodeFor("made-up-xi", "Made Up XI"));
    }

}
