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
    void SaveFamily(FamilyInfo family);
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

public static class FamilyPlates
{
    // Chave de agrupamento das placas coletivas. Prioridade: família com descrição; depois, o grupo
    // da promoção (que tem descrição própria na planilha); por fim a família sem descrição (Build explica o erro).
    public static string? Key(PriceItem item)
    {
        var hasFamily = !string.IsNullOrWhiteSpace(item.Family);
        if (hasFamily && !string.IsNullOrWhiteSpace(item.FamilyDescription)) return "F:" + FamilyCode.Key(item.Family!);
        if (item.Source == SourceKind.Promotion && !string.IsNullOrWhiteSpace(item.GroupCode)
            && !string.IsNullOrWhiteSpace(item.GroupDescription)) return "G:" + FamilyCode.Key(item.GroupCode!);
        return hasFamily ? "F:" + FamilyCode.Key(item.Family!) : null;
    }

    // Monta o item da placa coletiva: a descrição passa a ser a da família (ou do grupo da promoção) e a
    // condição comercial (preço, validade, quantidade) é a que todos os produtos marcados têm em comum.
    public static PriceItem Build(IReadOnlyList<PriceItem> items)
    {
        if (items.Count == 0) throw new InvalidOperationException("Nenhum produto informado.");
        var first = items[0];
        var key = Key(first) ?? throw new InvalidOperationException(
            $"O produto '{first.Description}' não tem família nem grupo de promoção com descrição.");
        if (items.Any(x => Key(x) != key))
            throw new InvalidOperationException("Os produtos informados pertencem a famílias ou grupos diferentes.");
        var isGroup = key.StartsWith("G:", StringComparison.Ordinal);
        var code = key[2..];
        var description = isGroup
            ? items.Select(x => x.GroupDescription).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            : items.Select(x => x.FamilyDescription).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (description is null)
            throw new InvalidOperationException(
                $"A família {first.Family} não tem descrição. Edite um produto dela e preencha 'Descrição da família'.");
        var conditions = items.Select(x => (x.Source, x.Price, x.NormalPrice, x.Start, x.End, x.Quantity, x.Rule)).Distinct().Count();
        if (conditions > 1)
            throw new InvalidOperationException(
                $"Os produtos marcados de '{description}' têm preço, validade ou quantidade diferentes. " +
                "Marque apenas os que compartilham a mesma condição.");
        description = description.Trim();
        return first with
        {
            Id = $"{(isGroup ? "group" : "family")}:{code}:{first.Source}:{first.Start:yyyyMMdd}:{first.End:yyyyMMdd}",
            Code = (isGroup ? "grupo:" : "familia:") + code,
            DisplayCode = (isGroup ? "Grupo " : "Fam. ") + code,
            Description = description,
            SubDescription = null,
            FamilyDescription = isGroup ? first.FamilyDescription : description,
            GroupDescription = isGroup ? description : first.GroupDescription
        };
    }
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

// Margens da folha, cada lado independente (em mm).
public sealed record PageMargins(double Top, double Right, double Bottom, double Left)
{
    public static PageMargins All(double value) => new(value, value, value, value);
    public bool IsFinite => double.IsFinite(Top) && double.IsFinite(Right) && double.IsFinite(Bottom) && double.IsFinite(Left);
}

public static class Pagination
{
        public static (int Columns, int Rows) Grid(
        double pageWidthMm,
        double pageHeightMm,
        double margin,
        double gap,
        PlateTemplate template
    ) => Grid(pageWidthMm, pageHeightMm, PageMargins.All(margin), gap, template);

    public static (int Columns, int Rows) Grid(
        double pageWidthMm,
        double pageHeightMm,
        PageMargins margins,
        double gap,
        PlateTemplate template
    )
    {
        if (!margins.IsFinite || !double.IsFinite(gap))
            throw new InvalidOperationException(
                "Margens e espaçamento precisam ser números válidos."
            );

        
        var columns = Math.Max(1, (int)Math.Floor(
            (pageWidthMm - margins.Left - margins.Right + gap) / (template.WidthMm + gap)));

        var rows = Math.Max(1, (int)Math.Floor(
            (pageHeightMm - margins.Top - margins.Bottom + gap) / (template.HeightMm + gap)));

        return (columns, rows);
    }
}