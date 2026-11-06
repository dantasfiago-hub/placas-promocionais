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

public static class FamilyCode
{
    public static string Key(string raw)
    {
        var value = raw.Trim();
        if (value.Length > 0 && value.All(char.IsDigit))
            return value.TrimStart('0') is { Length: > 0 } key ? key : "0";
        return value.ToUpperInvariant();
    }
}

public sealed record FamilyInfo(string Code, string Description);

public static class ImageSources
{
    public const string Product = "";                       
    public const string Family = "@familia";                
    public const string ProductOrFamily = "@produto-familia"; 
    public static bool IsDynamic(string? source) => string.IsNullOrWhiteSpace(source) || source.StartsWith('@');
}

public sealed record ImageInfo(string Id, string Name, int Bytes, byte[] Thumbnail, int ProductCount, int LayoutCount, int FamilyCount = 0);

public enum SourceKind { Catalog, Offer, Promotion }
public enum QuantityRule { Pending, UnitPrice, BundleTotal }
public sealed record PriceItem(
    string Id, string Code, string DisplayCode, string Description,
    decimal Price, decimal? NormalPrice, DateOnly? Start, DateOnly? End,
    SourceKind Source, string? GroupCode, decimal Quantity,
    QuantityRule Rule, string FileName, string Sheet, int Row, string? Family = null,
    string? SubDescription = null, string? FamilyDescription = null, string? GroupDescription = null,
    string? ImageId = null, string? FamilyImageId = null);

public sealed record ImportIssue(string Sheet, int Row, string Message);
public sealed record ImportBatch(string Hash, string FileName, List<PriceItem> Items, List<ImportIssue> Issues,
    List<FamilyInfo>? Families = null);
public sealed record PrintItem(PriceItem Product, int Copies);
public sealed record PrintBatch(string Id, DateTimeOffset CreatedAt, DateOnly SaleDate,
    PlateTemplate Template, List<PrintItem> Items);
public sealed record HistoryEntry(string Id, string CreatedAt, string Status, string Detail, PrintBatch Batch);

public static class PlateShapes
{
    public const string Rectangle = "rectangle";
    public const string Ellipse = "ellipse";
    public const string Line = "line";
    public const string Triangle = "triangle";
    public const string Arrow = "arrow";
    public const string Star = "star";

    public static bool IsShape(string? shape) => shape is Rectangle or Ellipse or Line or Triangle or Arrow or Star;
}

public sealed class PlateTextStyle
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? Foreground { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
}

