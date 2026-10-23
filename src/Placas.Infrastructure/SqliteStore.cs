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
            CREATE TABLE IF NOT EXISTS families(key TEXT PRIMARY KEY, code TEXT NOT NULL, description TEXT NOT NULL);
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
        if (insert.ExecuteNonQuery() == 0)
        {
            // Arquivo já importado: só atualiza as descrições de família lidas dele, sem duplicar itens.
            foreach (var family in batch.Families ?? []) UpsertFamily(db, transaction, family);
            foreach (var known in batch.Items.Where(x => !string.IsNullOrWhiteSpace(x.GroupDescription)))
                UpdateGroupDescription(db, transaction, known.Id, known.GroupDescription!);
            transaction.Commit();
            return false;
        }
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
            // A família vem do catálogo, quando o relatório de oferta/promoção não a informa.
            var imported = item;
            if (item.Source != SourceKind.Catalog && string.IsNullOrWhiteSpace(item.Family))
            {
                using var familyQuery = db.CreateCommand(); familyQuery.Transaction = transaction;
                familyQuery.CommandText = "SELECT json FROM items WHERE code=$code AND source=0 LIMIT 1";
                familyQuery.Parameters.AddWithValue("$code", item.Code);
                if (familyQuery.ExecuteScalar() is string catalogJson)
                    imported = item with { Family = JsonSerializer.Deserialize<PriceItem>(catalogJson)?.Family };
            }
            // Preserva a subdescrição cadastrada manualmente quando o arquivo importado não a informa.
            if (string.IsNullOrWhiteSpace(imported.SubDescription))
            {
                using var subQuery = db.CreateCommand(); subQuery.Transaction = transaction;
                subQuery.CommandText = "SELECT json FROM items WHERE id=$id";
                subQuery.Parameters.AddWithValue("$id", imported.Id);
                if (subQuery.ExecuteScalar() is string existingJson &&
                    JsonSerializer.Deserialize<PriceItem>(existingJson)?.SubDescription is { Length: > 0 } keptSub)
                    imported = imported with { SubDescription = keptSub };
            }
            id.Value = imported.Id; code.Value = imported.Code; search.Value = SearchKey(imported.Description);
            source.Value = (int)imported.Source; json.Value = JsonSerializer.Serialize(imported with { FamilyDescription = null });
            command.ExecuteNonQuery();
        }
        using var link = db.CreateCommand(); link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO import_items(import_hash,item_id) VALUES($hash,$id)";
        link.Parameters.AddWithValue("$hash", batch.Hash);
        var linkedId = link.Parameters.Add("$id", SqliteType.Text);
        foreach (var item in batch.Items) { linkedId.Value = item.Id; link.ExecuteNonQuery(); }
        foreach (var family in batch.Families ?? [])
            UpsertFamily(db, transaction, family);
        // Atualiza a família das condições existentes ao importar um catálogo posteriormente.
        if (batch.Items.Any(x => x.Source == SourceKind.Catalog && !string.IsNullOrWhiteSpace(x.Family)))
        {
            using var select = db.CreateCommand(); select.Transaction = transaction;
            select.CommandText = "SELECT id,json FROM items WHERE source<>0";
            var changes = new List<PriceItem>();
            using (var reader = select.ExecuteReader())
                while (reader.Read())
                {
                    var existing = JsonSerializer.Deserialize<PriceItem>(reader.GetString(1))!;
                    var catalog = batch.Items.FirstOrDefault(x => x.Source == SourceKind.Catalog && x.Code == existing.Code);
                    if (catalog is not null && !string.IsNullOrWhiteSpace(catalog.Family) && string.IsNullOrWhiteSpace(existing.Family))
                        changes.Add(existing with { Family = catalog.Family });
                }
            foreach (var updated in changes)
            {
                using var update = db.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE items SET json=$json WHERE id=$id";
                update.Parameters.AddWithValue("$id", updated.Id);
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(updated));
                update.ExecuteNonQuery();
            }
        }
        transaction.Commit(); return true;
    }


    public void SaveItem(PriceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Description) || string.IsNullOrWhiteSpace(item.DisplayCode))
            throw new InvalidOperationException("Código e descrição são obrigatórios.");
        if (item.Price < 0 || item.NormalPrice < 0 || item.Quantity < 1 ||
            item.Start > item.End || (item.Source == SourceKind.Promotion && item.Quantity != decimal.Truncate(item.Quantity)))
            throw new InvalidOperationException("Preço, quantidade ou período inválidos.");
        // A descrição da família vive na tabela "families"; não é gravada junto do item.
        var normalized = item with { Code = ProductCode.Normalize(item.DisplayCode), FamilyDescription = null };
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO items(id,code,search,source,json) VALUES($id,$code,$search,$source,$json)
            ON CONFLICT(id) DO UPDATE SET code=$code, search=$search, source=$source, json=$json
            """;
        command.Parameters.AddWithValue("$id", normalized.Id);
        command.Parameters.AddWithValue("$code", normalized.Code);
        command.Parameters.AddWithValue("$search", SearchKey(normalized.Description));
        command.Parameters.AddWithValue("$source", (int)normalized.Source);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(normalized));
        command.ExecuteNonQuery();
    }

    public void SaveFamily(FamilyInfo family)
    {
        if (string.IsNullOrWhiteSpace(family.Code) || string.IsNullOrWhiteSpace(family.Description))
            throw new InvalidOperationException("Informe o código e a descrição da família.");
        using var db = Open(); using var tx = db.BeginTransaction();
        UpsertFamily(db, tx, family);
        tx.Commit();
    }

    // Itens importados antes de a descrição do grupo ser lida ganham o valor ao reler o mesmo arquivo.
    private static void UpdateGroupDescription(SqliteConnection db, SqliteTransaction tx, string id, string description)
    {
        using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT json FROM items WHERE id=$id"; read.Parameters.AddWithValue("$id", id);
        if (read.ExecuteScalar() is not string json) return;
        var item = JsonSerializer.Deserialize<PriceItem>(json)!;
        if (item.GroupDescription == description) return;
        using var write = db.CreateCommand(); write.Transaction = tx;
        write.CommandText = "UPDATE items SET json=$json WHERE id=$id";
        write.Parameters.AddWithValue("$id", id);
        write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item with { GroupDescription = description, FamilyDescription = null }));
        write.ExecuteNonQuery();
    }

    private static void UpsertFamily(SqliteConnection db, SqliteTransaction tx, FamilyInfo family)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO families(key,code,description) VALUES($key,$code,$description) ON CONFLICT(key) DO UPDATE SET code=$code, description=$description";
        command.Parameters.AddWithValue("$key", FamilyCode.Key(family.Code));
        command.Parameters.AddWithValue("$code", family.Code.Trim());
        command.Parameters.AddWithValue("$description", family.Description.Trim());
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> LoadFamilies(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT key,description FROM families";
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public void DeleteItem(string id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var affected = ImportHashes(db, tx, "SELECT import_hash FROM import_items WHERE item_id=$id", ("$id", id));
        using var links = db.CreateCommand(); links.Transaction = tx;
        links.CommandText = "DELETE FROM import_items WHERE item_id=$id";
        links.Parameters.AddWithValue("$id", id); links.ExecuteNonQuery();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "DELETE FROM items WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        RemoveEmptyImports(db, tx, affected);
        tx.Commit();
    }

    private static List<string> ImportHashes(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    // Uma importação que perdeu todos os itens não tem mais o que gerenciar: remove o registro,
    // o que também permite importar o mesmo arquivo novamente.
    private static void RemoveEmptyImports(SqliteConnection db, SqliteTransaction tx, IEnumerable<string> hashes)
    {
        foreach (var hash in hashes.Distinct())
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "DELETE FROM imports WHERE hash=$hash AND NOT EXISTS (SELECT 1 FROM import_items WHERE import_hash=$hash)";
            command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery();
        }
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
        {
            // Sem itens associados (já removidos ou anterior ao rastreamento): só o registro é apagado.
            using var registry = db.CreateCommand(); registry.Transaction = tx;
            registry.CommandText = "DELETE FROM imports WHERE hash=$hash";
            registry.Parameters.AddWithValue("$hash", hash); registry.ExecuteNonQuery();
            tx.Commit(); return 0;
        }
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
        const string expiredFilter = "source IN (1,2) AND json_extract(json,'$.End') IS NOT NULL AND json_extract(json,'$.End') < $today";
        var today = date.ToString("yyyy-MM-dd");
        using var db = Open(); using var tx = db.BeginTransaction();
        var affected = ImportHashes(db, tx,
            $"SELECT DISTINCT import_hash FROM import_items WHERE item_id IN (SELECT id FROM items WHERE {expiredFilter})",
            ("$today", today));
        using var remove = db.CreateCommand(); remove.Transaction = tx;
        remove.CommandText = $"DELETE FROM items WHERE {expiredFilter}";
        remove.Parameters.AddWithValue("$today", today);
        var removed = remove.ExecuteNonQuery();
        using var orphan = db.CreateCommand(); orphan.Transaction = tx;
        orphan.CommandText = "DELETE FROM import_items WHERE item_id NOT IN (SELECT id FROM items)";
        orphan.ExecuteNonQuery();
        // Arquivos de oferta/promoção cujos itens venceram todos saem da lista de importações.
        RemoveEmptyImports(db, tx, affected);
        tx.Commit(); return removed;
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
        var families = LoadFamilies(db);
        using var reader = command.ExecuteReader(); var result = new List<PriceItem>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!;
            result.Add(string.IsNullOrWhiteSpace(item.Family) ? item
                : item with { FamilyDescription = families.GetValueOrDefault(FamilyCode.Key(item.Family)) });
        }
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