#nullable enable

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Placas.Domain;
using Placas.Rendering;

namespace Placas.Desktop;

public sealed record LayerRow(PlateElement Element, string Icon, string Title);

public partial class MainWindow
{
    private const double MmToDip = 96 / 25.4;
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(150, 37, 99, 235));
    private static readonly Brush GuideBrush = new SolidColorBrush(Color.FromRgb(236, 72, 153));

    private readonly Dictionary<PlateElement, Border> designVisuals = [];
    private readonly Dictionary<PlateElement, Rectangle> designHits = [];
    private readonly Dictionary<PlateElement, Rectangle> selectionOutlines = [];
    private readonly Dictionary<string, Rectangle> handles = [];
    private readonly List<PlateElement> designSelectedElements = [];
    private readonly Line guideV = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line guideH = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly PriceItem designItem = Example();
    private Brush gridBrush = null!;
    private PlateElement? designSelected;
    private PlateElement? inspectorTextElement;
    private double zoom = 1;
    private bool fitMode = true;
    private bool syncing;
    private bool layersSync;
    private readonly List<string> undoStack = [];
    private readonly List<string> redoStack = [];
    private string? lastSnapshot;
    private DateTime lastCommitTime;

    private enum DragKind { None, Move, Resize }
    private DragKind dragKind;
    private string dragHandle = "";
    private Point dragStart;
    private (double X, double Y, double W, double H, double FontSize, double MinFontSize, List<PlateTextStyle> TextStyles) dragOrigin;
    private readonly Dictionary<PlateElement, (double X, double Y)> dragPositions = [];

    private static readonly Dictionary<string, (string Text, double W, double H, double Size, bool Bold)> FieldPresets = new()
    {
        ["descricao"] = ("{descricao}", 85, 22, 24, true),
        ["subdescricao"] = ("{subdescricao}", 85, 9, 12, false),
        ["preco"] = ("{preco}", 80, 30, 60, true),
        ["preco_normal"] = ("De {preco_normal}", 60, 8, 14, false),
        ["validade"] = ("Válido até {validade}", 85, 8, 10, false),
        ["inicio"] = ("A partir de {inicio}", 85, 8, 10, false),
        ["condicao"] = ("{condicao}", 85, 12, 13, false),
        ["quantidade"] = ("{quantidade} un.", 40, 10, 14, false),
        ["codigo"] = ("Cód. {codigo}", 60, 6, 9, false),
        ["familia"] = ("{familia}", 60, 8, 12, false),
        ["descricao_familia"] = ("{descricao_familia}", 85, 12, 18, true)
    };


    private void InitDesigner()
    {
        guideV.Stroke = GuideBrush; guideH.Stroke = GuideBrush;
        foreach (var name in new[] { "NW", "N", "NE", "E", "SE", "S", "SW", "W" })
        {
            var handle = new Rectangle
            {
                Fill = Brushes.White, Stroke = AccentBrush, Tag = name, Visibility = Visibility.Collapsed,
                Cursor = name switch
                {
                    "NW" or "SE" => Cursors.SizeNWSE,
                    "NE" or "SW" => Cursors.SizeNESW,
                    "N" or "S" => Cursors.SizeNS,
                    _ => Cursors.SizeWE
                }
            };
            handle.MouseLeftButtonDown += HandleDown;
            handles[name] = handle;
        }
        OverlayLayer.MouseMove += OverlayMouseMove;
        OverlayLayer.MouseLeftButtonUp += OverlayMouseUp;
        OverlayLayer.LostMouseCapture += (_, _) => EndDrag();
        gridBrush = BuildGridBrush();
    }

    private static Brush BuildGridBrush()
    {
        var cell = 5 * MmToDip;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(80, 100, 116, 139)), 0.5);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(cell, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, cell)));
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, cell, cell), ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }


    private static double ToDip(double mm) => PlateRenderer.Dip(mm);
    private static List<PlateElement> DrawOrder(PlateTemplate template) => template.Elements.OrderByDescending(x => x.IsImage).ToList();

    private static Brush? SafeBrush(string? color)
    {
        try { return string.IsNullOrWhiteSpace(color) ? null : (Brush?)new BrushConverter().ConvertFromString(color); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return null; }
    }

    private void BindDesigner()
    {
        designSelected = null;
        designSelectedElements.Clear();
        RebuildDesigner();
        FillInspector();
        fitMode = true;
        ZoomFit();
        ResetHistory();
                 
    }

    private void RebuildDesigner()
    {
        EndDrag();
        PlateLayer.Children.Clear(); OverlayLayer.Children.Clear();
        designVisuals.Clear(); designHits.Clear(); selectionOutlines.Clear();
        if (editing is null)
        {
            PlateLayer.Width = PlateLayer.Height = OverlayLayer.Width = OverlayLayer.Height = 0;
            designSelected = null; designSelectedElements.Clear();
            UpdateSelectionVisuals(); RefreshLayers();
            RenderRulers();
            return;
        }
        PlateLayer.Width = OverlayLayer.Width = ToDip(editing.WidthMm);
        PlateLayer.Height = OverlayLayer.Height = ToDip(editing.HeightMm);
        designSelectedElements.RemoveAll(x => !editing.Elements.Contains(x));
        if (designSelected is not null && !designSelectedElements.Contains(designSelected))
            designSelected = designSelectedElements.LastOrDefault();
        foreach (var element in DrawOrder(editing))
        {
            var hit = new Rectangle { Fill = Brushes.Transparent, Tag = element, Cursor = Cursors.SizeAll };
            hit.MouseLeftButtonDown += ElementHitDown;
            hit.MouseEnter += HitEnter;
            hit.MouseLeave += HitLeave;
            OverlayLayer.Children.Add(hit);
            designHits[element] = hit;
            PositionElement(element);
            var outline = new Rectangle
            {
                Fill = Brushes.Transparent, Stroke = AccentBrush, Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            OverlayLayer.Children.Add(outline);
            selectionOutlines[element] = outline;
        }
        foreach (var handle in handles.Values) OverlayLayer.Children.Add(handle);
        OverlayLayer.Children.Add(guideV); OverlayLayer.Children.Add(guideH);
        RebuildVisuals();
        UpdateSelectionVisuals();
        RefreshLayers();
        RenderRulers();
    }

    private void RebuildVisuals()
    {
        if (editing is null) return;
        PlateLayer.Children.Clear(); designVisuals.Clear();
        PlateLayer.Background = SafeBrush(editing.Background) ?? Brushes.White;
        foreach (var element in DrawOrder(editing))
        {
            var visual = BuildVisual(element);
            if (visual is null) continue;
            Canvas.SetLeft(visual, ToDip(element.X)); Canvas.SetTop(visual, ToDip(element.Y));
            PlateLayer.Children.Add(visual); designVisuals[element] = visual;
        }
        if (GridCheck.IsChecked == true)
            PlateLayer.Children.Add(new Rectangle { Width = PlateLayer.Width, Height = PlateLayer.Height, Fill = gridBrush, IsHitTestVisible = false });
    }

    private Border? BuildVisual(PlateElement element)
    {
        try { return renderer.Element(element, designItem, placeholders: true); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
        {
            PreviewStatus.Text = "Prévia: " + ex.Message;
            return null;
        }
    }

    private void RefreshVisual(PlateElement element)
    {
        if (editing is null) return;
        if (!designVisuals.TryGetValue(element, out var old)) { RebuildVisuals(); return; }
        var index = PlateLayer.Children.IndexOf(old);
        PlateLayer.Children.RemoveAt(index);
        designVisuals.Remove(element);
        var visual = BuildVisual(element);
        if (visual is null) return;
        Canvas.SetLeft(visual, ToDip(element.X)); Canvas.SetTop(visual, ToDip(element.Y));
        PlateLayer.Children.Insert(index, visual);
        designVisuals[element] = visual;
    }

    private void PositionElement(PlateElement element)
    {
        if (designVisuals.TryGetValue(element, out var visual))
        {
            Canvas.SetLeft(visual, ToDip(element.X)); Canvas.SetTop(visual, ToDip(element.Y));
        }
        if (designHits.TryGetValue(element, out var hit))
        {
            hit.Width = ToDip(element.Width); hit.Height = ToDip(element.Height);
            Canvas.SetLeft(hit, ToDip(element.X)); Canvas.SetTop(hit, ToDip(element.Y));
            hit.RenderTransform = element.RotationDegrees == 0 ? Transform.Identity : new RotateTransform(element.RotationDegrees);
            hit.RenderTransformOrigin = new Point(.5, .5);
        }
        if (designVisuals.TryGetValue(element, out var rotatedVisual))
        {
            rotatedVisual.RenderTransform = element.RotationDegrees == 0 ? Transform.Identity : new RotateTransform(element.RotationDegrees);
            rotatedVisual.RenderTransformOrigin = new Point(.5, .5);
        }
    }


    private void SelectElement(PlateElement? element)
    {
        designSelected = element;
        designSelectedElements.Clear();
        if (element is not null) designSelectedElements.Add(element);
        UpdateSelectionVisuals();
        FillInspector();
        SyncLayersSelection();
    }

    private void SelectElements(IEnumerable<PlateElement> elements, PlateElement? primary = null)
    {
        designSelectedElements.Clear();
        if (editing is not null)
            designSelectedElements.AddRange(elements.Where(editing.Elements.Contains).Distinct());
        designSelected = primary is not null && designSelectedElements.Contains(primary)
            ? primary
            : designSelectedElements.LastOrDefault();
        UpdateSelectionVisuals();
        FillInspector();
        SyncLayersSelection();
    }

    private void UpdateSelectionVisuals()
    {
        if (editing is null || designSelectedElements.Count == 0)
        {
            foreach (var outline in selectionOutlines.Values) outline.Visibility = Visibility.Collapsed;
            foreach (var handle in handles.Values) handle.Visibility = Visibility.Collapsed;
            SelectionReadout.Text = editing is null ? "" : $"Placa {Fmt(editing.WidthMm)} × {Fmt(editing.HeightMm)} mm";
            return;
        }
        foreach (var element in designSelectedElements)
        {
            if (!selectionOutlines.TryGetValue(element, out var outline)) continue;
            outline.Width = ToDip(element.Width); outline.Height = ToDip(element.Height);
            outline.StrokeThickness = 1.5 / zoom;
            Canvas.SetLeft(outline, ToDip(element.X)); Canvas.SetTop(outline, ToDip(element.Y));
            outline.RenderTransform = Transform.Identity;
            outline.Visibility = Visibility.Visible;
        }
        foreach (var (element, outline) in selectionOutlines)
            if (!designSelectedElements.Contains(element)) outline.Visibility = Visibility.Collapsed;
        if (designSelectedElements.Count != 1)
        {
            foreach (var handle in handles.Values) handle.Visibility = Visibility.Collapsed;
            SelectionReadout.Text = $"{designSelectedElements.Count} elementos selecionados";
            return;
        }
        var selectedElement = designSelected!;
        double x = ToDip(selectedElement.X), y = ToDip(selectedElement.Y), w = ToDip(selectedElement.Width), h = ToDip(selectedElement.Height);
        var size = 9 / zoom;
        foreach (var (name, handle) in handles)
        {
            var cx = name.Contains('W') ? x : name.Contains('E') ? x + w : x + w / 2;
            var cy = name.Contains('N') ? y : name.Contains('S') ? y + h : y + h / 2;
            handle.Width = handle.Height = size;
            handle.StrokeThickness = 1.2 / zoom;
            Canvas.SetLeft(handle, cx - size / 2); Canvas.SetTop(handle, cy - size / 2);
            handle.Visibility = Visibility.Visible;
        }
        SelectionReadout.Text = $"X {Fmt(selectedElement.X)}  Y {Fmt(selectedElement.Y)}  •  {Fmt(selectedElement.Width)} × {Fmt(selectedElement.Height)} mm  •  {Fmt(selectedElement.RotationDegrees)}°";
    }

    private void HitEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Rectangle { Tag: PlateElement hovered } rectangle || dragKind != DragKind.None || designSelectedElements.Contains(hovered)) return;
        rectangle.Stroke = HoverBrush; rectangle.StrokeThickness = 1 / zoom;
    }
    private void HitLeave(object sender, MouseEventArgs e)
    {
        if (sender is Rectangle rectangle) rectangle.Stroke = null;
    }

    private void ElementHitDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: PlateElement element } rectangle) return;
        e.Handled = true;
        rectangle.Stroke = null;
        DesignScroll.Focus();
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (control)
        {
            if (designSelectedElements.Contains(element))
            {
                var remaining = designSelectedElements.Where(x => !ReferenceEquals(x, element)).ToList();
                SelectElements(remaining, remaining.LastOrDefault());
                return;
            }
            SelectElements(designSelectedElements.Append(element), element);
        }
        else if (shift)
            SelectElements(designSelectedElements.Append(element), element);
        else if (!designSelectedElements.Contains(element))
            SelectElement(element);
        else
            SelectElements(designSelectedElements, element);
        BeginDrag(DragKind.Move, "", e);
    }

    private void HandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: string name } || designSelected is null || designSelectedElements.Count != 1) return;
        e.Handled = true;
        DesignScroll.Focus();
        BeginDrag(DragKind.Resize, name, e);
    }

    private void OverlayBackgroundDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, OverlayLayer)) return;
        DesignScroll.Focus();
        SelectElement(null);
    }


    private void BeginDrag(DragKind kind, string handle, MouseButtonEventArgs e)
    {
        if (designSelected is null) return;
        dragKind = kind; dragHandle = handle;
        dragStart = e.GetPosition(OverlayLayer);
        dragOrigin = (designSelected.X, designSelected.Y, designSelected.Width, designSelected.Height,
            designSelected.FontSize, designSelected.MinFontSize, designSelected.TextStyles.Select(PlateTextFormatting.Clone).ToList());
        dragPositions.Clear();
        IEnumerable<PlateElement> targets = kind == DragKind.Move ? designSelectedElements : new[] { designSelected };
        foreach (var element in targets)
            dragPositions[element] = (element.X, element.Y);
        OverlayLayer.CaptureMouse();
    }

    private void EndDrag()
    {
        if (dragKind == DragKind.None) return;
        dragKind = DragKind.None;
        guideV.Visibility = Visibility.Collapsed; guideH.Visibility = Visibility.Collapsed;
        if (OverlayLayer.IsMouseCaptured) OverlayLayer.ReleaseMouseCapture();
    }

    private void OverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (dragKind == DragKind.None || designSelected is null || editing is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var point = e.GetPosition(OverlayLayer);
        if (dragKind == DragKind.Move) ApplyMove(point); else ApplyResize(point);
    }

    private void OverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragKind == DragKind.None) return;
        var wasResizing = dragKind == DragKind.Resize;
        EndDrag();
        if (designSelected is not null)
        {
            if (wasResizing) RefreshVisual(designSelected);
            FillInspector();
        }
        CommitHistory();
    }

    private void ApplyMove(Point point)
    {
        var element = designSelected!; var o = dragOrigin;
        var dx = (point.X - dragStart.X) / MmToDip; var dy = (point.Y - dragStart.Y) / MmToDip;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0; }
        double x = o.X + dx, y = o.Y + dy;
        HideGuides();
        if (SnapCheck.IsChecked == true)
        {
            var threshold = SnapThreshold();
            if (BestSnap(new[] { x, x + o.W / 2, x + o.W }, VerticalLines(), threshold, out var vLine) is { } deltaX)
            {
                x += deltaX; ShowGuideV(vLine);
            }
            if (BestSnap(new[] { y, y + o.H / 2, y + o.H }, HorizontalLines(), threshold, out var hLine) is { } deltaY)
            {
                y += deltaY; ShowGuideH(hLine);
            }
        }
        dx = x - o.X; dy = y - o.Y;
        foreach (var selected in designSelectedElements)
        {
            var origin = dragPositions[selected];
            selected.X = Math.Round(origin.X + dx, 2); selected.Y = Math.Round(origin.Y + dy, 2);
            PositionElement(selected);
        }
        UpdateSelectionVisuals();
        if (designSelectedElements.Count == 1) UpdatePositionFields();
    }

    private void ApplyResize(Point point)
    {
        var element = designSelected!; var o = dragOrigin;
        var dx = (point.X - dragStart.X) / MmToDip; var dy = (point.Y - dragStart.Y) / MmToDip;
        double left = o.X, top = o.Y, right = o.X + o.W, bottom = o.Y + o.H;
        var west = dragHandle.Contains('W'); var east = dragHandle.Contains('E');
        var north = dragHandle.Contains('N'); var south = dragHandle.Contains('S');
        if (west) left += dx;
        if (east) right += dx;
        if (north) top += dy;
        if (south) bottom += dy;
        HideGuides();
        if (SnapCheck.IsChecked == true)
        {
            var threshold = SnapThreshold();
            var vLines = VerticalLines(); var hLines = HorizontalLines();
            if (west && BestSnap(new[] { left }, vLines, threshold, out var lineW) is { } snapW) { left += snapW; ShowGuideV(lineW); }
            if (east && BestSnap(new[] { right }, vLines, threshold, out var lineE) is { } snapE) { right += snapE; ShowGuideV(lineE); }
            if (north && BestSnap(new[] { top }, hLines, threshold, out var lineN) is { } snapN) { top += snapN; ShowGuideH(lineN); }
            if (south && BestSnap(new[] { bottom }, hLines, threshold, out var lineS) is { } snapS) { bottom += snapS; ShowGuideH(lineS); }
        }
        const double min = 1;
        if (right - left < min) { if (west) left = right - min; else right = left + min; }
        if (bottom - top < min) { if (north) top = bottom - min; else bottom = top + min; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && dragHandle.Length == 2 && o.W > 0 && o.H > 0)
        {
            var ratio = o.W / o.H; var newW = right - left; var newH = bottom - top;
            if (Math.Abs(newW - o.W) / o.W >= Math.Abs(newH - o.H) / o.H) newH = Math.Max(min, newW / ratio);
            else newW = Math.Max(min, newH * ratio);
            if (west) left = right - newW; else right = left + newW;
            if (north) top = bottom - newH; else bottom = top + newH;
        }
        element.X = Math.Round(left, 2); element.Y = Math.Round(top, 2);
        element.Width = Math.Max(min, Math.Round(right - left, 2));
        element.Height = Math.Max(min, Math.Round(bottom - top, 2));
        if (!element.IsImage)
        {
            var scaleX = element.Width / o.W;
            var scaleY = element.Height / o.H;
            var fontScale = west || east
                ? north || south ? Math.Sqrt(scaleX * scaleY) : scaleX
                : north || south ? scaleY : 1;
            element.FontSize = Math.Max(0.1, Math.Round(o.FontSize * fontScale, 2));
            element.MinFontSize = Math.Max(0.1, Math.Round(o.MinFontSize * fontScale, 2));
            element.TextStyles = dragOrigin.TextStyles.Select(style =>
            {
                var resized = PlateTextFormatting.Clone(style);
                if (resized.FontSize is { } size) resized.FontSize = Math.Max(0.1, Math.Round(size * fontScale, 2));
                return resized;
            }).ToList();
        }
        RefreshVisual(element);
        PositionElement(element);
        UpdateSelectionVisuals();
        UpdatePositionFields();
    }


    private double SnapThreshold() => 7 / zoom / MmToDip; 

    private List<double> VerticalLines()
    {
        var lines = new List<double> { 0, editing!.WidthMm / 2, editing.WidthMm };
        foreach (var other in editing.Elements.Where(x => !designSelectedElements.Contains(x)))
        { lines.Add(other.X); lines.Add(other.X + other.Width / 2); lines.Add(other.X + other.Width); }
        return lines;
    }
    private List<double> HorizontalLines()
    {
        var lines = new List<double> { 0, editing!.HeightMm / 2, editing.HeightMm };
        foreach (var other in editing.Elements.Where(x => !designSelectedElements.Contains(x)))
        { lines.Add(other.Y); lines.Add(other.Y + other.Height / 2); lines.Add(other.Y + other.Height); }
        return lines;
    }
    private static double? BestSnap(double[] candidates, IEnumerable<double> lines, double threshold, out double line)
    {
        double? best = null; double bestDistance = double.MaxValue; line = 0;
        foreach (var candidate in candidates)
            foreach (var target in lines)
            {
                var delta = target - candidate;
                if (Math.Abs(delta) <= threshold && Math.Abs(delta) < bestDistance)
                { bestDistance = Math.Abs(delta); best = delta; line = target; }
            }
        return best;
    }
    private void ShowGuideV(double mm)
    {
        var x = ToDip(mm);
        guideV.X1 = guideV.X2 = x; guideV.Y1 = 0; guideV.Y2 = OverlayLayer.Height;
        guideV.StrokeThickness = 1 / zoom; guideV.Visibility = Visibility.Visible;
    }
    private void ShowGuideH(double mm)
    {
        var y = ToDip(mm);
        guideH.Y1 = guideH.Y2 = y; guideH.X1 = 0; guideH.X2 = OverlayLayer.Width;
        guideH.StrokeThickness = 1 / zoom; guideH.Visibility = Visibility.Visible;
    }
    private void HideGuides() { guideV.Visibility = Visibility.Collapsed; guideH.Visibility = Visibility.Collapsed; }


    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, 0.1, 8);
        ZoomTransform.ScaleX = zoom; ZoomTransform.ScaleY = zoom;
        ZoomText.Text = $"{zoom * 100:0}%";
        UpdateSelectionVisuals();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderRulers));
    }

    private static double NiceRulerStep(double minimum)
    {
        if (minimum <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
        var normalized = minimum / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }

    private static void AddRulerLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush brush)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = .75 };
        canvas.Children.Add(line);
    }

    private void RenderRulers()
    {
        if (!RulerHost.IsLoaded) return;
        HorizontalRuler.Children.Clear();
        VerticalRuler.Children.Clear();
        if (editing is null || zoom <= 0 || HorizontalRuler.ActualWidth <= 0 || VerticalRuler.ActualHeight <= 0) return;

        var plateOrigin = PlateLayer.TranslatePoint(new Point(0, 0), RulerHost);
        var pixelsPerMm = MmToDip * zoom;
        var horizontalOrigin = plateOrigin.X - 22;
        var verticalOrigin = plateOrigin.Y - 22;
        var majorStep = NiceRulerStep(42 / pixelsPerMm);
        var minorStep = majorStep / 5;
        var rulerBrush = new SolidColorBrush(Color.FromRgb(107, 119, 133));
        var firstX = Math.Max(0, Math.Ceiling(-horizontalOrigin / pixelsPerMm / minorStep));
        var lastX = Math.Min(Math.Ceiling(editing.WidthMm / minorStep), Math.Floor((HorizontalRuler.ActualWidth - horizontalOrigin) / pixelsPerMm / minorStep));
        for (var index = firstX; index <= lastX; index++)
        {
            var mm = index * minorStep;
            var x = horizontalOrigin + mm * pixelsPerMm;
            var major = Math.Abs(mm / majorStep - Math.Round(mm / majorStep)) < .0001;
            AddRulerLine(HorizontalRuler, x, 22, x, major ? 12 : 17, rulerBrush);
            if (major)
            {
                var label = new TextBlock { Text = Fmt(mm), FontSize = 8, Foreground = rulerBrush };
                Canvas.SetLeft(label, x + 2); Canvas.SetTop(label, 1);
                HorizontalRuler.Children.Add(label);
            }
        }

        var firstY = Math.Max(0, Math.Ceiling(-verticalOrigin / pixelsPerMm / minorStep));
        var lastY = Math.Min(Math.Ceiling(editing.HeightMm / minorStep), Math.Floor((VerticalRuler.ActualHeight - verticalOrigin) / pixelsPerMm / minorStep));
        for (var index = firstY; index <= lastY; index++)
        {
            var mm = index * minorStep;
            var y = verticalOrigin + mm * pixelsPerMm;
            var major = Math.Abs(mm / majorStep - Math.Round(mm / majorStep)) < .0001;
            AddRulerLine(VerticalRuler, 22, y, major ? 12 : 17, y, rulerBrush);
            if (major)
            {
                var label = new TextBlock
                {
                    Text = Fmt(mm), Width = 28, Height = 12, FontSize = 8,
                    Foreground = rulerBrush, TextAlignment = TextAlignment.Center,
                    RenderTransform = new RotateTransform(-90), RenderTransformOrigin = new Point(.5, .5)
                };
                Canvas.SetLeft(label, -3); Canvas.SetTop(label, y - 6);
                VerticalRuler.Children.Add(label);
            }
        }
    }

    private void ZoomFit()
    {
        if (editing is null || DesignScroll.ActualWidth < 200 || DesignScroll.ActualHeight < 200) return;
        double width = ToDip(editing.WidthMm), height = ToDip(editing.HeightMm);
        if (width <= 0 || height <= 0) return;
        fitMode = true;
        SetZoom(Math.Min((DesignScroll.ActualWidth - 130) / width, (DesignScroll.ActualHeight - 130) / height));
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) { fitMode = false; SetZoom(zoom * 1.25); }
    private void ZoomOutClick(object sender, RoutedEventArgs e) { fitMode = false; SetZoom(zoom / 1.25); }
    private void ZoomFitClick(object sender, RoutedEventArgs e) => ZoomFit();
    private void Zoom100Click(object sender, RoutedEventArgs e) { fitMode = false; SetZoom(1); }
    private void DesignScrollSizeChanged(object sender, SizeChangedEventArgs e) { if (fitMode) ZoomFit(); }
    private void DesignScrollChanged(object sender, ScrollChangedEventArgs e) => RenderRulers();
    private void DesignerLoaded(object sender, RoutedEventArgs e) => RenderRulers();
    private void GridToggled(object sender, RoutedEventArgs e) => RebuildVisuals();
    private void DesignWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        fitMode = false;
        SetZoom(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }


    private void DesignKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Z)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Redo(); else Undo();
            e.Handled = true; return;
        }
        if (control && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (designSelected is null) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 0.5;
        switch (e.Key)
        {
            case Key.Delete: DeleteSelectedElement(); break;
            case Key.Escape: SelectElement(null); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            case Key.D when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): DuplicateElementClick(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Nudge(double dx, double dy)
    {
        if (designSelected is null) return;
        foreach (var element in designSelectedElements)
        {
            element.X = Math.Round(element.X + dx, 2);
            element.Y = Math.Round(element.Y + dy, 2);
            PositionElement(element);
        }
        UpdateSelectionVisuals();
        if (designSelectedElements.Count == 1) UpdatePositionFields();
        CommitHistory(coalesce: true);
    }


    private void AddElement(PlateElement element, bool focusText = false)
    {
        RequireEditingTemplate();
        editing!.Elements.Add(element);
        RebuildDesigner();
        SelectElement(element);
        CommitHistory();
        if (focusText) { TextContentBox.Focus(); TextContentBox.SelectAll(); }
        PreviewStatus.Text = "Elemento adicionado: arraste para posicionar e use as alças para ajustar o tamanho.";
    }

    private void OpenToolMenu(object sender)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Right;
        button.ContextMenu.IsOpen = true;
    }
    private void AddFieldElementClick(object sender, RoutedEventArgs e) => OpenToolMenu(sender);

    private void AddTextElementClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireEditingTemplate();
        var width = Math.Max(10, Math.Min(60, editing!.WidthMm - 10));
        AddElement(new PlateElement { Text = "Novo texto", X = 5, Y = 5, Width = width, Height = 15, FontSize = 15 }, focusText: true);
    });

    private void AddShapeElementClick(object sender, RoutedEventArgs e) => OpenToolMenu(sender);

    private void AddShapeItemClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is not MenuItem { Tag: string shape } || !PlateShapes.IsShape(shape)) return;
        RequireEditingTemplate();
        var line = shape == PlateShapes.Line;
        var width = Math.Max(1, Math.Min(line ? 45 : 35, editing!.WidthMm - 5));
        var height = Math.Max(1, Math.Min(line ? 2 : 25, editing.HeightMm - 5));
        AddElement(new PlateElement
        {
            Text = "", Shape = shape, X = Math.Max(0, Math.Min(5, editing.WidthMm - width)),
            Y = Math.Max(0, Math.Min(5, editing.HeightMm - height)), Width = width, Height = height,
            Background = "#00FFFFFF", Foreground = "#111827", StrokeWidth = 0.8
        });
    });

    private void AddFieldItemClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is not MenuItem { Tag: string key } || !FieldPresets.TryGetValue(key, out var preset)) return;
        RequireEditingTemplate();
        var width = Math.Min(preset.W, editing!.WidthMm);
        AddElement(new PlateElement
        {
            Text = preset.Text, X = Math.Round((editing.WidthMm - width) / 2, 1), Y = 5,
            Width = width, Height = preset.H, FontSize = preset.Size, Bold = preset.Bold
        });
    });

    private void DuplicateElementClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (designSelected is null || editing is null) throw new InvalidOperationException("Selecione um elemento para duplicar.");
        var copies = designSelectedElements.Select(element =>
        {
            var copy = JsonSerializer.Deserialize<PlateElement>(JsonSerializer.Serialize(element))!;
            copy.X = Math.Round(copy.X + 3, 2); copy.Y = Math.Round(copy.Y + 3, 2);
            return copy;
        }).ToList();
        foreach (var original in designSelectedElements)
            editing.Elements.Insert(editing.Elements.IndexOf(original) + 1, copies[designSelectedElements.IndexOf(original)]);
        RebuildDesigner();
        SelectElements(copies, copies.Last());
        CommitHistory();
    });

    private void DeleteElementClick(object sender, RoutedEventArgs e) => Run(DeleteSelectedElement);
    private void DeleteSelectedElement()
    {
        if (designSelected is null || editing is null) return;
        foreach (var element in designSelectedElements.ToList()) editing.Elements.Remove(element);
        designSelected = null; designSelectedElements.Clear();
        RebuildDesigner();
        SelectElement(null);
        CommitHistory();
    }


    private void ResetHistory()
    {
        undoStack.Clear(); redoStack.Clear();
        lastSnapshot = editing is null ? null : JsonSerializer.Serialize(editing);
        UpdateHistoryButtons();
    }

    private void CommitHistory(bool coalesce = false)
    {
        if (editing is null) return;
        var now = JsonSerializer.Serialize(editing);
        if (now == lastSnapshot) return;
        var merge = coalesce && DateTime.UtcNow - lastCommitTime < TimeSpan.FromMilliseconds(800);
        if (lastSnapshot is not null && !merge)
        {
            undoStack.Add(lastSnapshot);
            if (undoStack.Count > 100) undoStack.RemoveAt(0);
        }
        redoStack.Clear();
        lastSnapshot = now; lastCommitTime = DateTime.UtcNow;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = undoStack.Count > 0;
        RedoButton.IsEnabled = redoStack.Count > 0;
    }

    private void Undo()
    {
        if (editing is null || undoStack.Count == 0) return;
        redoStack.Add(JsonSerializer.Serialize(editing));
        var previous = undoStack[^1]; undoStack.RemoveAt(undoStack.Count - 1);
        RestoreSnapshot(previous);
    }
    private void Redo()
    {
        if (editing is null || redoStack.Count == 0) return;
        undoStack.Add(JsonSerializer.Serialize(editing));
        var next = redoStack[^1]; redoStack.RemoveAt(redoStack.Count - 1);
        RestoreSnapshot(next);
    }
    private void RestoreSnapshot(string json)
    {
        List<int> selectedIndices = editing is null
            ? []
            : designSelectedElements.Select(element => editing.Elements.IndexOf(element)).Where(index => index >= 0).ToList();
        var primaryIndex = designSelected is null || editing is null ? -1 : editing.Elements.IndexOf(designSelected);
        editing = JsonSerializer.Deserialize<PlateTemplate>(json)!;
        lastSnapshot = json; lastCommitTime = DateTime.MinValue;
        designSelectedElements.Clear();
        designSelectedElements.AddRange(selectedIndices.Where(index => index < editing.Elements.Count).Select(index => editing.Elements[index]));
        designSelected = primaryIndex >= 0 && primaryIndex < editing.Elements.Count
            ? editing.Elements[primaryIndex]
            : designSelectedElements.LastOrDefault();
        RebuildDesigner();
        FillInspector();
        SyncLayersSelection();
        if (fitMode) ZoomFit();
        UpdateHistoryButtons();
    }
    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Redo();


    private static string LayerTitle(PlateElement element, IReadOnlyList<ImageChoice> choices)
    {
        if (!string.IsNullOrEmpty(element.Shape))
            return element.Shape switch
            {
                PlateShapes.Rectangle => "Forma: Retângulo",
                PlateShapes.Ellipse => "Forma: Elipse",
                PlateShapes.Line => "Forma: Linha",
                PlateShapes.Triangle => "Forma: Triângulo",
                PlateShapes.Arrow => "Forma: Seta",
                PlateShapes.Star => "Forma: Estrela",
                _ => "Forma"
            };
        if (!element.IsImage)
        {
            var text = element.Text.Trim();
            return text.Length == 0 ? "(texto vazio)" : text.Length > 30 ? text[..30] + "…" : text;
        }
        return element.ImageId switch
        {
            ImageSources.Product => "Imagem do produto",
            ImageSources.Family => "Imagem da família",
            ImageSources.ProductOrFamily => "Imagem do produto ou da família",
            _ => "Imagem: " + (choices.FirstOrDefault(c => c.Id == element.ImageId)?.Name ?? "(não encontrada)")
        };
    }

    private void RefreshLayers()
    {
        layersSync = true;
        try
        {
            var order = editing is null ? new List<PlateElement>() : DrawOrder(editing);
            order.Reverse();
            var rows = order.Select(x => new LayerRow(x, x.IsImage ? "▣" : !string.IsNullOrEmpty(x.Shape) ? "◇" : "T", LayerTitle(x, imageChoices))).ToList();
            LayersList.ItemsSource = rows;
            foreach (var row in rows.Where(row => designSelectedElements.Contains(row.Element)))
                LayersList.SelectedItems.Add(row);
        }
        finally { layersSync = false; }
    }

    private void SyncLayersSelection()
    {
        layersSync = true;
        try
        {
            LayersList.SelectedItems.Clear();
            var rows = LayersList.ItemsSource as List<LayerRow>;
            foreach (var row in rows?.Where(row => designSelectedElements.Contains(row.Element)) ?? [])
                LayersList.SelectedItems.Add(row);
        }
        finally { layersSync = false; }
    }

    private void LayersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (layersSync) return;
        var rows = LayersList.SelectedItems.OfType<LayerRow>().ToList();
        var primary = LayersList.SelectedItem is LayerRow selected && rows.Contains(selected)
            ? selected.Element
            : rows.LastOrDefault()?.Element;
        SelectElements(rows.Select(row => row.Element), primary);
    }

    private void LayerForwardClick(object sender, RoutedEventArgs e) => MoveLayer(+1);
    private void LayerBackwardClick(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void MoveLayer(int direction)
    {
        if (designSelected is null || editing is null) return;
        var list = editing.Elements;
        var selected = designSelectedElements.OrderBy(list.IndexOf).ToList();
        var changed = false;
        if (direction > 0)
        {
            foreach (var element in selected.AsEnumerable().Reverse())
            {
                var from = list.IndexOf(element);
                var to = from + 1;
                while (to < list.Count && list[to].IsImage != element.IsImage) to++;
                if (to >= list.Count) continue;
                (list[from], list[to]) = (list[to], list[from]);
                changed = true;
            }
        }
        else
        {
            foreach (var element in selected)
            {
                var from = list.IndexOf(element);
                var to = from - 1;
                while (to >= 0 && list[to].IsImage != element.IsImage) to--;
                if (to < 0) continue;
                (list[from], list[to]) = (list[to], list[from]);
                changed = true;
            }
        }
        if (!changed) return;
        RebuildDesigner();
        SelectElements(selected, designSelected);
        CommitHistory();
    }


    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static bool TryNumber(string? text, out double value)
        => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string? NormalizeColor(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.StartsWith('#')) text = "#" + text;
        return Regex.IsMatch(text, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$") ? text.ToUpperInvariant() : null;
    }

    private void SetSwatch(Button button, TextBox box, string? color)
    {
        button.Background = SafeBrush(color) ?? Brushes.Transparent;
        box.Text = color ?? "";
    }

    private void RefreshInspectorImageChoices()
    {
        syncing = true;
        try { ImageSourceBox.ItemsSource = imageChoices; }
        finally { syncing = false; }
        if (designSelected?.IsImage == true) FillInspector();
        RefreshLayers();
    }

    private void FillInspector()
    {
        syncing = true;
        try
        {
            var element = designSelected;
            var multi = designSelectedElements.Count > 1;
            PlatePanel.Visibility = element is null && !multi ? Visibility.Visible : Visibility.Collapsed;
            ElementPanel.Visibility = element is null || multi ? Visibility.Collapsed : Visibility.Visible;
            if (element is null || multi || element.IsImage || !string.IsNullOrEmpty(element.Shape))
                inspectorTextElement = null;
            InspectorTitle.Text = multi ? $"{designSelectedElements.Count} elementos" : element is null ? "Placa"
                : element.IsImage ? "Imagem" : !string.IsNullOrEmpty(element.Shape) ? "Forma" : "Texto";
            if (multi) return;
            if (element is null)
            {
                PlateNameBox.IsEnabled = PlateWidthBox.IsEnabled = PlateHeightBox.IsEnabled = editing is not null;
                PlateNameBox.Text = editing?.Name ?? "";
                PlateWidthBox.Text = editing is null ? "" : Fmt(editing.WidthMm);
                PlateHeightBox.Text = editing is null ? "" : Fmt(editing.HeightMm);
                SetSwatch(PlateColorButton, PlateColorBox, editing?.Background);
                return;
            }
            PosXBox.Text = Fmt(element.X); PosYBox.Text = Fmt(element.Y);
            RotationBox.Text = Fmt(element.RotationDegrees);
            var isShape = !string.IsNullOrEmpty(element.Shape);
            TextPanel.Visibility = element.IsImage || isShape ? Visibility.Collapsed : Visibility.Visible;
            ImagePanel.Visibility = element.IsImage ? Visibility.Visible : Visibility.Collapsed;
            ShapePanel.Visibility = isShape ? Visibility.Visible : Visibility.Collapsed;
            if (element.IsImage)
                ImageSourceBox.SelectedValue = string.IsNullOrWhiteSpace(element.ImageId) ? ImageSources.Product : element.ImageId;
            else if (!isShape)
            {
                if (!ReferenceEquals(inspectorTextElement, element) || TextContentBox.Text != element.Text)
                {
                    TextContentBox.Text = element.Text;
                    TextContentBox.Select(0, 0);
                    inspectorTextElement = element;
                }
                UpdateTextHint();
                UpdateTextFormattingControls();
                SetAlignToggles(element.TextAlign);
            }
            if (isShape) StrokeWidthBox.Text = Fmt(element.StrokeWidth);
            SetSwatch(StrokeColorButton, StrokeColorBox, element.Foreground);
            SetSwatch(BackColorButton, BackColorBox, element.Background);
        }
        finally { syncing = false; }
    }

    private void UpdatePositionFields()
    {
        if (designSelected is null) return;
        syncing = true;
        try
        {
            PosXBox.Text = Fmt(designSelected.X); PosYBox.Text = Fmt(designSelected.Y);
            RotationBox.Text = Fmt(designSelected.RotationDegrees);
        }
        finally { syncing = false; }
    }

    private void SetAlignToggles(string? align)
    {
        AlignLeftToggle.IsChecked = align == "Left";
        AlignRightToggle.IsChecked = align == "Right";
        AlignCenterToggle.IsChecked = align is not ("Left" or "Right");
    }

    private void UpdateTextHint()
    {
        var unknown = Regex.Matches(TextContentBox.Text, @"\{([^{}]+)\}").Select(m => m.Groups[1].Value)
            .Where(token => !PlateRules.Tokens.Contains(token)).Distinct().ToList();
        TextHint.Text = unknown.Count == 0 ? "" : "Campo desconhecido: " + string.Join(", ", unknown.Select(t => "{" + t + "}"));
    }

    private void ApplyToSelected(Action<PlateElement> change, bool coalesce = false)
    {
        if (syncing || designSelected is null) return;
        change(designSelected);
        RefreshVisual(designSelected);
        PositionElement(designSelected);
        UpdateSelectionVisuals();
        CommitHistory(coalesce);
    }

    private void NumericBoxCommit(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitNumeric(box);
    }
    private void NumericBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        CommitNumeric(box);
        e.Handled = true;
    }

    private void CommitNumeric(TextBox box)
    {
        if (syncing || editing is null) return;
        var allowNegative = box.Name is "PosXBox" or "PosYBox" or "RotationBox";
        var allowZero = box.Name == "StrokeWidthBox";
        if (!TryNumber(box.Text, out var value) || (allowNegative ? false : allowZero ? value < 0 : value <= 0))
        { FillInspector(); return; }
        value = Math.Round(value, 2);
        switch (box.Name)
        {
            case "PosXBox": ApplyToSelected(el => el.X = value); break;
            case "PosYBox": ApplyToSelected(el => el.Y = value); break;
            case "RotationBox": ApplyToSelected(el => el.RotationDegrees = value); break;
            case "FontSizeBox":
                ApplyTextFormatting(el => el.FontSize = value, style => style.FontSize = value, style => style.FontSize = null);
                break;
            case "StrokeWidthBox": ApplyToSelected(el => el.StrokeWidth = value); break;
            case "PlateWidthBox": editing.WidthMm = value; RebuildDesigner(); if (fitMode) ZoomFit(); break;
            case "PlateHeightBox": editing.HeightMm = value; RebuildDesigner(); if (fitMode) ZoomFit(); break;
        }
        CommitHistory();
        FillInspector();
    }

    private void PlateNameChanged(object sender, TextChangedEventArgs e)
    {
        if (syncing || editing is null) return;
        editing.Name = PlateNameBox.Text;
        CommitHistory(coalesce: true);
    }

    private void TextContentChanged(object sender, TextChangedEventArgs e)
    {
        if (syncing || designSelected is null || designSelected.IsImage) return;
        var text = TextContentBox.Text;
        ApplyToSelected(el =>
        {
            el.TextStyles = PlateTextFormatting.Remap(el.TextStyles, el.Text, text);
            el.Text = text;
        }, coalesce: true);
        UpdateTextHint();
        RefreshLayers();
    }

    private PlateTextStyle? SelectedTextStyle()
    {
        if (designSelected is null || TextContentBox.SelectionLength == 0) return null;
        var position = TextContentBox.SelectionStart;
        return designSelected.TextStyles.FirstOrDefault(style =>
            style.Start <= position && style.Start + style.Length > position);
    }

    private void UpdateTextFormattingControls()
    {
        if (designSelected is null) return;
        var style = SelectedTextStyle();
        FontButtonText.Text = style?.FontFamily ?? designSelected.FontFamily;
        FontSizeBox.Text = Fmt(style?.FontSize ?? designSelected.FontSize);
        BoldToggle.IsChecked = style?.Bold ?? designSelected.Bold;
        ItalicToggle.IsChecked = style?.Italic ?? designSelected.Italic;
        UnderlineToggle.IsChecked = style?.Underline ?? designSelected.Underline;
        SetSwatch(TextColorButton, TextColorBox, style?.Foreground ?? designSelected.Foreground);
    }

    private void TextSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (syncing || designSelected is null || designSelected.IsImage || !string.IsNullOrEmpty(designSelected.Shape)) return;
        UpdateTextFormattingControls();
    }

    private void TextContentPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var toggle = e.Key switch
        {
            Key.B => BoldToggle,
            Key.I => ItalicToggle,
            Key.U => UnderlineToggle,
            _ => null
        };
        if (toggle is null) return;
        toggle.IsChecked = toggle.IsChecked != true;
        StyleToggleClick(toggle, new RoutedEventArgs());
        e.Handled = true;
    }

    private void ApplyTextFormatting(
        Action<PlateElement> changeBase, Action<PlateTextStyle> changeRange, Action<PlateTextStyle> clearRange)
    {
        if (designSelected is null || designSelected.IsImage || !string.IsNullOrEmpty(designSelected.Shape)) return;
        var start = TextContentBox.SelectionStart;
        var length = TextContentBox.SelectionLength;
        ApplyToSelected(element =>
        {
            if (length > 0)
                element.TextStyles = PlateTextFormatting.Apply(element.TextStyles, element.Text.Length, start, length, changeRange);
            else
            {
                changeBase(element);
                element.TextStyles = PlateTextFormatting.Clear(element.TextStyles, clearRange);
            }
        });
        UpdateTextFormattingControls();
    }

    private void FieldInsertChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || FieldInsertBox.SelectedItem is not ComboBoxItem { Tag: string token }) return;
        var insertion = "{" + token + "}";
        var start = TextContentBox.SelectionStart;
        TextContentBox.Text = TextContentBox.Text.Remove(start, TextContentBox.SelectionLength).Insert(start, insertion);
        TextContentBox.CaretIndex = start + insertion.Length;
        syncing = true;
        try { FieldInsertBox.SelectedIndex = 0; }
        finally { syncing = false; }
        TextContentBox.Focus();
    }

    private void InspectorFontClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (designSelected is null || designSelected.IsImage || !string.IsNullOrEmpty(designSelected.Shape)) return;
        var chosen = PickFontDialog(SelectedTextStyle()?.FontFamily ?? designSelected.FontFamily);
        if (chosen is null) return;
        ApplyTextFormatting(el => el.FontFamily = chosen, style => style.FontFamily = chosen, style => style.FontFamily = null);
    });

    private void StyleToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        var enabled = toggle.IsChecked == true;
        switch (toggle.Name)
        {
            case "BoldToggle": ApplyTextFormatting(el => el.Bold = enabled, style => style.Bold = enabled, style => style.Bold = null); break;
            case "ItalicToggle": ApplyTextFormatting(el => el.Italic = enabled, style => style.Italic = enabled, style => style.Italic = null); break;
            case "UnderlineToggle": ApplyTextFormatting(el => el.Underline = enabled, style => style.Underline = enabled, style => style.Underline = null); break;
        }
    }

    private void AlignClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string align }) return;
        SetAlignToggles(align);
        ApplyToSelected(el => el.TextAlign = align);
    }

    private void ImageSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || designSelected is null || !designSelected.IsImage || ImageSourceBox.SelectedValue is not string id) return;
        ApplyToSelected(el => el.ImageId = id);
        RefreshLayers();
    }


    private string ColorOf(string target) => target switch
    {
        "plate" => editing?.Background ?? "#FFFFFF",
        "text" => SelectedTextStyle()?.Foreground ?? designSelected?.Foreground ?? "#111827",
        "stroke" => designSelected?.Foreground ?? "#111827",
        _ => designSelected?.Background ?? "#00FFFFFF"
    };

    private void ApplyColor(string target, string hex)
    {
        if (editing is null) return;
        switch (target)
        {
            case "plate":
                editing.Background = hex;
                PlateLayer.Background = SafeBrush(hex) ?? Brushes.White;
                CommitHistory();
                break;
            case "text": ApplyTextFormatting(el => el.Foreground = hex, style => style.Foreground = hex, style => style.Foreground = null); break;
            case "stroke": ApplyToSelected(el => el.Foreground = hex); break;
            default: ApplyToSelected(el => el.Background = hex); break;
        }
        FillInspector();
    }

    private void PickColorClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (sender is not Button { Tag: string target }) return;
        var chosen = PickColorDialog(ColorOf(target), allowTransparent: target != "text");
        if (chosen is not null) ApplyColor(target, chosen);
    });

    private void ColorBoxCommit(object sender, RoutedEventArgs e)
    {
        if (syncing || sender is not TextBox { Tag: string target } box) return;
        var hex = NormalizeColor(box.Text);
        if (hex is null) { FillInspector(); return; }
        if (hex != ColorOf(target)) ApplyColor(target, hex);
    }
    private void ColorBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ColorBoxCommit(sender, e);
        e.Handled = true;
    }
    private void ClearBackColorClick(object sender, RoutedEventArgs e) => ApplyColor("back", "#00FFFFFF");

    private string? PickColorDialog(string? current, bool allowTransparent)
    {
        string[] palette =
        [
            "#FFFFFF", "#F3F4F6", "#D1D5DB", "#9CA3AF", "#6B7280", "#374151", "#111827", "#000000",
            "#FEE2E2", "#FCA5A5", "#EF4444", "#D71920", "#B91C1C", "#7F1D1D", "#FFE4E6", "#EC4899",
            "#FFEDD5", "#FDBA74", "#F97316", "#EA580C", "#C2410C", "#7C2D12", "#FEF3C7", "#F59E0B",
            "#FEF9C3", "#FFF03B", "#FACC15", "#EAB308", "#A16207", "#713F12", "#ECFCCB", "#84CC16",
            "#DCFCE7", "#86EFAC", "#22C55E", "#16A34A", "#15803D", "#14532D", "#CCFBF1", "#14B8A6",
            "#DBEAFE", "#93C5FD", "#3B82F6", "#2563EB", "#1D4ED8", "#1E3A8A", "#E0E7FF", "#6366F1",
            "#F3E8FF", "#C084FC", "#A855F7", "#7E22CE", "#581C87", "#3B0764", "#FCE7F3", "#102C46"
        ];
        string? result = null;
        var window = new Window
        {
            Title = "Escolher cor", Width = 372, SizeToContent = SizeToContent.Height, Owner = this, ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var hexBox = new TextBox { Width = 120, Text = current ?? "" };
        var preview = new Border { Width = 56, Height = 28, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        void UpdatePreview() => preview.Background = SafeBrush(NormalizeColor(hexBox.Text)) ?? Brushes.Transparent;
        Button Swatch(string hex)
        {
            var button = new Button { Width = 34, Height = 28, Margin = new Thickness(2), Background = SafeBrush(hex), ToolTip = hex };
            button.Click += (_, _) => { result = hex; window.DialogResult = true; };
            return button;
        }
        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in palette) grid.Children.Add(Swatch(hex));
        var usedColors = editing is null ? new List<string>()
            : editing.Elements.SelectMany(x => new[] { x.Foreground, x.Background }
                    .Concat(x.TextStyles.Where(style => style.Foreground is not null).Select(style => style.Foreground!)))
                .Append(editing.Background)
                .Select(NormalizeColor).OfType<string>().Where(c => !c.StartsWith("#00")).Distinct().Take(8).ToList();
        var used = new UniformGrid { Columns = 8 };
        foreach (var hex in usedColors) used.Children.Add(Swatch(hex));

        hexBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var use = new Button { Content = "Usar", IsDefault = true, Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        use.Click += (_, _) =>
        {
            var hex = NormalizeColor(hexBox.Text);
            if (hex is null) { MessageBox.Show(window, "Use o formato #RRGGBB (ex.: #FFF03B).", "Cor inválida", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            result = hex; window.DialogResult = true;
        };
        var transparent = new Button { Content = "Transparente", Padding = new Thickness(8, 2, 8, 2), Visibility = allowTransparent ? Visibility.Visible : Visibility.Collapsed };
        transparent.Click += (_, _) => { result = "#00FFFFFF"; window.DialogResult = true; };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 80, Margin = new Thickness(8, 0, 0, 0) };

        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        custom.Children.Add(preview); custom.Children.Add(hexBox); custom.Children.Add(use);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(transparent); bottom.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Cores", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(grid);
        if (usedColors.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Usadas neste layout", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            panel.Children.Add(used);
        }
        panel.Children.Add(new TextBlock { Text = "Outra cor (hexadecimal)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(custom); panel.Children.Add(bottom);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }
}
