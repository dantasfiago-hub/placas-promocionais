using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Placas.Application;
using Placas.Domain;

namespace Placas.Rendering;

public sealed class PlateRenderer
{
    public static double Dip(double mm) => mm * 96 / 25.4;
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    public Canvas Plate(PlateTemplate template, PriceItem item)
    {
        PlateRules.ValidateTemplate(template);
        var canvas = new Canvas { Width = Dip(template.WidthMm), Height = Dip(template.HeightMm), Background = Brush(template.Background), ClipToBounds = true };
        foreach (var element in template.Elements)
        {
            var text = new TextBlock
            {
                Text = PlateRules.Resolve(element.Text, item), FontFamily = new FontFamily("Segoe UI"),
                FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brush(element.Foreground), TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = Dip(element.Width), FontSize = element.FontSize
            };
            while (true)
            {
                text.Measure(new Size(text.Width, double.PositiveInfinity));
                if (text.DesiredSize.Height <= Dip(element.Height) && text.DesiredSize.Width <= text.Width + .01) break;
                if (text.FontSize <= element.MinFontSize)
                    throw new InvalidOperationException($"Texto de {item.DisplayCode} não cabe no elemento '{element.Text}'. Aumente a área no modelo.");
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
    {
        var (columns, rows) = Pagination.Grid(widthMm, heightMm, marginMm, gapMm, batch.Template);
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
                var x = Dip(marginMm + index % columns * (batch.Template.WidthMm + gapMm));
                var y = Dip(marginMm + index / columns * (batch.Template.HeightMm + gapMm));
                FixedPage.SetLeft(plate, x); FixedPage.SetTop(plate, y); page.Children.Add(plate);
                if (cutMarks)
                {
                    // Dashed cutting outline stays inside the requested printable margins.
                    var outline = new Rectangle { Width = plate.Width, Height = plate.Height,
                        Stroke = Brushes.Gray, StrokeThickness = .5, StrokeDashArray = [3, 3], IsHitTestVisible = false };
                    FixedPage.SetLeft(outline, x); FixedPage.SetTop(outline, y); page.Children.Add(outline);
                }
            }
            var content = new PageContent(); ((System.Windows.Markup.IAddChild)content).AddChild(page); document.Pages.Add(content);
        }
        return document;
    }
}
