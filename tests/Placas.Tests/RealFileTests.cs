using Placas.Domain;
using Placas.Infrastructure;
using Xunit;

namespace Placas.Tests;

public sealed class RealFilesFactAttribute : FactAttribute
{
    public RealFilesFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VR_FIXTURES_DIR")))
            Skip = "Defina VR_FIXTURES_DIR para testar as planilhas comerciais fora do repositório.";
    }
}

public class RealFileTests
{
    // Commercial spreadsheets remain outside git. Set VR_FIXTURES_DIR for local verification.
    [RealFilesFact]
    public void OriginalVrExportsMatchExpectedStructure()
    {
        var folder = Environment.GetEnvironmentVariable("VR_FIXTURES_DIR")!;
        var importer = new VrImporter();
        var catalog = importer.Read(Path.Combine(folder, "DATABASE.xlsx"));
        var offers = importer.Read(Path.Combine(folder, "Consulta_de_Oferta_15-10-2026_81620.xls"));
        var promotions = importer.Read(Path.Combine(folder, "Consulta_de_Promoção_15102026_82233.xlsx"));
        Assert.Empty(catalog.Issues); Assert.Empty(offers.Issues); Assert.Empty(promotions.Issues);
        Assert.Equal(54380, catalog.Items.Count); Assert.Equal(11437, catalog.Items.Count(x => x.Price == 0));
        Assert.Equal(14, offers.Items.Count); Assert.Equal(56, promotions.Items.Count);
        Assert.Equal(4, promotions.Items.Select(x => x.GroupCode).Distinct().Count());
        Assert.Equal(7, promotions.Items.Count(x => x.Rule == QuantityRule.Pending));
        var codes = catalog.Items.Select(x => x.Code).ToHashSet();
        Assert.All(offers.Items.Concat(promotions.Items), x => Assert.Contains(x.Code, codes));
        Assert.All(offers.Items, x => Assert.Equal(x.Price, catalog.Items.Single(p => p.Code == x.Code).Price));
    }
}
