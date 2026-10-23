using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Placas.Application;
using Placas.Domain;

namespace Placas.Infrastructure;

public sealed class VrImporter : ISpreadsheetImporter
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public ImportBatch Read(string path, CancellationToken cancellationToken = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var items = new List<PriceItem>();
        var issues = new List<ImportIssue>();
        var families = new List<FamilyInfo>();
        var recognized = false;
        do
        {
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.GetValue(i);
                rows.Add(row);
            }
            var parsed = ParseRows(rows, Path.GetFileName(path), reader.Name, hash, cancellationToken);
            if (parsed is not null)
            {
                recognized = true; items.AddRange(parsed.Items); issues.AddRange(parsed.Issues);
                families.AddRange(parsed.Families ?? []);
            }
        } while (reader.NextResult());
        if (!recognized) throw new InvalidDataException("Nenhuma aba possui cabeçalhos reconhecidos do VR Master.");
        return new(hash, Path.GetFileName(path), items, issues, families);
    }

    public static ImportBatch? ParseRows(IReadOnlyList<object?[]> rows, string file, string sheet,
        string hash, CancellationToken cancellationToken = default)
    {
        Dictionary<string, int>? headers = null;
        SourceKind kind = default;
        var headerIndex = -1;
        for (var i = 0; i < Math.Min(rows.Count, 30); i++)
        {
            var h = rows[i].Select((v, index) => (key: Header(Text(v)), index))
                .Where(x => x.key.Length > 0).GroupBy(x => x.key)
                .ToDictionary(g => g.Key, g => g.First().index);
            if (h.ContainsKey("produto") && h.ContainsKey("descricao") && h.ContainsKey("precovenda")) kind = SourceKind.Catalog;
            else if (new[] { "codigo", "descricao", "precooferta", "preconormal", "datainicio", "datatermino" }.All(h.ContainsKey)) kind = SourceKind.Offer;
            else if (new[] { "codigo", "descricao", "valorfinal", "quantidade", "datainicio", "datatermino" }.All(h.ContainsKey)) kind = SourceKind.Promotion;
            else continue;
            headers = h; headerIndex = i; break;
        }
        if (headers is null) return null;
        var items = new List<PriceItem>();
        var issues = new List<ImportIssue>();
        var familyMap = new Dictionary<string, FamilyInfo>();
        // Coluna do código da família e, quando existir, a da descrição (ex.: "Descrição Família", "Nome da Família").
        var familyCodeKey = new[] { "codigofamilia", "codfamilia", "idfamilia", "familia" }.FirstOrDefault(headers.ContainsKey);
        var familyDescriptionKey = headers.Keys.FirstOrDefault(k => k != familyCodeKey && k.Contains("familia")
            && (k.Contains("desc") || k.Contains("nome")));
        string? group = null;
        DateOnly? groupStart = null, groupEnd = null;
        string? groupDescription = null;
        decimal quantity = 1;
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[i];
            if (row.All(v => string.IsNullOrWhiteSpace(Text(v)))) continue;
            object? Cell(string key) => headers.TryGetValue(key, out var index) && index < row.Length ? row[index] : null;
            try
            {
                var code = Text(Cell(kind == SourceKind.Catalog ? "produto" : "codigo"));
                var description = Text(Cell("descricao"));
                DateOnly? start = null, end = null;
                decimal price;
                decimal? normal = null;
                if (kind == SourceKind.Promotion)
                {
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        group = null; groupDescription = null; // Never inherit a previous group after an invalid header.
                        var newGroup = ProductCode.Normalize(code);
                        var newStart = Date(Cell("datainicio"));
                        var newEnd = Date(Cell("datatermino"));
                        var newQuantity = Money(Cell("quantidade"));
                        if (newQuantity < 1 || newQuantity != decimal.Truncate(newQuantity) || newStart > newEnd)
                            throw new FormatException("Quantidade ou período do grupo inválido.");
                        group = newGroup; groupStart = newStart; groupEnd = newEnd; quantity = newQuantity;
                        groupDescription = description.Length > 0 ? description : null;
                        continue;
                    }
                    if (group is null) throw new FormatException("Produto sem grupo promocional válido.");
                    var match = Regex.Match(description, @"^(\d+)\s*-\s*(.+)$");
                    if (!match.Success) throw new FormatException("Descrição deve começar com código - produto.");
                    code = match.Groups[1].Value; description = match.Groups[2].Value;
                    price = Money(Cell("valorfinal")); start = groupStart; end = groupEnd;
                }
                else if (kind == SourceKind.Offer)
                {
                    price = Money(Cell("precooferta")); normal = Money(Cell("preconormal"));
                    start = Date(Cell("datainicio")); end = Date(Cell("datatermino"));
                }
                else price = Money(Cell("precovenda"));
                if (description.Length == 0) throw new FormatException("Descrição vazia.");
                if (price < 0 || normal < 0 || start > end) throw new FormatException("Preço ou validade inválidos.");
                var key = ProductCode.Normalize(code);
                var id = $"{kind}:{key}:{group ?? ""}:{start:yyyyMMdd}:{end:yyyyMMdd}";
                string? family = null, familyDescription = null;
                var familyCell = familyCodeKey is null ? "" : Text(Cell(familyCodeKey));
                if (familyCell.Length > 0)
                {
                    // Aceita também "12 - LATICÍNIOS" na mesma célula.
                    var combined = Regex.Match(familyCell, @"^(\d+)\s*[-–]\s*(.+)$");
                    if (combined.Success) { family = combined.Groups[1].Value; familyDescription = combined.Groups[2].Value.Trim(); }
                    else family = familyCell;
                }
                if (familyDescriptionKey is not null && Text(Cell(familyDescriptionKey)) is { Length: > 0 } separate) familyDescription = separate;
                if (family is not null && !string.IsNullOrWhiteSpace(familyDescription))
                    familyMap[FamilyCode.Key(family)] = new FamilyInfo(family, familyDescription);
                items.Add(new(id, key, code, description, price, normal, start, end, kind, group,
                    kind == SourceKind.Promotion ? quantity : 1,
                    kind == SourceKind.Promotion && quantity > 1 ? QuantityRule.Pending : QuantityRule.UnitPrice,
                    file, sheet, i + 1, family,
                    GroupDescription: kind == SourceKind.Promotion ? groupDescription : null));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                issues.Add(new(sheet, i + 1, ex.Message));
            }
        }
        var unique = new List<PriceItem>();
        foreach (var duplicateGroup in items.GroupBy(x => x.Id))
        {
            if (duplicateGroup.Count() > 1)
                foreach (var item in duplicateGroup) issues.Add(new(sheet, item.Row, $"Condição duplicada para {item.DisplayCode}; revise a origem."));
            else unique.Add(duplicateGroup.Single());
        }
        return new(hash, file, unique, issues, familyMap.Values.ToList());
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
    private static string Header(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToLowerInvariant).ToArray());
    private static decimal Money(object? value)
    {
        if (value is null || Text(value).Length == 0) throw new FormatException("Valor obrigatório ausente.");
        if (value is string text)
        {
            text = text.Replace("R$", "").Trim();
            return decimal.Parse(text, NumberStyles.Number, text.Contains(',') ? Pt : CultureInfo.InvariantCulture);
        }
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }
    private static DateOnly Date(object? value)
    {
        if (value is DateTime date) return DateOnly.FromDateTime(date);
        if (value is double number) return DateOnly.FromDateTime(DateTime.FromOADate(number));
        var match = Regex.Match(Text(value), @"^\d{2}/\d{2}/\d{4}");
        if (!match.Success) throw new FormatException("Data obrigatória inválida.");
        return DateOnly.ParseExact(match.Value, "dd/MM/yyyy", CultureInfo.InvariantCulture);
    }
}