public static class PlateTextFormatting
{
    public static List<PlateTextStyle> Apply(
        IReadOnlyList<PlateTextStyle> styles, int textLength, int start, int length, Action<PlateTextStyle> change)
    {
        start = Math.Clamp(start, 0, textLength);
        var end = Math.Clamp(start + length, start, textLength);
        if (start == end) return styles.Select(Clone).ToList();

        var boundaries = new SortedSet<int> { 0, textLength, start, end };
        foreach (var style in styles)
        {
            boundaries.Add(Math.Clamp(style.Start, 0, textLength));
            boundaries.Add(Math.Clamp(style.Start + style.Length, 0, textLength));
        }

        var points = boundaries.ToArray();
        var result = new List<PlateTextStyle>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var segmentStart = points[index];
            var segmentEnd = points[index + 1];
            if (segmentEnd <= segmentStart) continue;

            var source = styles.FirstOrDefault(style =>
                style.Start <= segmentStart && style.Start + style.Length >= segmentEnd);
            var segment = source is null
                ? new PlateTextStyle()
                : Clone(source);
            segment.Start = segmentStart;
            segment.Length = segmentEnd - segmentStart;
            if (segmentStart >= start && segmentEnd <= end) change(segment);
            if (!HasOverrides(segment)) continue;
            if (result.Count > 0 && result[^1].Start + result[^1].Length == segment.Start && SameStyle(result[^1], segment))
                result[^1].Length += segment.Length;
            else
                result.Add(segment);
        }
        return result;
    }

    public static List<PlateTextStyle> Clear(
        IReadOnlyList<PlateTextStyle> styles, Action<PlateTextStyle> clear)
    {
        var result = new List<PlateTextStyle>();
        foreach (var style in styles.OrderBy(style => style.Start).Select(Clone))
        {
            clear(style);
            if (!HasOverrides(style)) continue;
            if (result.Count > 0 && result[^1].Start + result[^1].Length == style.Start && SameStyle(result[^1], style))
                result[^1].Length += style.Length;
            else
                result.Add(style);
        }
        return result;
    }

    public static List<PlateTextStyle> Remap(
        IReadOnlyList<PlateTextStyle> styles, string previousText, string updatedText)
    {
        var prefix = 0;
        while (prefix < previousText.Length && prefix < updatedText.Length && previousText[prefix] == updatedText[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < previousText.Length - prefix && suffix < updatedText.Length - prefix &&
               previousText[previousText.Length - 1 - suffix] == updatedText[updatedText.Length - 1 - suffix])
            suffix++;

        var previousEditEnd = previousText.Length - suffix;
        var updatedEditEnd = updatedText.Length - suffix;
        var delta = updatedEditEnd - previousEditEnd;
        if (previousEditEnd == prefix && updatedEditEnd > prefix)
        {
            var inherited = styles.FirstOrDefault(style => style.Start < prefix && style.Start + style.Length >= prefix);
            var result = styles.Select(Clone).ToList();
            if (inherited is not null)
            {
                var matching = result.First(style => style.Start == inherited.Start && style.Length == inherited.Length);
                matching.Length += updatedEditEnd - prefix;
            }
            foreach (var style in result.Where(style => inherited is null || style.Start != inherited.Start))
                if (style.Start >= prefix) style.Start += delta;
            return result;
        }

        var remapped = new List<PlateTextStyle>();
        foreach (var style in styles)
        {
            var styleEnd = style.Start + style.Length;
            if (style.Start < prefix)
            {
                var leftEnd = Math.Min(styleEnd, prefix);
                if (leftEnd > style.Start)
                {
                    var left = Clone(style);
                    left.Length = leftEnd - left.Start;
                    remapped.Add(left);
                }
            }
            if (styleEnd > previousEditEnd)
            {
                var rightStart = Math.Max(style.Start, previousEditEnd);
                var right = Clone(style);
                right.Start = rightStart + delta;
                right.Length = styleEnd - rightStart;
                remapped.Add(right);
            }
        }
        return remapped.OrderBy(style => style.Start).ToList();
    }

    public static PlateTextStyle Clone(PlateTextStyle style) => new()
    {
        Start = style.Start, Length = style.Length, FontFamily = style.FontFamily, FontSize = style.FontSize,
        Foreground = style.Foreground, Bold = style.Bold, Italic = style.Italic, Underline = style.Underline
    };

    private static bool HasOverrides(PlateTextStyle style) =>
        style.FontFamily is not null || style.FontSize is not null || style.Foreground is not null ||
        style.Bold is not null || style.Italic is not null || style.Underline is not null;

    private static bool SameStyle(PlateTextStyle left, PlateTextStyle right) =>
        left.FontFamily == right.FontFamily && left.FontSize == right.FontSize && left.Foreground == right.Foreground &&
        left.Bold == right.Bold && left.Italic == right.Italic && left.Underline == right.Underline;
}

public sealed class PlateElement
{
    public string Text { get; set; } = "{descricao}";
    public double X { get; set; } = 5;
    public double Y { get; set; } = 30;
    public double Width { get; set; } = 90;
    public double Height { get; set; } = 30;
    public double RotationDegrees { get; set; }
    public double FontSize { get; set; } = 24;
    public double MinFontSize { get; set; } = 9;
    public string Foreground { get; set; } = "#111827";
    public string Background { get; set; } = "#00FFFFFF";
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; } = false;
    public bool Underline { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public string TextAlign { get; set; } = "Center";
    public List<PlateTextStyle> TextStyles { get; set; } = [];
    public string Shape { get; set; } = "";
    public double StrokeWidth { get; set; } = 1;
    public bool IsImage { get; set; } = false;
    public string ImageId { get; set; } = ""; 
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
            new() { Text = "{descricao}", X = 5, Y = 29, Width = 85, Height = 27, FontSize = 24 },
            new() { Text = "{preco}", X = 4, Y = 68, Width = 87, Height = 33, FontSize = 62 },
            new() { Text = "{condicao}", X = 4, Y = 103, Width = 87, Height = 13, FontSize = 13 },
            new() { Text = "{validade}", X = 4, Y = 119, Width = 87, Height = 8, FontSize = 10 },
            new() { Text = "Cód. {codigo}", X = 4, Y = 128, Width = 87, Height = 5, FontSize = 9 },
            new() { Text = "{subdescricao}", X = 5, Y = 57, Width = 85, Height = 9, FontSize = 12, MinFontSize = 7, Foreground = "#374151", Bold = false }
        ]
    };
}

