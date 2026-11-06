using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Text.RegularExpressions;
using Placas.Application;
using Placas.Domain;

namespace Placas.Rendering;

public sealed class PlateRenderer
{
    public static double Dip(double mm) => mm * 96 / 25.4;
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    public Func<string, byte[]?>? ImageLoader { get; set; }
    private readonly Dictionary<string, BitmapSource> imageCache = [];
    public void ClearImageCache() => imageCache.Clear();

    private BitmapSource? LoadImage(string id, int decodeWidth)
    {
        var key = $"{id}@{decodeWidth}";
        if (imageCache.TryGetValue(key, out var cached)) return cached;
        var bytes = ImageLoader?.Invoke(id);
        if (bytes is not { Length: > 0 }) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth; 
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        if (imageCache.Count >= 24) imageCache.Clear();
        return imageCache[key] = image;
    }

    private Border? ImageElement(PlateElement element, PriceItem item, bool placeholders)
    {
        var source = element.ImageId;
        var fixedImage = !ImageSources.IsDynamic(source);
        var id = source switch
        {
            ImageSources.Family => item.FamilyImageId,
            ImageSources.ProductOrFamily => string.IsNullOrWhiteSpace(item.ImageId) ? item.FamilyImageId : item.ImageId,
            _ when fixedImage => source,
            _ => item.ImageId
        };
        var decodeWidth = Math.Clamp((int)Math.Ceiling(element.Width / 25.4 * 300), 64, ImageTools.MaxSide);
        var bitmap = string.IsNullOrWhiteSpace(id) ? null : LoadImage(id, decodeWidth);
        if (fixedImage && bitmap is null && !placeholders)
            throw new InvalidOperationException("Uma imagem fixa do layout não foi encontrada na biblioteca. Escolha outra imagem no layout.");
        UIElement? content = null;
        if (bitmap is not null)
        {
            var picture = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            content = picture;
        }
        else if (placeholders)
            content = new TextBlock
            {
                Text = fixedImage ? "Imagem não encontrada" : source switch
                {
                    ImageSources.Family => "Imagem da família",
                    ImageSources.ProductOrFamily => "Imagem do produto ou da família",
                    _ => "Imagem do produto"
                },
                Foreground = Brushes.Gray,
                FontStyle = FontStyles.Italic, FontSize = 11, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
        if (content is null) return null;
        var box = new Border
        {
            Width = Dip(element.Width), Height = Dip(element.Height),
            Background = Brush(element.Background), ClipToBounds = true, Child = content
        };
        if (element.RotationDegrees != 0)
        {
            box.RenderTransform = new RotateTransform(element.RotationDegrees);
            box.RenderTransformOrigin = new Point(.5, .5);
        }
        if (bitmap is null) { box.BorderBrush = Brushes.Gray; box.BorderThickness = new Thickness(1); }
        return box;
    }

    private static Geometry StarGeometry()
    {
        var points = Enumerable.Range(0, 10).Select(index =>
        {
            var angle = -Math.PI / 2 + index * Math.PI / 5;
            var radius = index % 2 == 0 ? .5 : .22;
            return new Point(.5 + Math.Cos(angle) * radius, .5 + Math.Sin(angle) * radius);
        }).ToArray();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Border ShapeElement(PlateElement element)
    {
        Geometry geometry = element.Shape switch
        {
            PlateShapes.Rectangle => new RectangleGeometry(new Rect(0, 0, 1, 1)),
            PlateShapes.Ellipse => new EllipseGeometry(new Point(.5, .5), .5, .5),
            PlateShapes.Line => Geometry.Parse("M 0,0.5 L 1,0.5"),
            PlateShapes.Triangle => Geometry.Parse("M 0.5,0 L 1,1 L 0,1 Z"),
            PlateShapes.Arrow => Geometry.Parse("M 0,0.35 L 0.65,0.35 L 0.65,0 L 1,0.5 L 0.65,1 L 0.65,0.65 L 0,0.65 Z"),
            PlateShapes.Star => StarGeometry(),
            _ => throw new InvalidOperationException("Forma não reconhecida.")
        };
        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = element.Shape == PlateShapes.Line ? Brushes.Transparent : Brush(element.Background),
            Stroke = Brush(element.Foreground),
            StrokeThickness = Dip(element.StrokeWidth),
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        var box = new Border
        {
            Width = Dip(element.Width), Height = Dip(element.Height),
            Background = Brushes.Transparent, Child = path
        };
        if (element.RotationDegrees != 0)
        {
            box.RenderTransform = new RotateTransform(element.RotationDegrees);
            box.RenderTransformOrigin = new Point(.5, .5);
        }
        return box;
    }

    private static void AddStyledRun(TextBlock text, PlateElement element, int sourceStart, string value)
    {
        if (value.Length == 0) return;
        var style = element.TextStyles.FirstOrDefault(candidate =>
            candidate.Start <= sourceStart && candidate.Start + candidate.Length > sourceStart);
        var run = new Run(value)
        {
            FontFamily = new FontFamily(style?.FontFamily ?? element.FontFamily),
            FontSize = style?.FontSize ?? element.FontSize,
            FontWeight = (style?.Bold ?? element.Bold) ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = (style?.Italic ?? element.Italic) ? FontStyles.Italic : FontStyles.Normal,
            Foreground = Brush(style?.Foreground ?? element.Foreground)
        };
        if (style?.Underline ?? element.Underline) run.TextDecorations = TextDecorations.Underline;
        text.Inlines.Add(run);
    }

    private static void AddStyledRange(TextBlock text, PlateElement element, string source, int start, int length)
    {
        var end = start + length;
        var cursor = start;
        while (cursor < end)
        {
            var style = element.TextStyles.FirstOrDefault(candidate =>
                candidate.Start <= cursor && candidate.Start + candidate.Length > cursor);
            var nextBoundary = end;
            foreach (var candidate in element.TextStyles)
            {
                if (candidate.Start > cursor) nextBoundary = Math.Min(nextBoundary, candidate.Start);
                if (candidate.Start + candidate.Length > cursor) nextBoundary = Math.Min(nextBoundary, candidate.Start + candidate.Length);
            }
            var segmentEnd = Math.Min(nextBoundary, end);
            AddStyledRun(text, element, cursor, source[cursor..segmentEnd]);
            cursor = segmentEnd;
        }
    }

    public Border? Element(PlateElement element, PriceItem item, bool placeholders = false)
    {
        if (element.IsImage) return ImageElement(element, item, placeholders);
        if (!string.IsNullOrEmpty(element.Shape)) return ShapeElement(element);
        var resolved = PlateRules.Resolve(element.Text, item);
        if (string.IsNullOrWhiteSpace(resolved) && element.Text.Contains('{'))
        {
            if (!placeholders) return null;
            resolved = element.Text;
        }
        var text = new TextBlock
        {
            Text = resolved, FontFamily = new FontFamily(string.IsNullOrWhiteSpace(element.FontFamily) ? "Segoe UI" : element.FontFamily),
            FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = element.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = Brush(element.Foreground),
            TextAlignment = element.TextAlign switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
            TextWrapping = TextWrapping.Wrap, Width = Dip(element.Width), FontSize = element.FontSize
        };
        if (element.TextStyles.Count > 0)
        {
            text.Text = "";
            var source = element.Text ?? "";
            var matches = Regex.Matches(source, @"\{[^{}]+\}");
            var cursor = 0;
            foreach (Match match in matches)
            {
                AddStyledRange(text, element, source, cursor, match.Index - cursor);
                var token = match.Value;
                var replacement = PlateRules.Resolve(token, item);
                if (placeholders && string.IsNullOrWhiteSpace(replacement)) replacement = token;
                AddStyledRun(text, element, match.Index, replacement);
                cursor = match.Index + match.Length;
            }
            AddStyledRange(text, element, source, cursor, source.Length - cursor);
            var runs = text.Inlines.OfType<Run>().ToList();
            var originalSizes = runs.Select(run => run.FontSize).ToArray();
            var scale = 1d;
            var minimumScale = Math.Min(1, element.MinFontSize / element.FontSize);
            while (true)
            {
                text.Measure(new Size(text.Width, double.PositiveInfinity));
                if (text.DesiredSize.Height <= Dip(element.Height) && text.DesiredSize.Width <= text.Width + .01 || scale <= minimumScale)
                    break;
                scale = Math.Max(minimumScale, scale - 1 / element.FontSize);
                for (var index = 0; index < runs.Count; index++) runs[index].FontSize = originalSizes[index] * scale;
            }
        }
        else
        {
            text.TextDecorations = element.Underline ? TextDecorations.Underline : null;
            while (true)
            {
                text.Measure(new Size(text.Width, double.PositiveInfinity));
                if (text.DesiredSize.Height <= Dip(element.Height) && text.DesiredSize.Width <= text.Width + .01) break;
                if (text.FontSize <= element.MinFontSize) break;
                text.FontSize = Math.Max(element.MinFontSize, text.FontSize - 1);
            }
        }
        text.VerticalAlignment = VerticalAlignment.Center;
        var textBox = new Border
        {
            Width = Dip(element.Width), Height = Dip(element.Height),
            Background = Brush(element.Background), Child = text
        };
        if (element.RotationDegrees != 0)
        {
            textBox.RenderTransform = new RotateTransform(element.RotationDegrees);
            textBox.RenderTransformOrigin = new Point(.5, .5);
        }
        return textBox;
    }

    public Canvas Plate(PlateTemplate template, PriceItem item, bool placeholders = false)
    {
        PlateRules.ValidateTemplate(template);
        var canvas = new Canvas { Width = Dip(template.WidthMm), Height = Dip(template.HeightMm), Background = Brush(template.Background), ClipToBounds = true };
        foreach (var element in template.Elements.OrderByDescending(x => x.IsImage))
        {
            var box = Element(element, item, placeholders);
            if (box is null) continue;
            Canvas.SetLeft(box, Dip(element.X)); Canvas.SetTop(box, Dip(element.Y)); canvas.Children.Add(box);
        }
        return canvas;
    }

    public FixedDocument Document(PrintBatch batch, double widthMm, double heightMm, double marginMm, double gapMm, bool cutMarks)
        => Document(batch, widthMm, heightMm, PageMargins.All(marginMm), gapMm, cutMarks);

    public FixedDocument Document(PrintBatch batch, double widthMm, double heightMm, PageMargins margins, double gapMm, bool cutMarks)
    {
        var (columns, rows) = Pagination.Grid(widthMm, heightMm, margins, gapMm, batch.Template);
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(Dip(widthMm), Dip(heightMm));
        var products = batch.Items.SelectMany(x => Enumerable.Repeat(x.Product, x.Copies)).ToArray();
        var capacity = columns * rows;
        for (var offset = 0; offset < products.Length; offset += capacity)
        {
            var page = new FixedPage { Width = Dip(widthMm), Height = Dip(heightMm), Background = Brushes.White };
            for (var index = 0; index < capacity && offset + index < products.Length; index++)
            {
                var plate = Plate(batch.Template, products[offset + index]);
                var x = Dip(margins.Left + index % columns * (batch.Template.WidthMm + gapMm));
                var y = Dip(margins.Top + index / columns * (batch.Template.HeightMm + gapMm));
                FixedPage.SetLeft(plate, x); FixedPage.SetTop(plate, y); page.Children.Add(plate);
                if (cutMarks)
                {
                    var outline = new Rectangle { Width = plate.Width, Height = plate.Height,
                        Stroke = Brushes.Gray, StrokeThickness = .5, StrokeDashArray = [3, 3], IsHitTestVisible = false };
                    FixedPage.SetLeft(outline, x); FixedPage.SetTop(outline, y); page.Children.Add(outline);
                }
            }
            var content = new PageContent { Child = page };
            document.Pages.Add(content);
        }
        return document;
    }
}