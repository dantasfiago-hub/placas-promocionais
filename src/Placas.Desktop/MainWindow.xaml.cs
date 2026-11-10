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
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MahApps.Metro.Controls;
using Placas.Application;
using Placas.Domain;
using Placas.Infrastructure;
using Placas.Rendering;

namespace Placas.Desktop;

public sealed class ProductRow(PriceItem item) : INotifyPropertyChanged
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public PriceItem Item { get; } = item;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public string Origin => Item.Source switch { SourceKind.Catalog => "Catálogo", SourceKind.Offer => "Oferta", _ => "Promoção" };

    public bool IsExpired => Item.End is { } end && end < DateOnly.FromDateTime(DateTime.Today);
    public bool IsFuture => Item.Start is { } start && start > DateOnly.FromDateTime(DateTime.Today);
    public string Level => IsExpired ? "expired"
        : Item.Rule == QuantityRule.Pending ? "attention"
        : Item.Price <= 0 ? "zero"
        : IsFuture ? "future" : "ok";
    public string Situation => Level switch
    {
        "expired" => "Vencida",
        "attention" => "Confirmar regra",
        "zero" => "Preço zerado",
        "future" => "Futura",
        _ => Item.Source == SourceKind.Catalog ? "" : "Vigente"
    };
    public string Condition
    {
        get
        {
            if (Item.Quantity <= 1) return "";
            var quantity = Item.Quantity.ToString("0.##", Pt);
            return Item.Rule switch
            {
                QuantityRule.BundleTotal => $"{quantity} un. pelo total",
                QuantityRule.UnitPrice => $"{quantity} un. • preço por unidade",
                _ => $"{quantity} un. • a confirmar"
            };
        }
    }
    public bool HasImage => !string.IsNullOrWhiteSpace(Item.ImageId);
    public string ImageMark => HasImage ? "Produto" : !string.IsNullOrWhiteSpace(Item.FamilyImageId) ? "Família" : "";
    public string FamilyLabel => string.IsNullOrWhiteSpace(Item.Family) ? ""
        : string.IsNullOrWhiteSpace(Item.FamilyDescription) ? Item.Family! : $"{Item.Family} - {Item.FamilyDescription}";
    public bool HasSub => !string.IsNullOrWhiteSpace(Item.SubDescription);
    public string Details => string.Join("\n",
        $"Arquivo: {Item.FileName}",
        $"Aba: {Item.Sheet} • Linha: {Item.Row}",
        $"Início: {Item.Start?.ToString("dd/MM/yyyy") ?? "—"}",
        $"Término: {Item.End?.ToString("dd/MM/yyyy") ?? "—"}",
        $"Grupo: {(string.IsNullOrWhiteSpace(Item.GroupCode) ? "—" : string.IsNullOrWhiteSpace(Item.GroupDescription) ? Item.GroupCode : $"{Item.GroupCode} - {Item.GroupDescription}")}");
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record ImageChoice(string Id, string Name);
public sealed record FontChoice(string Name, System.Windows.Media.FontFamily Family);
public sealed class ImageRow(ImageInfo info)
{
    private System.Windows.Media.ImageSource? thumbnail;
    public string Id { get; } = info.Id;
    public string Name { get; set; } = info.Name;
    public string Size { get; } = info.Bytes >= 1_048_576 ? $"{info.Bytes / 1_048_576d:0.0} MB" : $"{info.Bytes / 1024d:0} KB";
    public int Products { get; } = info.ProductCount;
    public int Layouts { get; } = info.LayoutCount;
    public int Families { get; } = info.FamilyCount;
    public System.Windows.Media.ImageSource? Thumbnail => thumbnail ??= MainWindow.ToBitmap(info.Thumbnail);
}
public sealed class ImportRow(ImportRecord record)
{
    public ImportRecord Record { get; } = record;
    public string Name => Record.Name;
    public DateTimeOffset? CreatedAt => DateTimeOffset.TryParse(Record.Created, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var created) ? created.ToLocalTime() : null;
    public int LinkedItems => Record.LinkedItems;
    public string Status => Record.Managed ? "Pode ser excluída" : "Sem itens (remove só o registro)";
}
public sealed class BatchRow(PriceItem item, int copies)
{
    public PriceItem Item { get; set; } = item;
    public int Copies { get; set; } = copies;
}

