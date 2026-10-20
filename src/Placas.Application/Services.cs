
using System.Text.Json;
using Placas.Domain;

namespace Placas.Application;

public interface ISpreadsheetImporter
{
    ImportBatch Read(
        string path,
        CancellationToken cancellationToken = default
    );
}

public sealed record ImportRecord(
    string Hash,
    string Name,
    string Created,
    int LinkedItems,
    bool Managed
);

public interface IStore
{
    bool Import(ImportBatch batch);
    void SaveItem(PriceItem item);
    void DeleteItem(string id);

    List<ImportRecord> ListImports();

    int DeleteImport(string hash);

    int CountExpired(DateOnly date);

    int DeleteExpired(DateOnly date);

    List<PriceItem> Search(
        string text,
        SourceKind? source,
        int limit = 500
    );

    void ConfirmPromotion(string itemId, QuantityRule rule);

    List<PlateTemplate> Templates();

    void SaveTemplate(PlateTemplate template);

    void DeleteTemplate(string id);

    void SaveHistory(
        PrintBatch batch,
        string status,
        string detail
    );

    List<HistoryEntry> History();

    void Backup(string path);
}

public sealed class BatchService
{
    public PrintBatch Create(
        IEnumerable<PrintItem> selection,
        PlateTemplate template,
        DateOnly saleDate
    )
    {
        PlateRules.ValidateTemplate(template);

        var items = selection.ToList();

        if (items.Count == 0)
            throw new InvalidOperationException(
                "Selecione ao menos um produto."
            );

        if (items.Any(x => x.Copies is < 1 or > 500)
            || items.Sum(x => x.Copies) > 2000)
        {
            throw new InvalidOperationException(
                "Use 1–500 cópias por item e até 2.000 placas por lote."
            );
        }

        if (items.GroupBy(x => x.Product.Code)
            .Any(g => g.Count() > 1))
        {
            throw new InvalidOperationException(
                "Há mais de uma condição para o mesmo produto. " +
                "Selecione apenas uma."
            );
        }

        foreach (var item in items)
        {
            PlateRules.ValidateItem(
                item.Product,
                saleDate,
                template
            );
        }

        var snapshot =
            JsonSerializer.Deserialize<PlateTemplate>(
                JsonSerializer.Serialize(template)
            )!;

        return new PrintBatch(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            saleDate,
            snapshot,
            items
        );
    }
}

public static class Pagination
{
    public static (int Columns, int Rows) Grid(
        double pageWidthMm,
        double pageHeightMm,
        double margin,
        double gap,
        PlateTemplate template
    )
    {
        if (!double.IsFinite(margin)
            || !double.IsFinite(gap)
            || margin < 0
            || gap < 0)
        {
            throw new InvalidOperationException(
                $"Margem e espaçamento precisam ser números finitos e não negativos. " +
                $"Margem informada: {margin:0.##} mm; espaçamento informado: {gap:0.##} mm."
            );
        }

        var columns = (int)Math.Floor(
            (pageWidthMm - 2 * margin + gap)
            / (template.WidthMm + gap)
        );

        var rows = (int)Math.Floor(
            (pageHeightMm - 2 * margin + gap)
            / (template.HeightMm + gap)
        );

        if (columns < 1 || rows < 1)
        {
            var availableWidth = pageWidthMm - 2 * margin;
            var availableHeight = pageHeightMm - 2 * margin;
            var overWidth = Math.Max(0, template.WidthMm - availableWidth);
            var overHeight = Math.Max(0, template.HeightMm - availableHeight);
            var maxMarginWidth = (pageWidthMm - template.WidthMm) / 2;
            var maxMarginHeight = (pageHeightMm - template.HeightMm) / 2;
            throw new InvalidOperationException(
                $"O modelo não cabe no papel com estas margens.\n" +
                $"Papel: {pageWidthMm:0.##} × {pageHeightMm:0.##} mm.\n" +
                $"Placa: {template.WidthMm:0.##} × {template.HeightMm:0.##} mm.\n" +
                $"Margem por lado: {margin:0.##} mm; espaçamento: {gap:0.##} mm.\n" +
                $"Área disponível: {availableWidth:0.##} × {availableHeight:0.##} mm.\n" +
                $"Excesso: {overWidth:0.##} mm na largura e {overHeight:0.##} mm na altura.\n" +
                (maxMarginWidth < 0 || maxMarginHeight < 0
                    ? "O modelo é maior que a folha: reduza a placa ou escolha um papel maior."
                    : $"Para uma placa por folha, use margem de no máximo {Math.Min(maxMarginWidth, maxMarginHeight):0.##} mm por lado.")
            );
        }

        return (columns, rows);
    }
}
