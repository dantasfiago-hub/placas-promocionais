using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Placas.Application;
using Placas.Domain;
using Placas.Infrastructure;
using Placas.Rendering;

namespace Placas.Desktop;

public sealed class ProductRow(PriceItem item) : INotifyPropertyChanged
{
    public PriceItem Item { get; } = item;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public string Origin => Item.Source switch { SourceKind.Catalog => "Catálogo", SourceKind.Offer => "Oferta", _ => "Promoção" };
    public string Review => Item.Price <= 0 ? "Preço zerado" : Item.Rule == QuantityRule.Pending ? "Confirmar regra" : "";
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class BatchRow(PriceItem item, int copies)
{
    public PriceItem Item { get; set; } = item;
    public int Copies { get; set; } = copies;
}

public partial class MainWindow : Window
{
    private readonly IStore store;
    private readonly BatchService batchService = new();
    private readonly PlateRenderer renderer = new();
    private readonly ObservableCollection<BatchRow> basket = [];
    private List<ProductRow> products = [];
    private PlateTemplate? editing;
    private CancellationTokenSource? importCancellation;
    private string importReport = "Nenhuma importação realizada nesta sessão.";
    private PrintBatch? currentBatch;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MainWindow(IStore store)
    {
        this.store = store;
        InitializeComponent();
        SaleDate.SelectedDate = DateTime.Today;
        BatchGrid.ItemsSource = basket;
        // Printing is only routed through our validated pipeline, including keyboard shortcuts.
        DocumentPreview.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, (_, e) => { PrintClick(this, e); e.Handled = true; }));
        RefreshTemplates(); RefreshProducts(); RefreshHistory();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { App.Log(ex); MessageBox.Show(this, ex.Message, "Confira os dados", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Planilhas Excel|*.xls;*.xlsx", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        ImportButton.IsEnabled = false; importCancellation = new();
        var report = new StringBuilder();
        try
        {
            foreach (var path in dialog.FileNames)
            {
                Status.Text = $"Lendo {Path.GetFileName(path)}…";
                var cancellation = importCancellation.Token;
                var batch = await Task.Run(() => new VrImporter().Read(path, cancellation), cancellation);
                report.AppendLine($"{batch.FileName}: {batch.Items.Count} itens válidos, {batch.Issues.Count} erros.");
                foreach (var issue in batch.Issues) report.AppendLine($"  {issue.Sheet}, linha {issue.Row}: {issue.Message}");
                importReport = report.ToString();
                var zero = batch.Items.Count(x => x.Price <= 0);
                var pending = batch.Items.Count(x => x.Rule == QuantityRule.Pending);
                var message = $"{batch.FileName}\n\n{batch.Items.Count:N0} condições válidas\n{zero:N0} preços zerados (bloqueados para impressão)\n{pending} condições pendentes\n{batch.Issues.Count} linhas rejeitadas\n\nImportar os itens válidos?";
                if (batch.Items.Count == 0) { MessageBox.Show(this, "Nenhum item válido. Consulte o relatório."); continue; }
                if (MessageBox.Show(this, message, "Conferir importação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) continue;
                cancellation.ThrowIfCancellationRequested();
                var imported = await Task.Run(() => store.Import(batch));
                report.AppendLine(imported ? "Importação concluída." : "Arquivo já importado; nenhuma duplicação.");
            }
            RefreshProducts(); Status.Text = "Importação finalizada. Consulte o relatório para os detalhes.";
        }
        catch (OperationCanceledException) { report.AppendLine("Leitura cancelada."); Status.Text = "Leitura cancelada. Importações já confirmadas foram preservadas."; }
        catch (Exception ex) { App.Log(ex); report.AppendLine(ex.Message); MessageBox.Show(this, ex.Message, "Falha na importação"); }
        finally { importReport = report.ToString(); importCancellation.Dispose(); importCancellation = null; ImportButton.IsEnabled = true; }
    }
    private void CancelImportClick(object sender, RoutedEventArgs e) => importCancellation?.Cancel();
    private void ImportReportClick(object sender, RoutedEventArgs e)
    {
        new Window { Owner = this, Title = "Relatório da importação", Width = 850, Height = 550,
            Content = new TextBox { Text = importReport, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true } }.ShowDialog();
    }
    private void RefreshProducts()
    {
        SourceKind? source = SourceFilter.SelectedIndex == 0 ? null : (SourceKind)(SourceFilter.SelectedIndex - 1);
        products = store.Search(SearchText.Text, source).Select(x => new ProductRow(x)).ToList();
        ProductsGrid.ItemsSource = products; Status.Text = $"{products.Count} condições exibidas • {basket.Count} produtos no lote.";
    }
    private void SearchClick(object sender, RoutedEventArgs e) => Run(RefreshProducts);
    private void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Run(RefreshProducts); }
    private void SelectAllClick(object sender, RoutedEventArgs e) { foreach (var row in products) row.Selected = true; }
    private void ClearSelectionClick(object sender, RoutedEventArgs e) { foreach (var row in products) row.Selected = false; }
    private void AddToBatchClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        ProductsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (!int.TryParse(CopiesText.Text, out var copies) || copies is < 1 or > 500) throw new InvalidOperationException("Informe 1 a 500 cópias.");
        var chosen = products.Where(x => x.Selected).ToList();
        if (chosen.Count == 0) throw new InvalidOperationException("Marque os produtos na primeira coluna.");
        if (chosen.GroupBy(x => x.Item.Code).Any(x => x.Count() > 1) || chosen.Any(x => basket.Any(b => b.Item.Code == x.Item.Code && b.Item.Id != x.Item.Id)))
            throw new InvalidOperationException("Há condições diferentes do mesmo produto. Selecione uma condição por produto.");
        foreach (var row in chosen)
        {
            var existing = basket.FirstOrDefault(x => x.Item.Id == row.Item.Id);
            if (existing is null) basket.Add(new(row.Item, copies));
            else { existing.Item = row.Item; existing.Copies = copies; }
            row.Selected = false;
        }
        BatchGrid.Items.Refresh(); InvalidatePreview();
        Status.Text = $"{basket.Count} produtos no lote. Escolha o layout na aba Lote e impressão.";
    });
    private void ConfirmRuleClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (ProductsGrid.SelectedItem is not ProductRow row || row.Item.Source != SourceKind.Promotion)
            throw new InvalidOperationException("Destaque uma linha de promoção.");
        var result = MessageBox.Show(this,
            $"Confira a condição no VR Master antes de prosseguir.\n\n{row.Item.Description}\nQuantidade: {row.Item.Quantity} — Valor: {row.Item.Price:C2}\n\nSIM: o valor é o TOTAL do conjunto.\nNÃO: o valor é POR UNIDADE ao comprar a quantidade.\nCANCELAR: manter sem confirmação.",
            "Definir condição comercial", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return;
        var rule = result == MessageBoxResult.Yes ? QuantityRule.BundleTotal : QuantityRule.UnitPrice;
        store.ConfirmPromotion(row.Item.Id, rule);
        foreach (var b in basket.Where(x => x.Item.Id == row.Item.Id)) b.Item = b.Item with { Rule = rule };
        InvalidatePreview(); RefreshProducts();
    });

    private void RefreshTemplates(string? selectedId = null)
    {
        var templates = store.Templates();
        TemplatesBox.ItemsSource = templates; BatchTemplateBox.ItemsSource = templates;
        TemplatesBox.SelectedItem = templates.FirstOrDefault(x => x.Id == selectedId) ?? templates.FirstOrDefault();
        BatchTemplateBox.SelectedItem = TemplatesBox.SelectedItem;
    }
    private void TemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatesBox.SelectedItem is not PlateTemplate selected) return;
        editing = JsonSerializer.Deserialize<PlateTemplate>(JsonSerializer.Serialize(selected))!;
        BindEditor();
    }
    private void BindEditor()
    {
        TemplateFields.DataContext = editing; ElementsGrid.ItemsSource = editing?.Elements;
        if (editing is not null) Run(UpdateTemplatePreview);
    }
    private void CommitEditor()
    {
        ElementsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ElementsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Keyboard.ClearFocus();
        if (editing is null) throw new InvalidOperationException("Crie ou selecione um modelo.");
        if (HasValidationError(TemplateFields) || HasValidationError(ElementsGrid))
            throw new InvalidOperationException("Corrija os campos numéricos destacados antes de salvar.");
        PlateRules.ValidateTemplate(editing);
    }
    private static bool HasValidationError(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (HasValidationError(System.Windows.Media.VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }
    private static PriceItem Example() => new("sample", "21513", "021513", "PILHA PANASONIC SUPER HYPER AA C/4UN", 5.69m, 7.99m,
        DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(30)), SourceKind.Offer, null, 1,
        QuantityRule.UnitPrice, "Exemplo", "", 0);
    private void UpdateTemplatePreview()
    {
        CommitEditor(); TemplatePreview.Child = renderer.Plate(editing!, Example());
    }
    private void NewTemplateClick(object sender, RoutedEventArgs e) { editing = PlateTemplate.Default(); editing.Name = "Novo layout"; BindEditor(); }
    private void DuplicateTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        CommitEditor(); editing!.Id = Guid.NewGuid().ToString("N"); editing.Name += " — cópia"; BindEditor();
    });
    private void SaveTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        CommitEditor(); UpdateTemplatePreview(); store.SaveTemplate(editing!); var id = editing!.Id;
        RefreshTemplates(id); InvalidatePreview(); Status.Text = "Layout salvo. Ele pode ser aplicado a todos os produtos do lote.";
    });
    private void DeleteTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (editing is null) return;
        if (MessageBox.Show(this, $"Excluir o layout '{editing.Name}'? O histórico será preservado.", "Excluir layout", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        store.DeleteTemplate(editing.Id); editing = null; TemplateFields.DataContext = null;
        ElementsGrid.ItemsSource = null; TemplatePreview.Child = null; RefreshTemplates(); InvalidatePreview();
    });
    private void ExportTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        CommitEditor(); var dialog = new SaveFileDialog { Filter = "Layout JSON|*.json", FileName = "layout-placa.json" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(editing, JsonOptions));
    });
    private void ImportTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Layout JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 1_000_000) throw new InvalidOperationException("Layout excede 1 MB.");
        var template = JsonSerializer.Deserialize<PlateTemplate>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Layout vazio.");
        PlateRules.ValidateTemplate(template); template.Id = Guid.NewGuid().ToString("N");
        store.SaveTemplate(template); RefreshTemplates(template.Id);
    });
    private void AddElementClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (editing is null) return;
        editing.Elements.Add(new() { Text = "Novo texto", X = 0, Y = 0, Width = Math.Min(60, editing.WidthMm), Height = 15, FontSize = 15 });
        ElementsGrid.Items.Refresh();
    });
    private void RemoveElementClick(object sender, RoutedEventArgs e)
    {
        if (ElementsGrid.SelectedItem is PlateElement element) { editing?.Elements.Remove(element); ElementsGrid.Items.Refresh(); }
    }
    private void TemplatePreviewClick(object sender, RoutedEventArgs e) => Run(UpdateTemplatePreview);

    private (double Width, double Height, double Margin, double Gap) Paper()
    {
        var (w, h) = PaperBox.SelectedIndex switch { 1 => (297d, 420d), 2 => (148d, 210d), _ => (210d, 297d) };
        if (Landscape.IsChecked == true) (w, h) = (h, w);
        if (!double.TryParse(MarginText.Text, out var margin) || !double.TryParse(GapText.Text, out var gap))
            throw new InvalidOperationException("Margem ou espaçamento inválido.");
        return (w, h, margin, gap);
    }
    private PrintBatch BuildBatch()
    {
        BatchGrid.CommitEdit(DataGridEditingUnit.Cell, true); BatchGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (HasValidationError(BatchGrid)) throw new InvalidOperationException("Corrija as quantidades do lote.");
        var template = BatchTemplateBox.SelectedItem as PlateTemplate ?? throw new InvalidOperationException("Salve e selecione um layout.");
        var date = SaleDate.SelectedDate ?? throw new InvalidOperationException("Escolha a data de venda.");
        return batchService.Create(basket.Select(x => new PrintItem(x.Item, x.Copies)), template, DateOnly.FromDateTime(date));
    }
    private void InvalidatePreview() { currentBatch = null; DocumentPreview.Document = null; }
    private void GenerateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var batch = BuildBatch(); var paper = Paper();
        var document = renderer.Document(batch, paper.Width, paper.Height, paper.Margin, paper.Gap, CutMarks.IsChecked == true);
        currentBatch = batch; DocumentPreview.Document = document;
        store.SaveHistory(batch, "Prévia gerada", $"{batch.Items.Sum(x => x.Copies)} placas; {document.Pages.Count} páginas");
        RefreshHistory(); Status.Text = "Prévia gerada. A impressão usa o layout e os dados atuais do lote.";
    });
    private void PrintClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        // Rebuild from current controls: never send an outdated preview after changing copies/template/date.
        var batch = BuildBatch(); var paper = Paper();
        var dialog = new PrintDialog();
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(PlateRenderer.Dip(paper.Width), PlateRenderer.Dip(paper.Height));
        dialog.PrintTicket.PageOrientation = PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = 1;
        if (dialog.ShowDialog() != true) return;
        var capabilities = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
        var area = capabilities.PageImageableArea;
        var chosen = dialog.PrintTicket.PageMediaSize;
        var width = (chosen?.Width ?? PlateRenderer.Dip(paper.Width));
        var height = (chosen?.Height ?? PlateRenderer.Dip(paper.Height));
        if (dialog.PrintTicket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape)
            (width, height) = (height, width);
        var minimumMargin = area is null ? 0 : Math.Max(Math.Max(area.OriginWidth, area.OriginHeight),
            Math.Max(width - area.OriginWidth - area.ExtentWidth, height - area.OriginHeight - area.ExtentHeight)) * 25.4 / 96;
        var margin = Math.Max(paper.Margin, minimumMargin);
        var document = renderer.Document(batch, width * 25.4 / 96, height * 25.4 / 96, margin, paper.Gap, CutMarks.IsChecked == true);
        currentBatch = batch; DocumentPreview.Document = document;
        // Modal review displays the exact document that will be sent to the driver.
        if (!ReviewPrint(document, $"{dialog.PrintQueue.Name} • {document.Pages.Count} páginas • margem {margin:0.#} mm")) return;
        store.SaveHistory(batch, "Envio iniciado", dialog.PrintQueue.Name);
        try
        {
            dialog.PrintDocument(document.DocumentPaginator, $"Placas {batch.Id[..8]}");
            store.SaveHistory(batch, "Enviado à fila", dialog.PrintQueue.Name);
            Status.Text = "Lote enviado à fila. Confira a saída física na impressora.";
        }
        catch (Exception ex) { store.SaveHistory(batch, "Envio não confirmado", ex.Message); throw; }
        finally { RefreshHistory(); }
    });
    private bool ReviewPrint(System.Windows.Documents.FixedDocument document, string title)
    {
        var window = new Window { Owner = this, Title = "Conferir impressão — " + title, Width = 1000, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel(); var button = new Button { Content = "Confirmar envio à impressora", Margin = new Thickness(12) };
        button.Click += (_, _) => window.DialogResult = true;
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var viewer = new DocumentViewer { Document = document };
        viewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, (_, args) => args.Handled = true, (_, args) => { args.CanExecute = false; args.Handled = true; }));
        panel.Children.Add(viewer); window.Content = panel;
        return window.ShowDialog() == true;
    }
    private void RemoveBatchItemClick(object sender, RoutedEventArgs e) { if (BatchGrid.SelectedItem is BatchRow row) basket.Remove(row); InvalidatePreview(); }
    private void ClearBatchClick(object sender, RoutedEventArgs e) { basket.Clear(); InvalidatePreview(); }
    private void RefreshHistory() => HistoryGrid.ItemsSource = store.History();
    private void RefreshHistoryClick(object sender, RoutedEventArgs e) => Run(RefreshHistory);
    private void LoadHistoryClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (HistoryGrid.SelectedItem is not HistoryEntry history) return;
        if (basket.Count > 0 && MessageBox.Show(this, "Substituir os itens do lote atual?", "Carregar histórico", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        basket.Clear(); foreach (var item in history.Batch.Items) basket.Add(new(item.Product, item.Copies));
        var templates = store.Templates();
        var snapshot = JsonSerializer.Deserialize<PlateTemplate>(JsonSerializer.Serialize(history.Batch.Template))!;
        snapshot.Id = Guid.NewGuid().ToString("N"); snapshot.Name += " (histórico)";
        templates.Add(snapshot); BatchTemplateBox.ItemsSource = templates; BatchTemplateBox.SelectedItem = snapshot;
        SaleDate.SelectedDate = DateTime.Today; InvalidatePreview(); Tabs.SelectedIndex = 2;
    });
    private void BackupClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new SaveFileDialog { Filter = "Backup SQLite|*.db", FileName = $"placas-backup-{DateTime.Now:yyyyMMdd}.db" };
        if (dialog.ShowDialog(this) == true) { store.Backup(dialog.FileName); Status.Text = "Backup concluído."; }
    });
    private void OpenDataClick(object sender, RoutedEventArgs e) => Run(() => Process.Start(new ProcessStartInfo(App.DataDirectory) { UseShellExecute = true }));
}