public partial class MainWindow : MetroWindow
{
    private readonly IStore store;
    private readonly BatchService batchService = new();
    private readonly PlateRenderer renderer = new();
    private readonly ObservableCollection<BatchRow> basket = [];
    private List<ProductRow> products = [];
    private List<ProductRow> allProducts = [];
    private bool loadingFilters;
    private List<ImageChoice> imageChoices = [];
    private const string ImageFilter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff";
    private static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
    private PlateTemplate? editing;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? importCancellation;
    private string importReport = "Nenhuma importação realizada nesta sessão.";
    private PrintBatch? currentBatch;
    private bool darkTheme;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MainWindow(IStore store)
    {
        this.store = store;
        InitializeComponent();
        Tabs.SelectedIndex = 0;
        Loaded += (_, _) => UpdateResponsiveLayout(ActualWidth, ActualHeight);
        UpdateNavigationState();
        UpdateResponsiveLayout(ActualWidth, ActualHeight);
        FitToWorkArea();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); RebuildVisuals(); };
        InitDesigner();
        SaleDate.SelectedDate = DateTime.Today;
        BatchGrid.ItemsSource = basket;
        basket.CollectionChanged += (_, _) => UpdateBatchCount();
        UpdateBatchCount();
        DocumentPreview.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, (_, e) => { PrintClick(this, e); e.Handled = true; }));
        renderer.ImageLoader = store.GetImage;
        RefreshTemplates(); RefreshProducts(); RefreshHistory(); RefreshImports(); RefreshImages();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(FitToWorkArea));

    private void UpdateBatchCount()
    {
        BatchCountText.Text = ActualWidth < 940
            ? basket.Count.ToString(CultureInfo.InvariantCulture)
            : $"{basket.Count} {(basket.Count == 1 ? "produto" : "produtos")} no lote";
        BatchCountButton.ToolTip = $"{basket.Count} {(basket.Count == 1 ? "produto" : "produtos")} no lote — abrir Lote e impressão";
    }

    private void BatchCountClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;

    private void NavigateTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tabIndex }
            && int.TryParse(tabIndex, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < Tabs.Items.Count)
            Tabs.SelectedIndex = index;
    }

    private void TabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        UpdateNavigationState();
        if (IsLoaded) UpdateResponsiveLayout(ActualWidth, ActualHeight);
    }

    private void UpdateNavigationState()
    {
        Button[] navigationButtons =
        [
            ProductsNavButton, LayoutsNavButton, BatchNavButton,
            HistoryNavButton, ImportsNavButton, ImagesNavButton
        ];
        for (var index = 0; index < navigationButtons.Length; index++)
        {
            var selected = index == Tabs.SelectedIndex;
            navigationButtons[index].Foreground = (Brush)System.Windows.Application.Current.Resources[
                selected ? "SurfaceBrush" : "TextSecondaryBrush"];
            navigationButtons[index].BorderBrush = Brushes.Transparent;
            navigationButtons[index].BorderThickness = new Thickness(0);
            navigationButtons[index].Background = selected
                ? (Brush)System.Windows.Application.Current.Resources["AccentBrush"]
                : Brushes.Transparent;
            navigationButtons[index].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded) UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
    }

    private void UpdateResponsiveLayout(double width, double height)
    {
        var compactHeader = width < 940;
        var compactVertical = height < 560;
        MainHeaderGrid.Height = height < 500 ? 48 : 56;
        MainHeaderGrid.ColumnDefinitions[0].Width = new GridLength(compactHeader ? 48 : 220);
        MainHeaderGrid.ColumnDefinitions[2].Width = new GridLength(compactHeader ? 116 : 190);
        BrandTitle.Visibility = compactHeader ? Visibility.Collapsed : Visibility.Visible;
        UpdateBatchCount();
        MainNavScroll.HorizontalScrollBarVisibility = compactHeader ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        var oneFilterColumn = width < 720;
        var fourFilterColumns = width >= 1100;
        ProductFilterGrid.ColumnDefinitions.Clear();
        ProductFilterGrid.RowDefinitions.Clear();
        if (fourFilterColumns)
        {
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ProductFilterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetFilterPosition(ProductSearchFilter, 0, 0);
            SetFilterPosition(ProductSourceFilter, 0, 1);
            SetFilterPosition(ProductSituationFilter, 0, 2);
            SetFilterPosition(ProductFamilyFilter, 0, 3);
            SetFilterPosition(SearchButton, 0, 4);
            Grid.SetColumnSpan(ProductSearchFilter, 1);
        }
        else if (oneFilterColumn)
        {
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < 5; row++)
                ProductFilterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetFilterPosition(ProductSearchFilter, 0, 0);
            SetFilterPosition(ProductSourceFilter, 1, 0);
            SetFilterPosition(ProductSituationFilter, 2, 0);
            SetFilterPosition(ProductFamilyFilter, 3, 0);
            SetFilterPosition(SearchButton, 4, 0);
            Grid.SetColumnSpan(ProductSearchFilter, 1);
        }
        else
        {
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            ProductFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < 3; row++)
                ProductFilterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetFilterPosition(ProductSearchFilter, 0, 0);
            Grid.SetColumnSpan(ProductSearchFilter, 2);
            SetFilterPosition(ProductSourceFilter, 1, 0);
            SetFilterPosition(ProductSituationFilter, 1, 1);
            SetFilterPosition(ProductFamilyFilter, 2, 0);
            SetFilterPosition(SearchButton, 2, 1);
        }

        DesignerGrid.ColumnDefinitions[0].Width = new GridLength(width < 720 ? 48 : width < 940 ? 52 : 56);
        DesignerGrid.ColumnDefinitions[2].Width = new GridLength(width < 720 ? 190 : width < 940 ? 220 : 266);

        var compactBatch = width < 1100;
        BatchSettingsGrid.ColumnDefinitions.Clear();
        BatchSettingsGrid.RowDefinitions.Clear();
        if (compactBatch)
        {
            for (var column = 0; column < 3; column++)
                BatchSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            BatchSettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BatchSettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetBatchSettingPosition(BatchTemplateSetting, 0, 0);
            SetBatchSettingPosition(BatchDateSetting, 0, 1);
            SetBatchSettingPosition(BatchPaperSetting, 0, 2);
            SetBatchSettingPosition(PageMarginsGroup, 1, 0);
            Grid.SetColumnSpan(PageMarginsGroup, 3);
        }
        else
        {
            BatchSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            BatchSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            BatchSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            BatchSettingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            BatchSettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetBatchSettingPosition(BatchTemplateSetting, 0, 0);
            SetBatchSettingPosition(BatchDateSetting, 0, 1);
            SetBatchSettingPosition(BatchPaperSetting, 0, 2);
            SetBatchSettingPosition(PageMarginsGroup, 0, 3);
            Grid.SetColumnSpan(PageMarginsGroup, 1);
        }

        var pageMargin = width < 720 ? 10 : width < 940 ? 16 : 24;
        ProductsGrid.MinHeight = compactVertical ? 60 : 100;
        if (Tabs.SelectedIndex is 0 or 1)
            if (Tabs.SelectedContent is FrameworkElement content && content is Border border
                && border.Child is DockPanel page)
                page.Margin = new Thickness(pageMargin, compactVertical ? 8 : width < 720 ? 10 : 16,
                    pageMargin, compactVertical ? 8 : width < 720 ? 10 : 16);
    }

    private static void SetFilterPosition(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private static void SetBatchSettingPosition(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    private void GoProductsClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 0;

    private void DocumentPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => LocalizeFindPrompt(DocumentPreview)));
    }

    private static void LocalizeFindPrompt(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock text && text.Text.Contains("Type text to find", StringComparison.OrdinalIgnoreCase))
                text.Text = "Digite o texto para localizar...";
            LocalizeFindPrompt(child);
        }
    }

    private void ThemeToggleClick(object sender, RoutedEventArgs e)
    {
        darkTheme = !darkTheme;
        var resources = System.Windows.Application.Current.Resources;
        resources["CanvasBrush"] = CreateBrush(darkTheme ? "#151A20" : "#F5F7FA");
        resources["SurfaceBrush"] = CreateBrush(darkTheme ? "#202832" : "#FFFFFF");
        resources["BorderBrush"] = CreateBrush(darkTheme ? "#35414D" : "#E1E7EF");
        resources["BorderStrongBrush"] = CreateBrush(darkTheme ? "#4C5B68" : "#C7D1DD");
        resources["TextPrimaryBrush"] = CreateBrush(darkTheme ? "#E6EDF3" : "#233247");
        resources["TextSecondaryBrush"] = CreateBrush(darkTheme ? "#A7B3C2" : "#66758A");
        resources["AccentBrush"] = CreateBrush(darkTheme ? "#458C91" : "#347E83");
        resources["AccentHoverBrush"] = CreateBrush(darkTheme ? "#76B5B6" : "#286B70");
        resources["AccentSoftBrush"] = CreateBrush(darkTheme ? "#293B40" : "#EBF4F4");
        resources["FocusRingBrush"] = CreateBrush(darkTheme ? "#80458C91" : "#80347E83");
        resources["DangerSoftBrush"] = CreateBrush(darkTheme ? "#402A2A" : "#FFF3F2");
        resources["DangerBorderBrush"] = CreateBrush(darkTheme ? "#72423F" : "#E8B8B4");
        resources["DangerTextBrush"] = CreateBrush(darkTheme ? "#FF9A90" : "#A12B24");
        ThemeToggle.ToolTip = darkTheme ? "Alternar para o tema claro" : "Alternar para o tema escuro";
        UpdateNavigationState();
    }

    private static SolidColorBrush CreateBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(640, area.Width);
        MinHeight = Math.Min(420, area.Height);
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = Math.Min(1280, area.Width);
        Height = Math.Min(900, area.Height);
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
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
                var message = $"{batch.FileName}\n\n{batch.Items.Count:N0} condições válidas\n{zero:N0} preços zerados (bloqueados para impressão)\n{pending} condições pendentes\n{batch.Families?.Count ?? 0:N0} famílias com descrição\n{batch.Issues.Count} linhas rejeitadas\n\nImportar os itens válidos?";
                if (batch.Items.Count == 0) { MessageBox.Show(this, "Nenhum item válido. Consulte o relatório."); continue; }
                if (MessageBox.Show(this, message, "Conferir importação", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) continue;
                cancellation.ThrowIfCancellationRequested();
                var imported = await Task.Run(() => store.Import(batch));
                report.AppendLine(imported ? "Importação concluída." : "Arquivo já importado; nenhuma duplicação (descrições de família foram atualizadas).");
            }
            RefreshImports(); RefreshProducts(); Status.Text = "Importação finalizada. Consulte o relatório para os detalhes.";
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

    private void RefreshImports() => ImportsGrid.ItemsSource = store.ListImports().Select(x => new ImportRow(x)).ToList();
    private void RefreshImportsClick(object sender, RoutedEventArgs e) => Run(RefreshImports);
    private string BackupBeforeMaintenance()
    {
        var directory = Path.Combine(App.DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"antes-limpeza-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
        store.Backup(path);
        return path;
    }
    private void DeleteImportClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var rows = ImportsGrid.SelectedItems.OfType<ImportRow>().ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Selecione uma ou mais importações (Ctrl/Shift+clique).");
        var withItems = rows.Count(x => x.Record.Managed);
        var registryOnly = rows.Count - withItems;
        var parts = new List<string>();
        if (withItems > 0)
            parts.Add($"{withItems} importação(ões) será(ão) excluída(s) junto com as condições exclusivas dela(s); itens compartilhados com outras importações são preservados");
        if (registryOnly > 0)
            parts.Add($"{registryOnly} sem itens associados terá(ão) só o registro removido, o que permite importar o arquivo de novo; nenhum produto é excluído");
        var names = string.Join(Environment.NewLine, rows.Take(6).Select(x => "• " + x.Name))
            + (rows.Count > 6 ? $"{Environment.NewLine}… e mais {rows.Count - 6}" : "");
        var question = $"{names}{Environment.NewLine}{Environment.NewLine}{string.Join("." + Environment.NewLine, parts)}.{Environment.NewLine}{Environment.NewLine}Continuar?";
        if (MessageBox.Show(this, question, "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = BackupBeforeMaintenance();
        var removed = 0;
        foreach (var row in rows) removed += store.DeleteImport(row.Record.Hash);
        if (removed > 0) { basket.Clear(); InvalidatePreview(); }
        RefreshImports(); RefreshProducts();
        Status.Text = $"{rows.Count} importação(ões) tratada(s); {removed} condição(ões) removida(s). Backup: {backup}";
    });
    private void ImportsGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.Key != Key.Delete) return;
        DeleteImportClick(sender, e); e.Handled = true;
    }
    private void CleanExpiredClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var expired = store.CountExpired(today);
        if (expired == 0)
        {
            Status.Text = "Nenhuma oferta ou promoção vencida para limpar.";
            MessageBox.Show(this, "Não há ofertas ou promoções vencidas. (As vencidas também são removidas automaticamente ao abrir o programa.)",
                "Limpar condições vencidas", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Excluir {expired} ofertas e promoções vencidas antes de {today:dd/MM/yyyy}?\n\nO catálogo, os layouts e o histórico serão preservados.",
            "Limpar condições vencidas", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = BackupBeforeMaintenance();
        var removed = store.DeleteExpired(today);
        basket.Clear(); InvalidatePreview(); RefreshImports(); RefreshProducts();
        Status.Text = $"Limpeza concluída: {removed} condições removidas. Backup: {backup}";
    });


    private void NewProductClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var item = new PriceItem("manual:" + Guid.NewGuid().ToString("N"), "1", "1", "",
            0, null, null, null, SourceKind.Catalog, null, 1, QuantityRule.UnitPrice,
            "Cadastro manual", "", 0);
        EditProductDialog(item, true);
    });
    private void EditProductClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var targets = Targets();
        if (targets.Count == 0) throw new InvalidOperationException("Marque ou destaque um produto, oferta ou promoção.");
        if (targets.Count > 1)
            throw new InvalidOperationException($"A edição funciona com um item por vez e há {targets.Count} selecionados. Desmarque os outros.");
        EditProductDialog(targets[0].Item, false);
    });
    private void DeleteProductClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var targets = Targets();
        if (targets.Count == 0) throw new InvalidOperationException("Marque ou destaque os registros que deseja excluir.");
        var nl = Environment.NewLine;
        var question = targets.Count == 1
            ? $"Excluir '{targets[0].Item.Description}'? O histórico já impresso será preservado."
            : $"Excluir {targets.Count} registros?{nl}{nl}"
              + string.Join(nl, targets.Take(6).Select(x => $"• {x.Item.DisplayCode} — {x.Item.Description}"))
              + (targets.Count > 6 ? $"{nl}… e mais {targets.Count - 6}" : "")
              + $"{nl}{nl}O histórico já impresso será preservado.";
        if (MessageBox.Show(this, question, "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ids = targets.Select(x => x.Item.Id).ToHashSet();
        var backup = BackupBeforeMaintenance();
        var removed = store.DeleteItems(ids);
        foreach (var row in basket.Where(x => ids.Contains(x.Item.Id)).ToList()) basket.Remove(row);
        InvalidatePreview(); RefreshProducts(); RefreshImports();
        Status.Text = $"{removed} registro(s) excluído(s). Backup: {backup}";
    });
    private void EditProductDialog(PriceItem item, bool creating)
    {
        var window = new Window
        {
            Owner = this, Title = creating ? "Cadastrar produto / condição" : "Editar produto / condição",
            Width = 580, Height = 680, MinWidth = 500, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var controls = new Dictionary<string, TextBox>();
        TextBox Field(string caption, string value)
        {
            panel.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 5, 0, 2) });
            var box = new TextBox { Text = value, MinWidth = 360 };
            panel.Children.Add(box); controls[caption] = box; return box;
        }
        Field("Código", item.DisplayCode);
        Field("Descrição", item.Description);
        Field("Subdescrição (opcional)", item.SubDescription ?? "");
        Field("Família", item.Family ?? "");
        Field("Descrição da família (opcional)", item.FamilyDescription ?? "");
        Field("Preço", item.Price.ToString(CultureInfo.CurrentCulture));
        Field("Preço normal (opcional)", item.NormalPrice?.ToString(CultureInfo.CurrentCulture) ?? "");
        Field("Quantidade", item.Quantity.ToString(CultureInfo.CurrentCulture));
        Field("Data início (dd/MM/aaaa, opcional)", item.Start?.ToString("dd/MM/yyyy") ?? "");
        Field("Data término (dd/MM/aaaa, opcional)", item.End?.ToString("dd/MM/yyyy") ?? "");
        Field("Código do grupo (opcional)", item.GroupCode ?? "");
        Field("Arquivo de origem", item.FileName);
        Field("Aba", item.Sheet);
        Field("Linha", item.Row.ToString(CultureInfo.InvariantCulture));
        panel.Children.Add(new TextBlock { Text = "Origem", Margin = new Thickness(0, 8, 0, 2) });
        var sources = new ComboBox { ItemsSource = Enum.GetValues<SourceKind>(), SelectedItem = item.Source };
        panel.Children.Add(sources);
        panel.Children.Add(new TextBlock { Text = "Regra da quantidade", Margin = new Thickness(0, 8, 0, 2) });
        var rules = new ComboBox { ItemsSource = Enum.GetValues<QuantityRule>(), SelectedItem = item.Rule };
        panel.Children.Add(rules);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Salvar alterações" };
        var cancel = new Button { Content = "Cancelar" };
        cancel.Click += (_, _) => window.DialogResult = false;
        save.Click += (_, _) =>
        {
            try
            {
                string Value(string key) => controls[key].Text.Trim();
                decimal ParseMoney(string key) => decimal.Parse(Value(key), NumberStyles.Number, CultureInfo.CurrentCulture);
                DateOnly? ParseDate(string key) => Value(key).Length == 0
                    ? null : DateOnly.ParseExact(Value(key), "dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
                var revised = item with
                {
                    DisplayCode = Value("Código"), Description = Value("Descrição"),
                    Family = Value("Família"),
                    SubDescription = Value("Subdescrição (opcional)") is { Length: > 0 } sub ? sub : null,
                    Price = ParseMoney("Preço"),
                    NormalPrice = Value("Preço normal (opcional)").Length == 0 ? null : ParseMoney("Preço normal (opcional)"),
                    Quantity = ParseMoney("Quantidade"),
                    Start = ParseDate("Data início (dd/MM/aaaa, opcional)"),
                    End = ParseDate("Data término (dd/MM/aaaa, opcional)"),
                    GroupCode = Value("Código do grupo (opcional)"),
                    FileName = Value("Arquivo de origem"), Sheet = Value("Aba"),
                    Row = int.Parse(Value("Linha"), CultureInfo.InvariantCulture),
                    Source = (SourceKind)sources.SelectedItem!,
                    Rule = (QuantityRule)rules.SelectedItem!
                };
                store.SaveItem(revised);
                var familyDescription = Value("Descrição da família (opcional)");
                if (revised.Family is { Length: > 0 } familyCode && familyDescription.Length > 0)
                    store.SaveFamily(new FamilyInfo(familyCode, familyDescription));
                foreach (var row in basket.Where(x => x.Item.Id == item.Id)) row.Item = revised with { Code = ProductCode.Normalize(revised.DisplayCode) };
                InvalidatePreview(); BatchGrid.Items.Refresh(); RefreshProducts();
                window.DialogResult = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
            {
                MessageBox.Show(window, ex.Message, "Revise os dados", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }

    private void RefreshProducts()
    {
        SourceKind? source = SourceFilter.SelectedIndex == 0 ? null : (SourceKind)(SourceFilter.SelectedIndex - 1);
        allProducts = store.Search(SearchText.Text, source).Select(x => new ProductRow(x)).ToList();
        foreach (var row in allProducts) row.PropertyChanged += OnProductRowChanged;
        var previous = FamilyFilter.SelectedItem as string;
        var families = allProducts.Select(x => x.FamilyLabel).Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        const string allFamilies = "Todas as famílias";
        loadingFilters = true;
        FamilyFilter.ItemsSource = new[] { allFamilies }.Concat(families).ToList();
        FamilyFilter.SelectedItem = previous is not null && families.Contains(previous) ? previous : allFamilies;
        loadingFilters = false;
        ApplyProductFilters();
    }
    private void ProductFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingFilters) ApplyProductFilters();
    }
    private void ApplyProductFilters()
    {
        if (ProductsGrid is null || StatusFilter is null || FamilyFilter is null) return;
        var family = FamilyFilter.SelectedIndex > 0 ? FamilyFilter.SelectedItem as string : null;
        IEnumerable<ProductRow> rows = allProducts;
        rows = StatusFilter.SelectedIndex switch
        {
            1 => rows.Where(x => !x.IsExpired),
            2 => rows.Where(x => x.IsExpired),
            3 => rows.Where(x => x.Level is "attention" or "zero"),
            _ => rows
        };
        if (family is not null)
            rows = rows.Where(x => string.Equals(x.FamilyLabel, family, StringComparison.CurrentCultureIgnoreCase));
        products = rows.ToList();
        ProductsGrid.ItemsSource = products;
        UpdateSelectionInfo();
        var hidden = allProducts.Count - products.Count;
        Status.Text = $"{products.Count} condições exibidas{(hidden > 0 ? $" ({hidden} ocultas pelos filtros)" : "")} • {basket.Count} produtos no lote.";
    }
    private void SearchClick(object sender, RoutedEventArgs e) => Run(RefreshProducts);
    private void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Run(RefreshProducts); }
    private IReadOnlyList<ProductRow> Targets()
    {
        var marked = products.Where(x => x.Selected).ToList();
        return marked.Count > 0 ? marked : ProductsGrid.SelectedItems.OfType<ProductRow>().ToList();
    }
    private bool bulkMarking;
    private void OnProductRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!bulkMarking && e.PropertyName == nameof(ProductRow.Selected)) UpdateSelectionInfo();
    }
    private void SetMarked(IEnumerable<ProductRow> rows, bool value)
    {
        bulkMarking = true;
        try { foreach (var row in rows) row.Selected = value; }
        finally { bulkMarking = false; }
        UpdateSelectionInfo();
    }
    private void ProductsSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionInfo();
    private void UpdateSelectionInfo()
    {
        if (ProductsGrid is null || SelectionInfo is null || AddFamiliesButton is null) return;
        var marked = products.Count(x => x.Selected);
        var highlighted = ProductsGrid.SelectedItems.Count;
        var count = marked > 0 ? marked : highlighted;
        SelectionInfo.Text = marked > 0 ? $"{marked} marcado(s): as ações valem para os marcados."
            : highlighted > 0 ? $"{highlighted} destacado(s): as ações valem para os destacados."
            : "Nenhum item selecionado: marque as caixas ou destaque linhas.";
        EditProductButton.IsEnabled = count == 1;
        ProductImageButton.IsEnabled = count == 1;
        FamilyImageButton.IsEnabled = Targets().Any(x => !string.IsNullOrWhiteSpace(x.Item.Family));
        DeleteProductButton.IsEnabled = count > 0;
        AddToBatchButton.IsEnabled = count > 0;
        AddFamiliesButton.IsEnabled = count > 0;
        ConfirmRuleButton.IsEnabled = Targets().Any(x => x.Item.Source == SourceKind.Promotion);
    }
    private void ProductsGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space)
        {
            var rows = ProductsGrid.SelectedItems.OfType<ProductRow>().ToList();
            if (rows.Count == 0) return;
            SetMarked(rows, !rows.All(x => x.Selected));
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && DeleteProductButton.IsEnabled)
        {
            DeleteProductClick(sender, e); e.Handled = true;
        }
    }
    private void MarkAllHeaderClick(object sender, RoutedEventArgs e) => SetMarked(products, ((CheckBox)sender).IsChecked == true);
    private void SelectAllClick(object sender, RoutedEventArgs e) => SetMarked(products, true);
    private void ClearSelectionClick(object sender, RoutedEventArgs e) => SetMarked(allProducts, false);
    private void MarkHighlightedClick(object sender, RoutedEventArgs e) => SetMarked(ProductsGrid.SelectedItems.OfType<ProductRow>().ToList(), true);

    private int ReadCopies()
    {
        ProductsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        return int.TryParse(CopiesText.Text, out var copies) && copies is >= 1 and <= 500
            ? copies : throw new InvalidOperationException("Informe de 1 a 500 cópias por item.");
    }
    private void UpsertBatch(PriceItem item, int copies)
    {
        var existing = basket.FirstOrDefault(x => x.Item.Id == item.Id);
        if (existing is null) basket.Add(new(item, copies));
        else { existing.Item = item; existing.Copies = copies; }
    }
    private void ShowSkipped(string title, string intro, IReadOnlyCollection<string> lines)
    {
        var nl = Environment.NewLine;
        var text = intro + nl + nl + string.Join(nl, lines.Take(12).Select(x => "• " + x))
            + (lines.Count > 12 ? $"{nl}… e mais {lines.Count - 12}." : "");
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddToBatchClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var copies = ReadCopies();
        var chosen = Targets();
        if (chosen.Count == 0) throw new InvalidOperationException("Marque ou destaque ao menos um produto.");
        var accepted = new List<ProductRow>(); var skipped = new List<string>();
        foreach (var group in chosen.GroupBy(x => x.Item.Code))
        {
            if (group.Count() > 1)
            {
                skipped.AddRange(group.Select(x => $"{x.Item.DisplayCode} — {x.Item.Description}: mais de uma condição escolhida ({x.Origin}); escolha só uma"));
                continue;
            }
            var row = group.Single();
            if (basket.Any(b => b.Item.Code == row.Item.Code && b.Item.Id != row.Item.Id))
            {
                skipped.Add($"{row.Item.DisplayCode} — {row.Item.Description}: já há outra condição deste produto no lote");
                continue;
            }
            accepted.Add(row);
        }
        foreach (var row in accepted) UpsertBatch(row.Item, copies);
        SetMarked(accepted, false); 
        BatchGrid.Items.Refresh(); InvalidatePreview();
        Status.Text = $"{accepted.Count} produto(s) adicionado(s); {basket.Count} no lote. Escolha o layout na aba Lote e impressão.";
        if (skipped.Count > 0)
            ShowSkipped(accepted.Count > 0 ? "Alguns itens ficaram de fora" : "Nada foi adicionado",
                accepted.Count > 0 ? "Os demais foram adicionados. Estes continuam marcados:" : "Estes itens não puderam entrar no lote:", skipped);
    });

    private void AddFamiliesToBatchClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var copies = ReadCopies();
        var chosen = Targets();
        if (chosen.Count == 0) throw new InvalidOperationException("Marque ou destaque ao menos um produto de cada família.");
        var keyed = chosen.Select(x => (Row: x, Key: FamilyPlates.Key(x.Item))).ToList();
        var skipped = keyed.Where(x => x.Key is null)
            .Select(x => $"{x.Row.Item.DisplayCode} — {x.Row.Item.Description}: sem família nem grupo de promoção com descrição").ToList();
        var plates = new List<PriceItem>(); var used = new List<ProductRow>();
        foreach (var group in keyed.Where(x => x.Key is not null).GroupBy(x => x.Key!))
        {
            var rows = group.Select(x => x.Row).ToList();
            try
            {
                var plate = FamilyPlates.Build(rows.Select(x => x.Item).ToList());
                if (basket.Any(b => b.Item.Code == plate.Code && b.Item.Id != plate.Id))
                    throw new InvalidOperationException("já há outra condição desta família no lote.");
                plates.Add(plate); used.AddRange(rows);
            }
            catch (InvalidOperationException ex)
            {
                var label = !string.IsNullOrWhiteSpace(rows[0].Item.Family) ? $"Família {rows[0].FamilyLabel}"
                    : $"Grupo {rows[0].Item.GroupCode} - {rows[0].Item.GroupDescription}";
                skipped.Add($"{label}: {ex.Message}");
            }
        }
        foreach (var plate in plates) UpsertBatch(plate, copies);
        SetMarked(used, false); 
        BatchGrid.Items.Refresh(); InvalidatePreview();
        Status.Text = $"{plates.Count} placa(s) de família/grupo adicionada(s); {basket.Count} no lote. Escolha o layout na aba Lote e impressão.";
        if (skipped.Count > 0)
            ShowSkipped(plates.Count > 0 ? "Alguns itens ficaram de fora" : "Nenhuma placa de família foi criada",
                plates.Count > 0 ? "As placas possíveis foram adicionadas. Estes itens continuam marcados:" : "Estes itens não puderam virar placa de família:", skipped);
    });

    private void ConfirmRuleClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var promotions = Targets().Where(x => x.Item.Source == SourceKind.Promotion).ToList();
        if (promotions.Count == 0) throw new InvalidOperationException("Marque ou destaque ao menos uma linha de promoção.");
        var nl = Environment.NewLine;
        var sample = string.Join(nl, promotions.Take(5).Select(x => $"• {x.Item.Description} — {x.Item.Quantity:0.##} un., valor {x.Item.Price:C2}"));
        var more = promotions.Count > 5 ? $"{nl}… e mais {promotions.Count - 5}" : "";
        var result = MessageBox.Show(this,
            $"Confira a condição no VR Master antes de prosseguir.{nl}{nl}{promotions.Count} promoção(ões):{nl}{sample}{more}{nl}{nl}SIM: o valor é o TOTAL do conjunto.{nl}NÃO: o valor é POR UNIDADE ao comprar a quantidade.{nl}CANCELAR: manter sem confirmação.",
            "Definir condição comercial", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return;
        var rule = result == MessageBoxResult.Yes ? QuantityRule.BundleTotal : QuantityRule.UnitPrice;
        foreach (var row in promotions)
        {
            store.ConfirmPromotion(row.Item.Id, rule);
            foreach (var b in basket.Where(x => x.Item.Id == row.Item.Id)) b.Item = b.Item with { Rule = rule };
        }
        InvalidatePreview(); RefreshProducts();
        Status.Text = $"Condição definida para {promotions.Count} promoção(ões).";
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
        previewTimer.Stop();
        BindDesigner();
    }
    private void CommitEditor()
    {
        Keyboard.ClearFocus(); 
        if (editing is null) throw new InvalidOperationException("Crie ou selecione um modelo.");
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
        QuantityRule.UnitPrice, "Exemplo", "", 0, null, "Pilha alcalina • pacote com 4 unidades");
    private void QueueEditorPreview()
    {
        if (editing is null) return;
        previewTimer.Stop();
        previewTimer.Start();
    }
    private void NewTemplateClick(object sender, RoutedEventArgs e) { editing = PlateTemplate.Default(); editing.Name = "Novo layout"; BindEditor(); }
    private void DuplicateTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        CommitEditor(); editing!.Id = Guid.NewGuid().ToString("N"); editing.Name += " — cópia"; BindEditor();
    });
    private void SaveTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        CommitEditor(); store.SaveTemplate(editing!); var id = editing!.Id;
        RefreshTemplates(id); InvalidatePreview(); Status.Text = "Layout salvo. Ele pode ser aplicado a todos os produtos do lote.";
    });
    private void DeleteTemplateClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (editing is null) return;
        if (MessageBox.Show(this, $"Excluir o layout '{editing.Name}'? O histórico será preservado.", "Excluir layout", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        store.DeleteTemplate(editing.Id); editing = null; BindDesigner(); RefreshTemplates(); InvalidatePreview();
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
    private void AddImageElementClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
    private void RequireEditingTemplate()
    {
        if (editing is null) throw new InvalidOperationException("Abra ou crie um layout antes de adicionar elementos.");
    }
    private static double AspectOf(byte[]? bytes)
        => ToBitmap(bytes) is { PixelHeight: > 0 } bitmap ? (double)bitmap.PixelWidth / bitmap.PixelHeight : 1;
    private void AddImageElement(string imageId, double aspect = 1)
    {
        RequireEditingTemplate();
        var box = Math.Min(40, Math.Min(editing!.WidthMm, editing.HeightMm));
        var width = aspect >= 1 ? box : box * aspect;
        var height = aspect >= 1 ? box / aspect : box;
        var element = new PlateElement
        {
            IsImage = true, Text = "", ImageId = imageId, X = 5, Y = 5, Background = "#00FFFFFF",
            Width = Math.Max(1, Math.Round(width, 1)), Height = Math.Max(1, Math.Round(height, 1))
        };
        AddElement(element);
        PreviewStatus.Text = "Imagem inserida no layout: arraste para posicionar. Ela fica ao fundo e os textos aparecem por cima.";
    }
    private void InsertImageFromFileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireEditingTemplate();
        var dialog = new OpenFileDialog { Filter = ImageFilter, Title = "Inserir imagem no layout" };
        if (dialog.ShowDialog(this) != true) return;
        var id = AddImageFile(dialog.FileName); 
        renderer.ClearImageCache(); RefreshImages();
        AddImageElement(id, AspectOf(store.GetImage(id)));
    });
    private void InsertImageFromLibraryClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireEditingTemplate();
        var (ok, id) = PickImageDialog("Escolher imagem para o layout", null);
        if (!ok || id is null) return;
        AddImageElement(id, AspectOf(store.GetImage(id)));
    });
    private void InsertProductImageSlotClick(object sender, RoutedEventArgs e) => Run(() => AddImageElement(ImageSources.Product));
    private void InsertFamilyImageSlotClick(object sender, RoutedEventArgs e) => Run(() => AddImageElement(ImageSources.Family));
    private void InsertProductOrFamilySlotClick(object sender, RoutedEventArgs e) => Run(() => AddImageElement(ImageSources.ProductOrFamily));

    internal static System.Windows.Media.Imaging.BitmapImage? ToBitmap(byte[]? bytes, int decodeWidth = 0)
    {
        if (bytes is not { Length: > 0 }) return null;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit(); image.Freeze();
        return image;
    }

    private void RefreshImages()
    {
        var infos = store.Images();
        ImagesGrid.ItemsSource = infos.Select(x => new ImageRow(x)).ToList();
        SetImageChoices(infos);
    }
    private void RefreshImageChoices() => SetImageChoices(store.Images());
    private void SetImageChoices(List<ImageInfo> infos)
    {
        imageChoices = [
            new(ImageSources.Product, "(imagem do produto)"),
            new(ImageSources.Family, "(imagem da família)"),
            new(ImageSources.ProductOrFamily, "(do produto; se não houver, da família)"),
            .. infos.Select(x => new ImageChoice(x.Id, x.Name))];
        RefreshInspectorImageChoices();
    }
    private void NotifyImagesChanged()
    {
        renderer.ClearImageCache(); RefreshImages(); RefreshProducts(); InvalidatePreview(); QueueEditorPreview();
    }

    private string AddImageFile(string path, string? name = null)
    {
        var info = new FileInfo(path);
        if (info.Length > 30 * 1024 * 1024) throw new InvalidOperationException($"{info.Name}: arquivo maior que 30 MB.");
        var source = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source));
        if (store.FindImage(hash) is { } existing) return existing;
        byte[] data, thumbnail;
        try { (data, thumbnail) = ImageTools.Prepare(source); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
            or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidOperationException($"{info.Name}: não é uma imagem válida ({ex.Message}).");
        }
        return store.AddImage(name ?? Path.GetFileNameWithoutExtension(path), hash, data, thumbnail);
    }

    private void AddImageClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter, Multiselect = true, Title = "Adicionar imagens à biblioteca" };
        if (dialog.ShowDialog(this) != true) return;
        var added = 0; var problems = new List<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var file in dialog.FileNames)
            {
                try { AddImageFile(file); added++; }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { problems.Add(ex.Message); }
            }
        }
        finally { Mouse.OverrideCursor = null; }
        NotifyImagesChanged();
        Status.Text = $"{added} imagem(ns) na biblioteca.";
        if (problems.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)), "Algumas imagens não foram adicionadas", MessageBoxButton.OK, MessageBoxImage.Warning);
    });

    private void ImportImageFolderClick(object sender, RoutedEventArgs e) => Run(() => ImportImageFolder(false));
    private void ImportFamilyImageFolderClick(object sender, RoutedEventArgs e) => Run(() => ImportImageFolder(true));
    private void ImportImageFolder(bool forFamilies)
    {
        var target = forFamilies ? "família" : "produto";
        var dialog = new OpenFolderDialog { Title = $"Pasta com imagens nomeadas pelo código da {target} (ex.: 10486.jpg)" };
        if (dialog.ShowDialog(this) != true) return;
        var files = Directory.EnumerateFiles(dialog.FolderName)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Nenhuma imagem (png, jpg, bmp, gif, tif) encontrada na pasta.");
        int linked = 0, skipped = 0; var problems = new List<string>();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            foreach (var file in files)
            {
                var code = Path.GetFileNameWithoutExtension(file).Trim();
                if (code.Length == 0 || !code.All(char.IsAsciiDigit)) { skipped++; continue; }
                try
                {
                    var imageId = AddImageFile(file);
                    if (forFamilies) store.SetFamilyImage(code, imageId);
                    else store.SetProductImage(ProductCode.Normalize(code), imageId);
                    linked++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { problems.Add(ex.Message); }
            }
        }
        finally { Mouse.OverrideCursor = null; }
        NotifyImagesChanged();
        var summary = $"{linked} imagem(ns) vinculada(s) a {(forFamilies ? "famílias" : "produtos")} pelo código.";
        var text = summary;
        if (skipped > 0) text += $"{Environment.NewLine}{skipped} arquivo(s) ignorado(s): o nome não é apenas o código da {target}.";
        if (problems.Count > 0) text += $"{Environment.NewLine}{problems.Count} com problema:{Environment.NewLine}" + string.Join(Environment.NewLine, problems.Take(8));
        Status.Text = summary;
        MessageBox.Show(this, text, "Importar imagens da pasta", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteImageClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var rows = ImagesGrid.SelectedItems.OfType<ImageRow>().ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Selecione uma ou mais imagens (Ctrl/Shift+clique).");
        var question = rows.Count == 1 ? $"Excluir a imagem '{rows[0].Name}'?" : $"Excluir {rows.Count} imagens?";
        var linked = rows.Sum(x => x.Products + x.Families);
        if (linked > 0) question += $"{Environment.NewLine}{Environment.NewLine}{linked} vínculo(s) com produtos ou famílias será(ão) removido(s).";
        if (MessageBox.Show(this, question, "Excluir imagem", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deleted = 0; var failed = new List<string>();
        foreach (var row in rows)
        {
            try { store.DeleteImage(row.Id); deleted++; }
            catch (InvalidOperationException ex) { failed.Add($"{row.Name}: {ex.Message}"); }
        }
        NotifyImagesChanged();
        Status.Text = $"{deleted} imagem(ns) excluída(s) da biblioteca.";
        if (failed.Count > 0) ShowSkipped("Algumas imagens não foram excluídas", "Estas imagens continuam na biblioteca:", failed);
    });
    private void ImagesGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.Key != Key.Delete) return;
        DeleteImageClick(sender, e); e.Handled = true;
    }
    private void ImagesCellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Run(() =>
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ImageRow row || e.EditingElement is not TextBox box) return;
        var name = box.Text.Trim();
        if (name.Length == 0) { e.Cancel = true; throw new InvalidOperationException("O nome da imagem não pode ficar vazio."); }
        store.RenameImage(row.Id, name);
        Dispatcher.BeginInvoke(new Action(RefreshImageChoices));
    });

    private void ProductImageClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var targets = Targets();
        if (targets.Count == 0) throw new InvalidOperationException("Marque ou destaque o produto.");
        if (targets.Count > 1)
            throw new InvalidOperationException($"A imagem é definida para um produto por vez e há {targets.Count} selecionados. Para muitos produtos, use a aba Imagens e importe uma pasta com arquivos nomeados pelo código.");
        var row = targets[0];
        var (ok, imageId) = PickImageDialog($"Imagem do produto {row.Item.DisplayCode}", row.Item.ImageId);
        if (!ok) return;
        store.SetProductImage(row.Item.Code, imageId);
        var marked = allProducts.Where(x => x.Selected).Select(x => x.Item.Id).ToHashSet();
        NotifyImagesChanged();
        SetMarked(allProducts.Where(x => marked.Contains(x.Item.Id)), true);
        Status.Text = imageId is null
            ? "Imagem do produto removida."
            : "Imagem vinculada ao produto (vale para catálogo, ofertas e promoções dele).";
    });

    private void FamilyImageClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var targets = Targets();
        var families = targets.Where(x => !string.IsNullOrWhiteSpace(x.Item.Family))
            .GroupBy(x => FamilyCode.Key(x.Item.Family!)).ToList();
        if (families.Count == 0) throw new InvalidOperationException("Os itens selecionados não têm família.");
        var first = families[0].First();
        var title = families.Count == 1 ? $"Imagem da família {first.FamilyLabel}" : $"Imagem de {families.Count} famílias";
        var (ok, imageId) = PickImageDialog(title, families.Count == 1 ? first.Item.FamilyImageId : null);
        if (!ok) return;
        foreach (var family in families) store.SetFamilyImage(family.First().Item.Family!, imageId);
        var marked = allProducts.Where(x => x.Selected).Select(x => x.Item.Id).ToHashSet();
        NotifyImagesChanged();
        SetMarked(allProducts.Where(x => marked.Contains(x.Item.Id)), true);
        var withoutFamily = targets.Count - families.Sum(g => g.Count());
        Status.Text = (imageId is null ? "Imagem removida de " : "Imagem vinculada a ") + $"{families.Count} família(s)"
            + (withoutFamily > 0 ? $"; {withoutFamily} item(ns) sem família foram ignorados." : ".");
    });

    private (bool Ok, string? ImageId) PickImageDialog(string title, string? currentId)
    {
        var window = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var combo = new ComboBox { DisplayMemberPath = nameof(ImageChoice.Name), SelectedValuePath = nameof(ImageChoice.Id), Margin = new Thickness(0, 0, 0, 8) };
        var preview = new Image { Height = 180, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
        (bool Ok, string? ImageId) result = (false, null);
        void Load(string? selected)
        {
            var choices = new List<ImageChoice> { new("", "(sem imagem)") };
            choices.AddRange(store.Images().Select(i => new ImageChoice(i.Id, i.Name)));
            combo.ItemsSource = choices;
            combo.SelectedValue = string.IsNullOrWhiteSpace(selected) ? "" : selected;
        }
        combo.SelectionChanged += (_, _) =>
            preview.Source = combo.SelectedValue is string chosen && chosen.Length > 0 ? ToBitmap(store.GetImage(chosen), 360) : null;
        var add = new Button { Content = "Adicionar nova imagem…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3) };
        add.Click += (_, _) => Run(() =>
        {
            var dialog = new OpenFileDialog { Filter = ImageFilter, Title = "Escolher imagem" };
            if (dialog.ShowDialog(window) != true) return;
            var newId = AddImageFile(dialog.FileName);
            renderer.ClearImageCache(); RefreshImages(); Load(newId);
        });
        var save = new Button { Content = "Salvar", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            result = (true, combo.SelectedValue is string chosen && chosen.Length > 0 ? chosen : null);
            window.DialogResult = true;
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Escolha a imagem na biblioteca:", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(combo); panel.Children.Add(add); panel.Children.Add(preview); panel.Children.Add(buttons);
        window.Content = panel;
        Load(currentId);
        window.ShowDialog();
        return result;
    }

    private static List<FontChoice>? fontChoices;

    private string? PickFontDialog(string? current)
    {
        var fonts = fontChoices ??= System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => new FontChoice(f.Source, f))
            .GroupBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var window = new Window
        {
            Title = "Escolher tipo de letra", Width = 400, Height = 560, Owner = this, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 320, MinHeight = 380
        };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Digite para filtrar a lista" };
        var info = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(2, 0, 0, 6) };
        var list = new ListBox { ItemsSource = fonts };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FontChoice.Name)));
        text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontChoice.Family)));
        text.SetValue(TextBlock.FontSizeProperty, 16.0);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(6, 3, 6, 3));
        list.ItemTemplate = new DataTemplate { VisualTree = text };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);

        var preview = new TextBlock
        {
            Text = "OFERTA R$ 12,99 — Pão de queijo 350 ml", FontSize = 22, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8), MinHeight = 64
        };
        var use = new Button { Content = "Usar", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var typed = new Button { Content = "Usar o texto digitado", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(typed); buttons.Children.Add(use); buttons.Children.Add(cancel);

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(fonts);
        string? result = null;

        void ApplyFilter()
        {
            var filter = search.Text.Trim();
            if (filter.Length == 0) view.Filter = null;
            else view.Filter = o => o is FontChoice f
                && System.Globalization.CultureInfo.CurrentCulture.CompareInfo.IndexOf(f.Name, filter,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
            if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
            info.Text = $"{list.Items.Count} de {fonts.Count} tipos de letra";
            var exact = fonts.Any(f => string.Equals(f.Name, filter, StringComparison.OrdinalIgnoreCase));
            typed.Visibility = filter.Length > 0 && !exact ? Visibility.Visible : Visibility.Collapsed;
        }
        void Accept()
        {
            if (list.SelectedItem is FontChoice selected) result = selected.Name;
            else if (search.Text.Trim().Length > 0) result = search.Text.Trim();
            else return;
            window.DialogResult = true;
        }

        search.TextChanged += (_, _) => ApplyFilter();
        search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Down) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); args.Handled = true; }
            else if (args.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); args.Handled = true; }
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not FontChoice selected) return;
            preview.FontFamily = selected.Family;
            list.ScrollIntoView(selected);
        };
        list.MouseDoubleClick += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is ListBoxItem) Accept();
        };
        use.Click += (_, _) => Accept();
        typed.Click += (_, _) => { result = search.Text.Trim(); window.DialogResult = true; };
        window.Loaded += (_, _) =>
        {
            var currentChoice = fonts.FirstOrDefault(f => string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase));
            if (currentChoice is not null) { list.SelectedItem = currentChoice; list.ScrollIntoView(currentChoice); }
            info.Text = $"{list.Items.Count} de {fonts.Count} tipos de letra";
            search.Focus();
        };

        var header = new StackPanel();
        header.Children.Add(search); header.Children.Add(info);
        var footer = new StackPanel();
        footer.Children.Add(preview); footer.Children.Add(buttons);
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header); root.Children.Add(footer); root.Children.Add(list);
        window.Content = root;
        try { window.ShowDialog(); }
        finally { view.Filter = null; }
        return result;
    }

    private static double ParseMm(TextBox box, string name)
        => double.TryParse(box.Text, out var value) && double.IsFinite(value)
            ? value : throw new InvalidOperationException($"{name} inválido(a).");
    private (double Width, double Height, PageMargins Margins, double Gap) Paper()
    {
        var (w, h) = PaperBox.SelectedIndex switch { 1 => (297d, 420d), 2 => (148d, 210d), _ => (210d, 297d) };
        if (Landscape.IsChecked == true) (w, h) = (h, w);
        var margins = new PageMargins(
            ParseMm(MarginTopText, "Margem superior"), ParseMm(MarginRightText, "Margem direita"),
            ParseMm(MarginBottomText, "Margem inferior"), ParseMm(MarginLeftText, "Margem esquerda"));
        return (w, h, margins, ParseMm(GapText, "Espaçamento"));
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
        var document = renderer.Document(batch, paper.Width, paper.Height, paper.Margins, paper.Gap, CutMarks.IsChecked == true);
        currentBatch = batch; DocumentPreview.Document = document;
        store.SaveHistory(batch, "Prévia gerada", $"{batch.Items.Sum(x => x.Copies)} placas; {document.Pages.Count} páginas");
        RefreshHistory(); Status.Text = "Prévia gerada. A impressão usa o layout e os dados atuais do lote.";
    });
    private void PrintClick(object sender, RoutedEventArgs e) => Run(() =>
    {
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
        static double Mm(double dip) => Math.Max(0, dip) * 25.4 / 96;
        var margins = new PageMargins(
            Math.Max(paper.Margins.Top, area is null ? 0 : Mm(area.OriginHeight)),
            Math.Max(paper.Margins.Right, area is null ? 0 : Mm(width - area.OriginWidth - area.ExtentWidth)),
            Math.Max(paper.Margins.Bottom, area is null ? 0 : Mm(height - area.OriginHeight - area.ExtentHeight)),
            Math.Max(paper.Margins.Left, area is null ? 0 : Mm(area.OriginWidth)));
        var document = renderer.Document(batch, width * 25.4 / 96, height * 25.4 / 96, margins, paper.Gap, CutMarks.IsChecked == true);
        DocumentPreview.Document = null;
        currentBatch = batch;
        if (!ReviewPrint(document, $"{dialog.PrintQueue.Name} • {document.Pages.Count} páginas • margens sup/dir/inf/esq {margins.Top:0.#}/{margins.Right:0.#}/{margins.Bottom:0.#}/{margins.Left:0.#} mm"))
        {
            DocumentPreview.Document = document;
            return;
        }
        store.SaveHistory(batch, "Envio iniciado", dialog.PrintQueue.Name);
        try
        {
            dialog.PrintDocument(document.DocumentPaginator, $"Placas {batch.Id[..8]}");
            store.SaveHistory(batch, "Enviado à fila", dialog.PrintQueue.Name);
            Status.Text = "Lote enviado à fila. Confira a saída física na impressora.";
        }
        catch (Exception ex) { store.SaveHistory(batch, "Envio não confirmado", ex.Message); throw; }
        finally { DocumentPreview.Document = document; RefreshHistory(); }
    });
    private bool ReviewPrint(System.Windows.Documents.FixedDocument document, string title)
    {
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Owner = this, Title = "Conferir impressão — " + title,
            Width = Math.Min(980, area.Width), Height = Math.Min(720, area.Height),
            MaxWidth = area.Width, MaxHeight = area.Height,
            MinWidth = Math.Min(440, area.Width), MinHeight = Math.Min(320, area.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel(); var button = new Button { Content = "Confirmar envio à impressora", Margin = new Thickness(12) };
        button.Click += (_, _) => window.DialogResult = true;
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var viewer = new DocumentViewer { Document = document };
        viewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, (_, args) => args.Handled = true, (_, args) => { args.CanExecute = false; args.Handled = true; }));
        panel.Children.Add(viewer); window.Content = panel;
        var confirmed = window.ShowDialog() == true;
        viewer.Document = null;
        return confirmed;
    }
    private void RemoveBatchItemClick(object sender, RoutedEventArgs e)
    {
        var rows = BatchGrid.SelectedItems.OfType<BatchRow>().ToList();
        if (rows.Count == 0) { Status.Text = "Destaque os itens do lote que deseja remover (Ctrl/Shift+clique para vários)."; return; }
        foreach (var row in rows) basket.Remove(row);
        InvalidatePreview();
        Status.Text = $"{rows.Count} item(ns) removido(s) do lote; restam {basket.Count}.";
    }
    private void ClearBatchClick(object sender, RoutedEventArgs e)
    {
        if (basket.Count == 0) return;
        if (basket.Count > 1 && MessageBox.Show(this, $"Remover todos os {basket.Count} itens do lote?", "Limpar lote",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        basket.Clear(); InvalidatePreview();
    }
    private void BatchGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.Key != Key.Delete) return;
        RemoveBatchItemClick(sender, e); e.Handled = true;
    }
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