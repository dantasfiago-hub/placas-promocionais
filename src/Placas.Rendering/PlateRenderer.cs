using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
        if (bitmap is null) { box.BorderBrush = Brushes.Gray; box.BorderThickness = new Thickness(1); }
        return box;
    }

    public Canvas Plate(PlateTemplate template, PriceItem item, bool placeholders = false)
    {
        PlateRules.ValidateTemplate(template);
        var canvas = new Canvas { Width = Dip(template.WidthMm), Height = Dip(template.HeightMm), Background = Brush(template.Background), ClipToBounds = true };
        foreach (var element in template.Elements.OrderByDescending(x => x.IsImage))
        {
            if (element.IsImage)
            {
                var box = ImageElement(element, item, placeholders);
                if (box is null) continue;
                Canvas.SetLeft(box, Dip(element.X)); Canvas.SetTop(box, Dip(element.Y)); canvas.Children.Add(box);
                continue;
            }
            var resolved = PlateRules.Resolve(element.Text, item);
            if (string.IsNullOrWhiteSpace(resolved) && element.Text.Contains('{')) continue;
            var text = new TextBlock
            {
                Text = resolved, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = element.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = Brush(element.Foreground), TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = Dip(element.Width), FontSize = element.FontSize
            };
            while (true)
            {
                text.Measure(new Size(text.Width, double.PositiveInfinity));
                if (text.DesiredSize.Height <= Dip(element.Height) && text.DesiredSize.Width <= text.Width + .01) break;
                if (text.FontSize <= element.MinFontSize) break;   // antes: throw
                text.FontSize = Math.Max(element.MinFontSize, text.FontSize - 1);
            }
            var border = new Border
            {
                Width = Dip(element.Width), Height = Dip(element.Height),
                Background = Brush(element.Background), Child = text
            };
            text.VerticalAlignment = VerticalAlignment.Center;
            Canvas.SetLeft(border, Dip(element.X)); Canvas.SetTop(border, Dip(element.Y)); canvas.Children.Add(border);
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