public static class PlateRules
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public static readonly string[] Tokens = ["descricao", "codigo", "preco", "preco_normal","inicio", "validade", "condicao","quantidade","familia","subdescricao","descricao_familia","inicio_curto","validade_curta"];
    public static void ValidateTemplate(PlateTemplate template)
    {
        if (template.SchemaVersion != 1 || string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidOperationException("Modelo sem nome ou versão não suportada.");
        if (!double.IsFinite(template.WidthMm) || !double.IsFinite(template.HeightMm) ||
            template.WidthMm <= 0 || template.HeightMm <= 0)
            throw new InvalidOperationException("Largura e altura do modelo devem ser números maiores que zero.");
        if (template.Elements is null || template.Elements.Count < 1)
            throw new InvalidOperationException("O modelo deve conter pelo menos 1 elemento.");
        ValidateColor(template.Background);
        foreach (var e in template.Elements)
        {
            if (new[] { e.X, e.Y, e.Width, e.Height, e.RotationDegrees, e.FontSize, e.MinFontSize, e.StrokeWidth }.Any(x => !double.IsFinite(x)) ||
                e.Width <= 0 || e.Height <= 0 || e.FontSize <= 0 || e.MinFontSize <= 0)
                throw new InvalidOperationException("Largura, altura e tamanhos de fonte devem ser números maiores que zero.");
            if (e.StrokeWidth < 0 || (!string.IsNullOrEmpty(e.Shape) && !PlateShapes.IsShape(e.Shape)))
                throw new InvalidOperationException("Forma ou espessura de contorno inválida.");
            if (e.IsImage && !string.IsNullOrEmpty(e.Shape))
                throw new InvalidOperationException("Um elemento não pode ser imagem e forma ao mesmo tempo.");
            if (e.TextStyles is null)
                throw new InvalidOperationException("A formatação do texto está inválida.");
            ValidateColor(e.Foreground); ValidateColor(e.Background);
            foreach (var style in e.TextStyles)
            {
                if (style.Start < 0 || style.Length <= 0 || style.Start + style.Length > (e.Text ?? "").Length ||
                    style.FontSize is { } size && (!double.IsFinite(size) || size <= 0) ||
                    style.FontFamily is not null && string.IsNullOrWhiteSpace(style.FontFamily))
                    throw new InvalidOperationException("A formatação do texto contém um intervalo ou tamanho inválido.");
                if (style.Foreground is not null) ValidateColor(style.Foreground);
            }
            if (e.TextStyles.OrderBy(style => style.Start).Zip(e.TextStyles.OrderBy(style => style.Start).Skip(1))
                .Any(pair => pair.First.Start + pair.First.Length > pair.Second.Start))
                throw new InvalidOperationException("Os intervalos de formatação do texto não podem se sobrepor.");
            foreach (Match match in Regex.Matches(e.IsImage || !string.IsNullOrEmpty(e.Shape) ? "" : e.Text ?? "", @"\{([^{}]+)\}"))
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
        if (template.Elements.Any(x => !x.IsImage && string.IsNullOrEmpty(x.Shape) && x.Text.Contains("{preco_normal}")) &&
            (item.NormalPrice is null || item.NormalPrice <= item.Price))
            throw new InvalidOperationException($"{item.DisplayCode}: preço normal ausente ou menor/igual ao promocional.");
        if (item.Quantity > 1 && !template.Elements.Any(x => !x.IsImage && string.IsNullOrEmpty(x.Shape) &&
            (x.Text.Contains("{condicao}") || x.Text.Contains("{quantidade}"))))
            throw new InvalidOperationException("Promoções por quantidade exigem {condicao} ou {quantidade} no layout.");
        
    }

    public static string Resolve(string text, PriceItem item)
    {
        var condition = item.Quantity <= 1 ? "" : item.Rule switch
        {
            QuantityRule.BundleTotal => $"{item.Quantity.ToString("0.##", Pt)} unidades por {item.Price.ToString("C2", Pt)}",
            QuantityRule.UnitPrice => $"Preço por unidade na compra de {item.Quantity.ToString("0.##", Pt)}",
            _ => "CONDIÇÃO NÃO CONFIRMADA"
        };
        return text
            .Replace("{descricao}", item.Description)
            .Replace("{familia}", item.Family ?? "")
            .Replace("{subdescricao}", item.SubDescription ?? "")
            .Replace("{descricao_familia}", item.FamilyDescription ?? "")
            .Replace("{codigo}", item.DisplayCode)
            .Replace("{preco}", item.Price.ToString("N2", Pt))
            .Replace("{preco_normal}", item.NormalPrice?.ToString("N2", Pt) ?? "")
            .Replace("{quantidade}", item.Quantity.ToString("0.##", Pt))
            .Replace("{validade}", item.End is { } end ? $"{end:dd/MM/yyyy}" : "")
            .Replace("{condicao}", condition)
            .Replace("{inicio}", item.Start is { } start ? $"{start:dd/MM/yyyy}" : "")
            .Replace("{inicio_curto}", item.Start is { } shortStart ? $"{shortStart:dd/MM}" : "")
            .Replace("{validade_curta}", item.End is { } shortEnd ? $"{shortEnd:dd/MM}" : "");
    }
}