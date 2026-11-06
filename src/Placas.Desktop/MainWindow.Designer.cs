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
    private readonly Dictionary<string, Rectangle> handles = [];
    private readonly Rectangle selectionBox = new() { Fill = Brushes.Transparent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line guideV = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Line guideH = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly PriceItem designItem = Example();
    private Brush gridBrush = null!;
    private PlateElement? designSelected;
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
    private (double X, double Y, double W, double H) dragOrigin;

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
        selectionBox.Stroke = AccentBrush;
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
        RebuildDesigner();
        FillInspector();
        fitMode = true;
        ZoomFit();
        ResetHistory();
        PreviewStatus.Text = editing is null
            ? "Selecione ou crie um layout."
            : "Edite visualmente e clique em Salvar layout para guardar as alterações.";
    }

    private void RebuildDesigner()
    {
        EndDrag();
        PlateLayer.Children.Clear(); OverlayLayer.Children.Clear();
        designVisuals.Clear(); designHits.Clear();
        if (editing is null)
        {
            PlateLayer.Width = PlateLayer.Height = OverlayLayer.Width = OverlayLayer.Height = 0;
            designSelected = null;
            UpdateSelectionVisuals(); RefreshLayers();
            return;
        }
        PlateLayer.Width = OverlayLayer.Width = ToDip(editing.WidthMm);
        PlateLayer.Height = OverlayLayer.Height = ToDip(editing.HeightMm);
        if (designSelected is not null && !editing.Elements.Contains(designSelected)) designSelected = null;
        foreach (var element in DrawOrder(editing))
        {
            var hit = new Rectangle { Fill = Brushes.Transparent, Tag = element, Cursor = Cursors.SizeAll };
            hit.MouseLeftButtonDown += ElementHitDown;
            hit.MouseEnter += HitEnter;
            hit.MouseLeave += HitLeave;
            OverlayLayer.Children.Add(hit);
            designHits[element] = hit;
            PositionElement(element);
        }
        OverlayLayer.Children.Add(selectionBox);
        foreach (var handle in handles.Values) OverlayLayer.Children.Add(handle);
        OverlayLayer.Children.Add(guideV); OverlayLayer.Children.Add(guideH);
        RebuildVisuals();
        UpdateSelectionVisuals();
        RefreshLayers();
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
        }
    }


    private void SelectElement(PlateElement? element)
    {
        designSelected = element;
        UpdateSelectionVisuals();
        FillInspector();
        SyncLayersSelection();
    }

    private void UpdateSelectionVisuals()
    {
        var element = designSelected;
        if (element is null || editing is null)
        {
            selectionBox.Visibility = Visibility.Collapsed;
            foreach (var handle in handles.Values) handle.Visibility = Visibility.Collapsed;
            SelectionReadout.Text = editing is null ? "" : $"Placa {Fmt(editing.WidthMm)} × {Fmt(editing.HeightMm)} mm";
            return;
        }
        double x = ToDip(element.X), y = ToDip(element.Y), w = ToDip(element.Width), h = ToDip(element.Height);
        selectionBox.Width = w; selectionBox.Height = h;
        Canvas.SetLeft(selectionBox, x); Canvas.SetTop(selectionBox, y);
        selectionBox.StrokeThickness = 1.5 / zoom;
        selectionBox.Visibility = Visibility.Visible;
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
        SelectionReadout.Text = $"X {Fmt(element.X)}  Y {Fmt(element.Y)}  •  {Fmt(element.Width)} × {Fmt(element.Height)} mm";
    }

    private void HitEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Rectangle rectangle || dragKind != DragKind.None || ReferenceEquals(rectangle.Tag, designSelected)) return;
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
        SelectElement(element);
        BeginDrag(DragKind.Move, "", e);
    }

    private void HandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: string name } || designSelected is null) return;
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
        dragOrigin = (designSelected.X, designSelected.Y, designSelected.Width, designSelected.Height);
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
        EndDrag();
        if (designSelected is not null) { RefreshVisual(designSelected); FillInspector(); }
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
            if (BestSnap(new[] { x, x + o.W / 2, x + o.W }, VerticalLines(element), threshold, out var vLine) is { } deltaX)
            {
                x += deltaX; ShowGuideV(vLine);
            }
            if (BestSnap(new[] { y, y + o.H / 2, y + o.H }, HorizontalLines(element), threshold, out var hLine) is { } deltaY)
            {
                y += deltaY; ShowGuideH(hLine);
            }
        }
        element.X = Math.Round(x, 2); element.Y = Math.Round(y, 2);
        PositionElement(element);
        UpdateSelectionVisuals();
        UpdatePositionFields();
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
            var vLines = VerticalLines(element); var hLines = HorizontalLines(element);
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
        RefreshVisual(element);
        PositionElement(element);
        UpdateSelectionVisuals();
        UpdatePositionFields();
    }


    private double SnapThreshold() => 7 / zoom / MmToDip; 

    private List<double> VerticalLines(PlateElement exclude)
    {
        var lines = new List<double> { 0, editing!.WidthMm / 2, editing.WidthMm };
        foreach (var other in editing.Elements.Where(x => !ReferenceEquals(x, exclude)))
        { lines.Add(other.X); lines.Add(other.X + other.Width / 2); lines.Add(other.X + other.Width); }
        return lines;
    }
    private List<double> HorizontalLines(PlateElement exclude)
    {
        var lines = new List<double> { 0, editing!.HeightMm / 2, editing.HeightMm };
        foreach (var other in editing.Elements.Where(x => !ReferenceEquals(x, exclude)))
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
        designSelected.X = Math.Round(designSelected.X + dx, 2);
        designSelected.Y = Math.Round(designSelected.Y + dy, 2);
        PositionElement(designSelected);
        UpdateSelectionVisuals();
        UpdatePositionFields();
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
        var copy = JsonSerializer.Deserialize<PlateElement>(JsonSerializer.Serialize(designSelected))!;
        copy.X = Math.Round(copy.X + 3, 2); copy.Y = Math.Round(copy.Y + 3, 2);
        editing.Elements.Insert(editing.Elements.IndexOf(designSelected) + 1, copy);
        RebuildDesigner();
        SelectElement(copy);
        CommitHistory();
    });

    private void DeleteElementClick(object sender, RoutedEventArgs e) => Run(DeleteSelectedElement);
    private void DeleteSelectedElement()
    {
        if (designSelected is null || editing is null) return;
        editing.Elements.Remove(designSelected);
        designSelected = null;
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
        var index = designSelected is null || editing is null ? -1 : editing.Elements.IndexOf(designSelected);
        editing = JsonSerializer.Deserialize<PlateTemplate>(json)!;
        lastSnapshot = json; lastCommitTime = DateTime.MinValue;
        designSelected = index >= 0 && index < editing.Elements.Count ? editing.Elements[index] : null;
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
            var rows = order.Select(x => new LayerRow(x, x.IsImage ? "▣" : "T", LayerTitle(x, imageChoices))).ToList();
            LayersList.ItemsSource = rows;
            LayersList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Element, designSelected));
        }
        finally { layersSync = false; }
    }

    private void SyncLayersSelection()
    {
        layersSync = true;
        try
        {
            LayersList.SelectedItem = (LayersList.ItemsSource as List<LayerRow>)?.FirstOrDefault(r => ReferenceEquals(r.Element, designSelected));
        }
        finally { layersSync = false; }
    }

    private void LayersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (layersSync || LayersList.SelectedItem is not LayerRow row) return;
        SelectElement(row.Element);
    }

    private void LayerForwardClick(object sender, RoutedEventArgs e) => MoveLayer(+1);
    private void LayerBackwardClick(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void MoveLayer(int direction)
    {
        if (designSelected is null || editing is null) return;
        var list = editing.Elements;
        var from = list.IndexOf(designSelected);
        var to = from + direction;
        while (to >= 0 && to < list.Count && list[to].IsImage != designSelected.IsImage) to += direction;
        if (to < 0 || to >= list.Count) return;
        (list[from], list[to]) = (list[to], list[from]);
        RebuildDesigner();
        SelectElement(designSelected);
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
            PlatePanel.Visibility = element is null ? Visibility.Visible : Visibility.Collapsed;
            ElementPanel.Visibility = element is null ? Visibility.Collapsed : Visibility.Visible;
            InspectorTitle.Text = element is null ? "Placa" : element.IsImage ? "Imagem" : "Texto";
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
            WidthBox.Text = Fmt(element.Width); HeightBox.Text = Fmt(element.Height);
            TextPanel.Visibility = element.IsImage ? Visibility.Collapsed : Visibility.Visible;
            ImagePanel.Visibility = element.IsImage ? Visibility.Visible : Visibility.Collapsed;
            if (element.IsImage)
                ImageSourceBox.SelectedValue = string.IsNullOrWhiteSpace(element.ImageId) ? ImageSources.Product : element.ImageId;
            else
            {
                TextContentBox.Text = element.Text;
                UpdateTextHint();
                FontButtonText.Text = element.FontFamily;
                FontSizeBox.Text = Fmt(element.FontSize); MinFontBox.Text = Fmt(element.MinFontSize);
                BoldToggle.IsChecked = element.Bold; ItalicToggle.IsChecked = element.Italic;
                SetAlignToggles(element.TextAlign);
                SetSwatch(TextColorButton, TextColorBox, element.Foreground);
            }
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
            WidthBox.Text = Fmt(designSelected.Width); HeightBox.Text = Fmt(designSelected.Height);
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
        var allowZeroOrNegative = box.Name is "PosXBox" or "PosYBox";
        if (!TryNumber(box.Text, out var value) || (!allowZeroOrNegative && value <= 0)) { FillInspector(); return; }
        value = Math.Round(value, 2);
        switch (box.Name)
        {
            case "PosXBox": ApplyToSelected(el => el.X = value); break;
            case "PosYBox": ApplyToSelected(el => el.Y = value); break;
            case "WidthBox": ApplyToSelected(el => el.Width = value); break;
            case "HeightBox": ApplyToSelected(el => el.Height = value); break;
            case "FontSizeBox": ApplyToSelected(el => el.FontSize = value); break;
            case "MinFontBox": ApplyToSelected(el => el.MinFontSize = value); break;
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
        ApplyToSelected(el => el.Text = text, coalesce: true);
        UpdateTextHint();
        RefreshLayers();
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
        if (designSelected is null || designSelected.IsImage) return;
        var chosen = PickFontDialog(designSelected.FontFamily);
        if (chosen is null) return;
        ApplyToSelected(el => el.FontFamily = chosen);
        FontButtonText.Text = chosen;
    });

    private void StyleToggleClick(object sender, RoutedEventArgs e)
    {
        var bold = BoldToggle.IsChecked == true; var italic = ItalicToggle.IsChecked == true;
        ApplyToSelected(el => { el.Bold = bold; el.Italic = italic; });
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
        "text" => designSelected?.Foreground ?? "#111827",
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
            case "text": ApplyToSelected(el => el.Foreground = hex); break;
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
            : editing.Elements.SelectMany(x => new[] { x.Foreground, x.Background }).Append(editing.Background)
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
