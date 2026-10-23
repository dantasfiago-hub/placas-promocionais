using Placas.Application;
using Placas.Domain;
using Placas.Infrastructure;
using Xunit;

namespace Placas.Tests;

public class CoreTests
{
    private static PriceItem Item(decimal price = 5.69m) => new("offer:1", "1", "000001", "Produto teste", price, 7.99m,
        new(2026, 1, 1), new(2026, 12, 31), SourceKind.Offer, null, 1, QuantityRule.UnitPrice, "fixture.xls", "Sheet0", 2);
    private static DateOnly Date => new(2026, 10, 19);

    [Fact] public void ImportManagementRemovesOnlyExpiredAndPreservesCatalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            var expired = Item() with { Id = "expired", End = new DateOnly(2026, 3, 1) };
            var current = Item() with { Id = "current", End = new DateOnly(2026, 12, 31) };
            var catalog = Item() with { Id = "catalog", Source = SourceKind.Catalog, End = null };
            Assert.True(store.Import(new ImportBatch("old-hash", "offer.xlsx", [expired, current], [])));
            Assert.True(store.Import(new ImportBatch("catalog-hash", "database.xlsx", [catalog], [])));
            Assert.Equal(1, store.CountExpired(new DateOnly(2026, 10, 20)));
            Assert.Equal(1, store.DeleteExpired(new DateOnly(2026, 10, 20)));
            Assert.Equal(2, store.Search("", null).Count);
            Assert.Equal(1, store.DeleteImport("old-hash"));
            Assert.Single(store.Search("", null));
            Assert.Equal(SourceKind.Catalog, store.Search("", null)[0].Source);
            Assert.Single(store.ListImports(), x => x.Managed);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }


    [Fact] public void QuantityAndFamilyTokensResolve()
    {
        var template = PlateTemplate.Default();
        template.Elements[3].Text = "{quantidade}X";
        PlateRules.ValidateTemplate(template);
        var product = Item() with { Quantity = 5, Family = "BEBIDAS", Source = SourceKind.Promotion, Rule = QuantityRule.UnitPrice };
        Assert.Equal("5X - BEBIDAS", PlateRules.Resolve("{quantidade}X - {familia}", product));
        Assert.NotNull(new BatchService().Create([new(product, 1)], template, Date));
    }

    [Fact] public void ManualCrudPreservesEditedProduct()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            var product = Item() with { Id = "manual:1", Family = "MERCEARIA" };
            store.SaveItem(product);
            Assert.Equal("MERCEARIA", Assert.Single(store.Search("000001", null)).Family);
            store.SaveItem(product with { Description = "Outro nome", Quantity = 2 });
            Assert.Equal(2, Assert.Single(store.Search("Outro nome", null)).Quantity);
            store.DeleteItem(product.Id);
            Assert.Empty(store.Search("", null));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void CatalogFamilyIsAppliedToOfferImportedLater()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            store.Import(new ImportBatch("catalog-family", "base.xlsx",
                [Item() with { Id = "cat", Source = SourceKind.Catalog, Family = "LATICÍNIOS" }], []));
            store.Import(new ImportBatch("offer-family", "oferta.xlsx",
                [Item() with { Id = "off", Family = null }], []));
            Assert.Equal("LATICÍNIOS", store.Search("", SourceKind.Offer).Single().Family);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void CodesNormalizeWithoutLosingOriginal()
    {
        Assert.Equal("10486", ProductCode.Normalize("010486"));
        Assert.Equal("0", ProductCode.Normalize("000"));
        Assert.Throws<FormatException>(() => ProductCode.Normalize("12A"));
    }
    [Fact] public void ParsesHierarchyAndDoesNotTreatGroupAsProduct()
    {
        object?[][] rows = [
            ["Código", "Descrição", "Data Início", "Data Término", "Valor Final", "Quantidade"],
            ["024803", "SORVETE", "30/09/2026 - QUA", "31/12/2026 - QUI", "", 2],
            ["", "051210 - SORV KIBON MINI", "", "", 10d, ""]
        ];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Empty(batch.Issues); var item = Assert.Single(batch.Items);
        Assert.Equal("51210", item.Code); Assert.Equal("24803", item.GroupCode);
        Assert.Equal(2m, item.Quantity); Assert.Equal(QuantityRule.Pending, item.Rule);
        Assert.Equal(new DateOnly(2026, 12, 31), item.End);
    }
    [Fact] public void InvalidGroupDoesNotLeakPreviousGroup()
    {
        object?[][] rows = [
            ["Código", "Descrição", "Data Início", "Data Término", "Valor Final", "Quantidade"],
            ["1", "Valid", "01/01/2026", "31/12/2026", "", 1],
            ["2", "Bad", "not a date", "31/12/2026", "", 1],
            ["", "003 - orphan", "", "", 10, ""]
        ];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Empty(batch.Items); Assert.Equal(2, batch.Issues.Count);
    }
    [Fact] public void FindsReorderedHeadersAndBrazilianDecimal()
    {
        object?[][] rows = [["Relatório"], ["Descrição", "Preço Venda", "Produto"], ["Teste", "1.234,56", "001"]];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Equal(1234.56m, Assert.Single(batch.Items).Price);
    }
    [Fact] public void DuplicateConditionsAreRejectedInsteadOfOverwritten()
    {
        object?[][] rows = [["Produto", "Descrição", "Preço Venda"], [1, "A", 10], [1, "B", 20]];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Empty(batch.Items); Assert.Equal(2, batch.Issues.Count);
    }
    [Fact] public void ZeroPriceCannotPrint()
        => Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item(0), 1)], PlateTemplate.Default(), Date));
    [Fact] public void ExpiredOfferCannotPrint()
        => Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item(), 1)], PlateTemplate.Default(), new(2027, 1, 1)));
    [Fact] public void PendingPromotionCannotPrint()
        => Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item() with { Source = SourceKind.Promotion, Quantity = 2, Rule = QuantityRule.Pending }, 1)], PlateTemplate.Default(), Date));
    [Fact] public void QuantityConditionMustBeVisible()
    {
        var template = PlateTemplate.Default(); template.Elements.RemoveAll(x => x.Text.Contains("{condicao}"));
        Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item() with { Quantity = 2, Rule = QuantityRule.BundleTotal }, 1)], template, Date));
    }
    [Fact] public void DoesNotInventNormalPrice()
    {
        var template = PlateTemplate.Default(); template.Elements[1].Text = "De {preco_normal}";
        Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item() with { NormalPrice = null }, 1)], template, Date));
    }
    [Fact] public void ConflictingConditionsCannotEnterSameBatch()
        => Assert.Throws<InvalidOperationException>(() => new BatchService().Create([new(Item(), 1), new(Item() with { Id = "other" }, 1)], PlateTemplate.Default(), Date));
    [Fact] public void SnapshotIsIndependentOfTemplateEdits()
    {
        var template = PlateTemplate.Default();
        var batch = new BatchService().Create([new(Item(), 3)], template, Date);
        template.Elements[0].Text = "Changed";
        Assert.Equal("OFERTA", batch.Template.Elements[0].Text); Assert.Equal(3, batch.Items[0].Copies);
    }

    // Antes: PageFitErrorExplainsExactOverflowAndCorrection (lançava exceção).
    // Agora uma placa maior que o papel ocupa 1 posição por página, sem erro.
    [Fact] public void OversizedPlateStillGetsOneSlotPerPage()
    {
        var plate = PlateTemplate.Default();
        plate.WidthMm = 150;
        plate.HeightMm = 210;
        Assert.Equal((1, 1), Pagination.Grid(148, 210, 5, 0, plate));
    }

    [Fact] public void FourCompactSignsFitA4ButFullA6GetsOneSlot()
    {
        Assert.Equal((2, 2), Pagination.Grid(210, 297, 7, 3, PlateTemplate.Default()));
        var fullA6 = PlateTemplate.Default(); fullA6.WidthMm = 105; fullA6.HeightMm = 148;
        Assert.Equal((1, 1), Pagination.Grid(210, 297, 7, 3, fullA6));
    }

    [Fact] public void GridRejectsNonFiniteMargins()
        => Assert.Throws<InvalidOperationException>(() => Pagination.Grid(210, 297, double.NaN, 3, PlateTemplate.Default()));

    // Antes: TemplateRejectsOutOfBoundsAndUnknownFields.
    [Fact] public void TemplateRejectsUnknownFieldsAndNonPositiveSizes()
    {
        var template = PlateTemplate.Default();
        template.Elements[0].Text = "{inexistente}";
        Assert.Throws<InvalidOperationException>(() => PlateRules.ValidateTemplate(template));

        template = PlateTemplate.Default();
        template.Elements[0].Width = 0;
        Assert.Throws<InvalidOperationException>(() => PlateRules.ValidateTemplate(template));

        template = PlateTemplate.Default();
        template.Elements[0].X = 100; // fora da placa: agora é permitido
        PlateRules.ValidateTemplate(template);

        template = PlateTemplate.Default();
        template.WidthMm = 5000; // sem limite máximo de dimensão
        PlateRules.ValidateTemplate(template);
    }

    [Fact] public void ItalicDefaultsToFalseAndSurvivesSnapshot()
    {
        var template = PlateTemplate.Default();
        Assert.All(template.Elements, e => Assert.False(e.Italic));

        template.Elements[1].Italic = true;
        var batch = new BatchService().Create([new(Item(), 1)], template, Date);
        Assert.True(batch.Template.Elements[1].Italic);
        Assert.False(batch.Template.Elements[0].Italic);
    }

    [Fact] public void OldTemplateJsonWithoutItalicStillLoads()
    {
        var json = """{"SchemaVersion":1,"Name":"Antigo","WidthMm":95,"HeightMm":135,"Background":"#FFF03B","Elements":[{"Text":"{descricao}","X":5,"Y":30,"Width":90,"Height":30,"FontSize":24,"MinFontSize":9,"Foreground":"#111827","Background":"#00FFFFFF","Bold":true}]}""";
        var template = System.Text.Json.JsonSerializer.Deserialize<PlateTemplate>(json)!;
        Assert.False(template.Elements[0].Italic);
        PlateRules.ValidateTemplate(template);
    }

    [Fact] public void SqliteImportIsIdempotentAndPreservesExactDecimalsAndHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteStore(Path.Combine(directory, "test.db"));
            var item = Item(2.2735m);
            var import = new ImportBatch("same-hash", "fixture.xls", [item], []);
            Assert.True(store.Import(import)); Assert.False(store.Import(import));
            Assert.Equal(2.2735m, Assert.Single(store.Search("000001", null)).Price);
            var template = PlateTemplate.Default(); store.SaveTemplate(template);
            var batch = new BatchService().Create([new(item, 1)], template, Date);
            store.SaveHistory(batch, "Prévia", "teste");
            template.Name = "Changed"; store.SaveTemplate(template);
            Assert.NotEqual("Changed", Assert.Single(store.History()).Batch.Template.Name);
            store.Backup(Path.Combine(directory, "backup.db"));
            Assert.Single(new SqliteStore(Path.Combine(directory, "backup.db")).Search("", null));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact] public void SubDescriptionTokenIsValidAndOptional()
    {
        var template = PlateTemplate.Default();
        template.Elements[1].Text = "{descricao} {subdescricao}";
        PlateRules.ValidateTemplate(template);
        var filled = Item() with { Description = "ARROZ", SubDescription = "Tipo 1 - 5kg" };
        Assert.Equal("ARROZ Tipo 1 - 5kg", PlateRules.Resolve("{descricao} {subdescricao}", filled));
        Assert.Equal("ARROZ ", PlateRules.Resolve("{descricao} {subdescricao}", Item() with { Description = "ARROZ" }));
        Assert.NotNull(new BatchService().Create([new(Item(), 1)], template, Date));
    }

    [Fact] public void SubDescriptionIsSavedAndOldItemsLoadWithoutIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            store.SaveItem(Item() with { Id = "manual:sub", SubDescription = "Pacote 5kg" });
            Assert.Equal("Pacote 5kg", Assert.Single(store.Search("", null)).SubDescription);
            var old = System.Text.Json.JsonSerializer.Deserialize<PriceItem>(
                System.Text.Json.JsonSerializer.Serialize(Item() with { Family = "X" })
                    .Replace(",\"SubDescription\":null", ""))!;
            Assert.Null(old.SubDescription);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void ReimportKeepsManualSubDescription()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            store.Import(new ImportBatch("h1", "a.xlsx", [Item() with { Id = "x" }], []));
            store.SaveItem(Item() with { Id = "x", SubDescription = "Pacote 5kg" });
            store.Import(new ImportBatch("h2", "b.xlsx", [Item() with { Id = "x", Price = 6.99m }], []));
            var saved = Assert.Single(store.Search("", null));
            Assert.Equal(6.99m, saved.Price);
            Assert.Equal("Pacote 5kg", saved.SubDescription);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void DefaultTemplateIncludesOptionalSubDescription()
    {
        var template = PlateTemplate.Default();
        PlateRules.ValidateTemplate(template);
        var element = Assert.Single(template.Elements, x => x.Text == "{subdescricao}");
        Assert.Equal("Tipo 1", PlateRules.Resolve(element.Text, Item() with { SubDescription = "Tipo 1" }));
        Assert.Equal("", PlateRules.Resolve(element.Text, Item()));
        Assert.NotNull(new BatchService().Create([new(Item(), 1)], template, Date));
    }

    [Fact] public void ExpiredCleanupRemovesEmptiedImportsAndAllowsReimport()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            var batch = new ImportBatch("offer-hash", "oferta.xlsx", [Item() with { Id = "exp", End = new DateOnly(2026, 3, 1) }], []);
            Assert.True(store.Import(batch));
            Assert.Equal(1, store.DeleteExpired(new DateOnly(2026, 10, 20)));
            Assert.Empty(store.ListImports());
            Assert.True(store.Import(batch));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void DeletingLastItemRemovesImportRecord()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            store.Import(new ImportBatch("two-items", "duas.xlsx", [Item() with { Id = "a" }, Item() with { Id = "b" }], []));
            store.DeleteItem("a");
            Assert.Equal(1, Assert.Single(store.ListImports()).LinkedItems);
            store.DeleteItem("b");
            Assert.Empty(store.ListImports());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void ImportWithoutLinksCanBeRemovedWithoutTouchingProducts()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "test.db");
            var store = new SqliteStore(path);
            store.SaveItem(Item() with { Id = "manual:x" });
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO imports VALUES('legacy','antigo.xls','2026-01-01T00:00:00+00:00')";
                command.ExecuteNonQuery();
            }
            Assert.False(Assert.Single(store.ListImports()).Managed);
            Assert.Equal(0, store.DeleteImport("legacy"));
            Assert.Empty(store.ListImports());
            Assert.Single(store.Search("", null));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void ImporterReadsFamilyCodeAndDescriptionFromSeparateColumn()
    {
        object?[][] rows = [
            ["Produto", "Descrição", "Preço Venda", "Família", "Descrição Família"],
            ["1", "Iogurte morango", 3.5, "012", "LATICÍNIOS"],
            ["2", "Iogurte uva", 3.5, "012", "LATICÍNIOS"]
        ];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Empty(batch.Issues);
        Assert.All(batch.Items, x => Assert.Equal("012", x.Family));
        var family = Assert.Single(batch.Families!);
        Assert.Equal("LATICÍNIOS", family.Description);
    }

    [Fact] public void ImporterSplitsFamilyCodeAndDescriptionFromSingleCell()
    {
        object?[][] rows = [
            ["Produto", "Descrição", "Preço Venda", "Família"],
            ["1", "Suco", 4, "12 - BEBIDAS"]
        ];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Equal("12", Assert.Single(batch.Items).Family);
        Assert.Equal("BEBIDAS", Assert.Single(batch.Families!).Description);
    }

    [Fact] public void FamilyDescriptionIsStoredOnceAndResolvedOnSearch()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            store.Import(new ImportBatch("h", "a.xlsx",
                [Item() with { Id = "c", Source = SourceKind.Catalog, End = null, Family = "012" }], [],
                [new FamilyInfo("012", "LATICÍNIOS")]));
            Assert.Equal("LATICÍNIOS", Assert.Single(store.Search("", null)).FamilyDescription);
            store.SaveFamily(new FamilyInfo("12", "LATICÍNIOS PREMIUM"));
            Assert.Equal("LATICÍNIOS PREMIUM", Assert.Single(store.Search("", null)).FamilyDescription);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact] public void FamilyPlateUsesFamilyDescriptionAndRequiresSameCondition()
    {
        var a = Item() with { Id = "a", Code = "1", Family = "012", FamilyDescription = "LATICÍNIOS" };
        var b = a with { Id = "b", Code = "2", Description = "Outro produto" };
        var plate = FamilyPlates.Build([a, b]);
        Assert.Equal("LATICÍNIOS", plate.Description);
        Assert.Equal("familia:12", plate.Code);
        Assert.NotNull(new BatchService().Create([new(plate, 1)], PlateTemplate.Default(), Date));
        Assert.Equal("LATICÍNIOS", PlateRules.Resolve("{descricao_familia}", a));

        Assert.Throws<InvalidOperationException>(() => FamilyPlates.Build([a, b with { Price = 9.99m }]));
        Assert.Throws<InvalidOperationException>(() => FamilyPlates.Build([a with { FamilyDescription = null }]));
        Assert.Throws<InvalidOperationException>(() => FamilyPlates.Build([a, b with { Family = "99" }]));
    }

    [Fact] public void ParserKeepsPromotionGroupDescription()
    {
        object?[][] rows = [
            ["Código", "Descrição", "Data Início", "Data Término", "Valor Final", "Quantidade"],
            ["024803", "SORVETE KIBON MINI 92G", "30/09/2026 - QUA", "31/12/2026 - QUI", "", 2],
            ["", "051210 - SORV KIBON MINI CHICABON CHOC 92G", "", "", 10d, ""]
        ];
        var batch = VrImporter.ParseRows(rows, "test.xlsx", "Sheet0", "a")!;
        Assert.Equal("SORVETE KIBON MINI 92G", Assert.Single(batch.Items).GroupDescription);
    }

    [Fact] public void PromotionGroupDescriptionIsUsedWhenProductHasNoFamily()
    {
        var a = Item() with { Id = "a", Code = "1", Source = SourceKind.Promotion, GroupCode = "24803",
            GroupDescription = "SORVETE KIBON MINI 92G", Quantity = 2, Rule = QuantityRule.UnitPrice };
        var b = a with { Id = "b", Code = "2" };
        var plate = FamilyPlates.Build([a, b]);
        Assert.Equal("SORVETE KIBON MINI 92G", plate.Description);
        Assert.Equal("grupo:24803", plate.Code);
        // Com família descrita, a família tem prioridade sobre o grupo.
        var withFamily = a with { Family = "012", FamilyDescription = "SORVETES" };
        Assert.Equal("SORVETES", FamilyPlates.Build([withFamily]).Description);
        // Sem família e fora de grupo de promoção, não há como montar a placa coletiva.
        Assert.Null(FamilyPlates.Key(Item()));
    }

    [Fact] public void ReimportingSameFileFillsMissingGroupDescription()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "test.db"));
            var promotion = Item() with { Id = "p1", Source = SourceKind.Promotion, GroupCode = "1", Quantity = 2, Rule = QuantityRule.UnitPrice };
            Assert.True(store.Import(new ImportBatch("hp", "promo.xlsx", [promotion], [])));
            Assert.False(store.Import(new ImportBatch("hp", "promo.xlsx", [promotion with { GroupDescription = "BEBIDA MISTA" }], [])));
            Assert.Equal("BEBIDA MISTA", Assert.Single(store.Search("", null)).GroupDescription);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}