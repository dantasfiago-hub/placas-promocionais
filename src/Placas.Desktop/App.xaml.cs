using System.Globalization;
using System.IO;
using System.Windows;
using Placas.Infrastructure;

namespace Placas.Desktop;

public partial class App : System.Windows.Application
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlacasPromocionais");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR")));
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show(args.Exception.Message, "Não foi possível concluir", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var store = new SqliteStore(Path.Combine(DataDirectory, "placas.db"));
            var backups = Path.Combine(DataDirectory, "backups"); Directory.CreateDirectory(backups);
            var latest = Directory.GetFiles(backups, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) > TimeSpan.FromDays(7))
            {
                store.Backup(Path.Combine(backups, $"placas-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
                foreach (var old in Directory.GetFiles(backups, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).Skip(4)) File.Delete(old);
            }
            new MainWindow(store).Show();
        }
        catch (Exception ex) { Log(ex); MessageBox.Show(ex.Message, "Erro na inicialização"); Shutdown(1); }
    }
    public static void Log(Exception ex)
    {
        try
        {
            var folder = Path.Combine(DataDirectory, "logs"); Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"app-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(file) || new FileInfo(file).Length < 2_000_000)
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {ex}\n");
            foreach (var old in Directory.GetFiles(folder, "*.log").OrderByDescending(x => x).Skip(7)) File.Delete(old);
        }
        catch { /* Logging must not mask the original exception. */ }
    }
}
