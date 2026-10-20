using System.Globalization;
using System.Text.RegularExpressions;

namespace Placas.Domain;

public static class ProductCode
{
    public static string Normalize(string raw)
    {
        var value = raw.Trim();
        if (!Regex.IsMatch(value, @"^\d+$"))
            throw new FormatException($"Código inválido: {raw}");
        return value.TrimStart('0') is { Length: > 0 } key ? key : "0";
    }
}

public enum SourceKind { Catalog, Offer, Promotion }
public enum QuantityRule { Pending, UnitPrice, BundleTotal }
public sealed record PriceItem(
    string Id, string Code, string DisplayCode, string Description,
    decimal Price, decimal? NormalPrice, DateOnly? Start, DateOnly? End,
    SourceKind Source, string? GroupCode, decimal Quantity,
    QuantityRule Rule, string FileName, string Sheet, int Row);

public sealed record ImportIssue(string Sheet, int Row, string Message);
public sealed record ImportBatch(string Hash, string FileName, List<PriceItem> Items, List<ImportIssue> Issues);
public sealed record PrintItem(PriceItem Product, int Copies);
public sealed record PrintBatch(string Id, DateTimeOffset CreatedAt, DateOnly SaleDate,
    PlateTemplate Template, List<PrintItem> Items);
public sealed record HistoryEntry(string Id, string CreatedAt, string Status, string Detail, PrintBatch Batch);

public sealed class PlateElement
{
    public string Text { get; set; } = "{descricao}";
    public double X { get; set; } = 5;
    public double Y { get; set; } = 30;
    public double Width { get; set; } = 90;
    public double Height { get; set; } = 30;
    public double FontSize { get; set; } = 24;
    public double MinFontSize { get; set; } = 9;
    public string Foreground { get; set; } = "#111827";
    public string Background { get; set; } = "#00FFFFFF";
    public bool Bold { get; set; } = true;
}

public sealed class PlateTemplate
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Oferta compacta";
    public double WidthMm { get; set; } = 95;
    public double HeightMm { get; set; } = 135;
    public string Background { get; set; } = "#FFF03B";
    public List<PlateElement> Elements { get; set; } = [];

    public static PlateTemplate Default() => new()
    {
        Elements = [
            new() { Text = "OFERTA", X = 0, Y = 0, Width = 95, Height = 24,
                FontSize = 38, Foreground = "#FFFFFF", Background = "#D71920" },
            new() { Text = "{descricao}", X = 5, Y = 29, Width = 85, Height = 35, FontSize = 24 },
            new() { Text = "{preco}", X = 4, Y = 68, Width = 87, Height = 33, FontSize = 62 },
            new() { Text = "{condicao}", X = 4, Y = 103, Width = 87, Height = 13, FontSize = 13 },
            new() { Text = "{validade}", X = 4, Y = 119, Width = 87, Height = 8, FontSize = 10 },
            new() { Text = "Cód. {codigo}", X = 4, Y = 128, Width = 87, Height = 5, FontSize = 9 }
        ]
    };
}

public static class PlateRules
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public static readonly string[] Tokens = ["descricao", "codigo", "preco", "preco_normal", "validade", "condicao"];
    public static void ValidateTemplate(PlateTemplate template)
    {
        if (template.SchemaVersion != 1 || string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidOperationException("Modelo sem nome ou versão não suportada.");
        if (!double.IsFinite(template.WidthMm) || !double.IsFinite(template.HeightMm) ||
            template.WidthMm < 20 || template.HeightMm < 20 || template.WidthMm > 1000 || template.HeightMm > 1000)
            throw new InvalidOperationException("Dimensões do modelo devem estar entre 20 e 1000 mm.");
        if (template.Elements is null || template.Elements.Count is < 1 or > 50)
            throw new InvalidOperationException("O modelo deve conter entre 1 e 50 elementos.");
        ValidateColor(template.Background);
        foreach (var e in template.Elements)
        {
            if (new[] { e.X, e.Y, e.Width, e.Height, e.FontSize, e.MinFontSize }.Any(x => !double.IsFinite(x)) ||
                e.X < 0 || e.Y < 0 || e.Width <= 0 || e.Height <= 0 ||
                e.X + e.Width > template.WidthMm + .001 || e.Y + e.Height > template.HeightMm + .001 ||
                e.MinFontSize < 6 || e.FontSize < e.MinFontSize || e.FontSize > 300)
                throw new InvalidOperationException("Elemento fora da placa ou tamanho de fonte inválido.");
            ValidateColor(e.Foreground); ValidateColor(e.Background);
            foreach (Match match in Regex.Matches(e.Text ?? "", @"\{([^{}]+)\}"))
                if (!Tokens.Contains(match.Groups[1].Value))
                    throw new InvalidOperationException($"Campo desconhecido: {match.Value}");
        }
    }

    private static void ValidateColor(string color)
    {
        if (!Regex.IsMatch(color ?? "", @"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
            throw new InvalidOperationException("Use cores hexadecimais como #FFF03B.");
    }

    public static void ValidateItem(PriceItem item, DateOnly saleDate, PlateTemplate template)
    {
        if (item.Price <= 0) throw new InvalidOperationException($"{item.DisplayCode}: preço ausente ou zerado.");
        if (item.Start > saleDate || item.End < saleDate)
            throw new InvalidOperationException($"{item.DisplayCode}: condição fora da validade em {saleDate:dd/MM/yyyy}.");
        if (item.Source == SourceKind.Promotion && item.Rule == QuantityRule.Pending)
            throw new InvalidOperationException($"{item.DisplayCode}: confirme a regra da promoção antes de imprimir.");
        if (template.Elements.Any(x => x.Text.Contains("{preco_normal}")) &&
            (item.NormalPrice is null || item.NormalPrice <= item.Price))
            throw new InvalidOperationException($"{item.DisplayCode}: preço normal ausente ou menor/igual ao promocional.");
        if (item.Quantity > 1 && !template.Elements.Any(x => x.Text.Contains("{condicao}")))
            throw new InvalidOperationException("Este produto exige um modelo com o campo {condicao}.");
    }

    public static string Resolve(string text, PriceItem item)
    {
        var condition = item.Quantity <= 1 ? "" : item.Rule switch
        {
            QuantityRule.BundleTotal => $"{item.Quantity.ToString("0.##", Pt)} unidades por {item.Price.ToString("C2", Pt)}",
            QuantityRule.UnitPrice => $"Preço por unidade na compra de {item.Quantity.ToString("0.##", Pt)}",
            _ => "CONDIÇÃO NÃO CONFIRMADA"
        };
        return text.Replace("{descricao}", item.Description).Replace("{codigo}", item.DisplayCode)
            .Replace("{preco}", item.Price.ToString("C2", Pt))
            .Replace("{preco_normal}", item.NormalPrice?.ToString("C2", Pt) ?? "")
            .Replace("{validade}", item.End is { } end ? $"Válido até {end:dd/MM/yyyy}" : "")
            .Replace("{condicao}", condition);
    }
}
