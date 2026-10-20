using System.Text.Json;
using Placas.Domain;

namespace Placas.Application;

public interface ISpreadsheetImporter
{
    ImportBatch Read(string path, CancellationToken cancellationToken = default);
}

public sealed record ImportRecord(string Hash, string Name, string Created, int LinkedItems, bool Managed);

public interface IStore
{
    bool Import(ImportBatch batch);
    List<ImportRecord> ListImports();
    int DeleteImport(string hash);
    int CountExpired(DateOnly date);
    int DeleteExpired(DateOnly date);
    List<PriceItem> Search(string text, SourceKind? source, int limit = 500);
    void ConfirmPromotion(string itemId, QuantityRule rule);
    List<PlateTemplate> Templates();
    void SaveTemplate(PlateTemplate template);
    void DeleteTemplate(string id);
    void SaveHistory(PrintBatch batch, string status, string detail);
    List<HistoryEntry> History();
    void Backup(string path);
}

public sealed class BatchService
{
    public PrintBatch Create(IEnumerable<PrintItem> selection, PlateTemplate template, DateOnly saleDate)
    {
        PlateRules.ValidateTemplate(template);
        var items = selection.ToList();
        if (items.Count == 0) throw new InvalidOperationException("Selecione ao menos um produto.");
        if (items.Any(x => x.Copies is < 1 or > 500) || items.Sum(x => x.Copies) > 2000)
            throw new InvalidOperationException("Use 1–500 cópias por item e até 2.000 placas por lote.");
        if (items.GroupBy(x => x.Product.Code).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Há mais de uma condição para o mesmo produto. Selecione apenas uma.");
        foreach (var item in items) PlateRules.ValidateItem(item.Product, saleDate, template);
        var snapshot = JsonSerializer.Deserialize<PlateTemplate>(JsonSerializer.Serialize(template))!;
        return new(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, saleDate, snapshot, items);
    }
}

public static class Pagination
{
    public static (int Columns, int Rows) Grid(double pageWidthMm, double pageHeightMm,
        double margin, double gap, PlateTemplate template)
    {
        if (!double.IsFinite(margin) || !double.IsFinite(gap) || margin < 0 || gap < 0)
            throw new InvalidOperationException("Margem e espaçamento inválidos.");
        var columns = (int)Math.Floor((pageWidthMm - 2 * margin + gap) / (template.WidthMm + gap));
        var rows = (int)Math.Floor((pageHeightMm - 2 * margin + gap) / (template.HeightMm + gap));
        if (columns < 1 || rows < 1) throw new InvalidOperationException("O modelo não cabe no papel com estas margens.");
        return (columns, rows);
    }
}
