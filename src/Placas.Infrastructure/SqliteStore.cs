using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Placas.Application;
using Placas.Domain;

namespace Placas.Infrastructure;

public sealed class SqliteStore : IStore
{
    private readonly string connectionString;
    public SqliteStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30 }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS imports(hash TEXT PRIMARY KEY, name TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS import_items(import_hash TEXT NOT NULL, item_id TEXT NOT NULL, PRIMARY KEY(import_hash,item_id));
            CREATE TABLE IF NOT EXISTS items(id TEXT PRIMARY KEY, code TEXT NOT NULL, search TEXT NOT NULL, source INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_items_code ON items(code);
            CREATE INDEX IF NOT EXISTS ix_items_source ON items(source);
            CREATE TABLE IF NOT EXISTS templates(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id TEXT PRIMARY KEY, created TEXT NOT NULL, status TEXT NOT NULL, detail TEXT NOT NULL, json TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        if (Templates().Count == 0) SaveTemplate(PlateTemplate.Default());
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open(); return connection;
    }
    public bool Import(ImportBatch batch)
    {
        if (batch.Items.Count == 0) throw new InvalidOperationException("O arquivo não contém itens válidos.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var insert = db.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO imports VALUES ($hash,$name,$date)";
        insert.Parameters.AddWithValue("$hash", batch.Hash);
        insert.Parameters.AddWithValue("$name", batch.FileName);
        insert.Parameters.AddWithValue("$date", DateTimeOffset.Now.ToString("O"));
        if (insert.ExecuteNonQuery() == 0) return false;
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO items VALUES ($id,$code,$search,$source,$json) ON CONFLICT(id) DO UPDATE SET code=$code, search=$search, source=$source, json=$json";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var code = command.Parameters.Add("$code", SqliteType.Text);
        var search = command.Parameters.Add("$search", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Integer);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        command.Prepare();
        foreach (var item in batch.Items)
        {
            id.Value = item.Id; code.Value = item.Code; search.Value = SearchKey(item.Description);
            source.Value = (int)item.Source; json.Value = JsonSerializer.Serialize(item);
            command.ExecuteNonQuery();
        }
        using var link = db.CreateCommand(); link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO import_items(import_hash,item_id) VALUES($hash,$id)";
        link.Parameters.AddWithValue("$hash", batch.Hash);
        var linkedId = link.Parameters.Add("$id", SqliteType.Text);
        foreach (var item in batch.Items) { linkedId.Value = item.Id; link.ExecuteNonQuery(); }
        transaction.Commit(); return true;
    }

    public List<ImportRecord> ListImports()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT i.hash,i.name,i.created,COUNT(l.item_id)
            FROM imports i LEFT JOIN import_items l ON l.import_hash=i.hash
            GROUP BY i.hash,i.name,i.created ORDER BY i.created DESC
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ImportRecord>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(3)>0));
        return result;
    }

    public int DeleteImport(string hash)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var count = db.CreateCommand(); count.Transaction = tx;
        count.CommandText = "SELECT COUNT(*) FROM import_items WHERE import_hash=$hash";
        count.Parameters.AddWithValue("$hash", hash);
        if (Convert.ToInt32(count.ExecuteScalar()) == 0)
            throw new InvalidOperationException("Importação sem vínculos rastreáveis; não é possível excluí-la individualmente com segurança.");
        using var delete = db.CreateCommand(); delete.Transaction = tx;
        delete.CommandText = """
            DELETE FROM items WHERE id IN
            (SELECT item_id FROM import_items WHERE import_hash=$hash)
            AND NOT EXISTS
            (SELECT 1 FROM import_items other
             WHERE other.item_id=items.id AND other.import_hash<>$hash)
            """;
        delete.Parameters.AddWithValue("$hash", hash);
        var removed = delete.ExecuteNonQuery();
        using var unlink = db.CreateCommand(); unlink.Transaction = tx;
        unlink.CommandText = "DELETE FROM import_items WHERE import_hash=$hash";
        unlink.Parameters.AddWithValue("$hash", hash); unlink.ExecuteNonQuery();
        using var imported = db.CreateCommand(); imported.Transaction = tx;
        imported.CommandText = "DELETE FROM imports WHERE hash=$hash";
        imported.Parameters.AddWithValue("$hash", hash); imported.ExecuteNonQuery();
        tx.Commit(); return removed;
    }

    public int CountExpired(DateOnly date)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE source IN (1,2) AND json_extract(json,'$.End') IS NOT NULL AND json_extract(json,'$.End') < $today";
        cmd.Parameters.AddWithValue("$today", date.ToString("yyyy-MM-dd"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int DeleteExpired(DateOnly date)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var remove = db.CreateCommand(); remove.Transaction = tx;
        remove.CommandText = """
            DELETE FROM items
            WHERE source IN (1,2) AND json_extract(json,'$.End') IS NOT NULL
              AND json_extract(json,'$.End') < $today
            """;
        remove.Parameters.AddWithValue("$today", date.ToString("yyyy-MM-dd"));
        var affected = remove.ExecuteNonQuery();
        using var orphan = db.CreateCommand(); orphan.Transaction = tx;
        orphan.CommandText = "DELETE FROM import_items WHERE item_id NOT IN (SELECT id FROM items)";
        orphan.ExecuteNonQuery();
        tx.Commit(); return affected;
    }

    private static string SearchKey(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    public List<PriceItem> Search(string text, SourceKind? source, int limit = 500)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM items WHERE ($source IS NULL OR source=$source) AND (instr(search,$text)>0 OR code=$code) ORDER BY search,source LIMIT $limit";
        command.Parameters.AddWithValue("$source", source is null ? DBNull.Value : (object)(int)source);
        command.Parameters.AddWithValue("$text", SearchKey(text.Trim()));
        command.Parameters.AddWithValue("$code", text.Trim().TrimStart('0'));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        using var reader = command.ExecuteReader(); var result = new List<PriceItem>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!);
        return result;
    }
    public void ConfirmPromotion(string itemId, QuantityRule rule)
    {
        if (rule == QuantityRule.Pending) throw new InvalidOperationException("Selecione uma regra confirmada.");
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var read = db.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT json FROM items WHERE id=$id"; read.Parameters.AddWithValue("$id", itemId);
        var item = JsonSerializer.Deserialize<PriceItem>((string?)read.ExecuteScalar() ?? throw new InvalidOperationException("Item não encontrado."))!;
        if (item.Source != SourceKind.Promotion) throw new InvalidOperationException("Selecione uma promoção.");
        using var write = db.CreateCommand(); write.Transaction = transaction;
        write.CommandText = "UPDATE items SET json=$json WHERE id=$id";
        write.Parameters.AddWithValue("$id", itemId);
        write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item with { Rule = rule }));
        write.ExecuteNonQuery(); transaction.Commit();
    }
    public List<PlateTemplate> Templates()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM templates ORDER BY rowid";
        using var reader = command.ExecuteReader(); var result = new List<PlateTemplate>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PlateTemplate>(reader.GetString(0))!);
        return result;
    }
    public void SaveTemplate(PlateTemplate template)
    {
        PlateRules.ValidateTemplate(template);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO templates VALUES ($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        command.Parameters.AddWithValue("$id", template.Id); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(template));
        command.ExecuteNonQuery();
    }
    public void DeleteTemplate(string id)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM templates WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    public void SaveHistory(PrintBatch batch, string status, string detail)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO history VALUES($id,$date,$status,$detail,$json) ON CONFLICT(id) DO UPDATE SET status=$status,detail=$detail";
        command.Parameters.AddWithValue("$id", batch.Id); command.Parameters.AddWithValue("$date", batch.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(batch)); command.ExecuteNonQuery();
    }
    public List<HistoryEntry> History()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT id,created,status,detail,json FROM history ORDER BY created DESC LIMIT 200";
        using var reader = command.ExecuteReader(); var result = new List<HistoryEntry>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), JsonSerializer.Deserialize<PrintBatch>(reader.GetString(4))!));
        return result;
    }
    public void Backup(string path)
    {
        var original = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Escolha outro arquivo para o backup.");
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        target.Open(); source.BackupDatabase(target);
    }
}
