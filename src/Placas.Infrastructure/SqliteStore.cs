using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Placas.Application;
using Placas.Domain;

namespace Placas.Infrastructure;

public sealed class SqliteStore : IStore
{
    private sealed record ProductDetails(
        string Code, string DisplayCode, string Description, string? SubDescription, string? Family,
        bool ManualDescription, bool ManualSubDescription, bool ManualFamily);

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
            CREATE TABLE IF NOT EXISTS families(key TEXT PRIMARY KEY, code TEXT NOT NULL, description TEXT NOT NULL, manual INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS group_descriptions(key TEXT PRIMARY KEY, code TEXT NOT NULL, description TEXT NOT NULL, manual INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS product_details(
                code TEXT PRIMARY KEY, display_code TEXT NOT NULL, description TEXT NOT NULL, sub_description TEXT, family TEXT,
                manual_description INTEGER NOT NULL DEFAULT 0, manual_sub_description INTEGER NOT NULL DEFAULT 0, manual_family INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS images(id TEXT PRIMARY KEY, name TEXT NOT NULL, hash TEXT NOT NULL UNIQUE, data BLOB NOT NULL, thumb BLOB NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS product_images(code TEXT PRIMARY KEY, image_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS family_images(key TEXT PRIMARY KEY, image_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS templates(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id TEXT PRIMARY KEY, created TEXT NOT NULL, status TEXT NOT NULL, detail TEXT NOT NULL, json TEXT NOT NULL);
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
        if (!HasColumn(db, "families", "manual"))
        {
            using var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE families ADD COLUMN manual INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        InitializeProductDetails(db);
        if (Templates().Count == 0) SaveTemplate(PlateTemplate.Default());
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open(); return connection;
    }
    private static bool HasColumn(SqliteConnection db, string table, string column)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void InitializeProductDetails(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT code,json FROM items ORDER BY source,id";
        var items = new List<(string Code, PriceItem Item)>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                items.Add((reader.GetString(0), JsonSerializer.Deserialize<PriceItem>(reader.GetString(1))!));

        foreach (var (code, item) in items)
        {
            using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO product_details(code,display_code,description,sub_description,family)
                VALUES($code,$display,$description,$sub,$family)
                """;
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$display", item.DisplayCode);
            insert.Parameters.AddWithValue("$description", item.Description);
            insert.Parameters.AddWithValue("$sub", (object?)item.SubDescription ?? DBNull.Value);
            insert.Parameters.AddWithValue("$family", (object?)item.Family ?? DBNull.Value);
            insert.ExecuteNonQuery();
            if (item.Source == SourceKind.Promotion && !string.IsNullOrWhiteSpace(item.GroupCode)
                && !string.IsNullOrWhiteSpace(item.GroupDescription))
                UpsertGroupDescription(db, tx, item.GroupCode, item.GroupDescription, manual: false);
        }
        tx.Commit();
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
            foreach (var family in batch.Families ?? []) UpsertFamily(db, transaction, family, manual: false);
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
            var imported = item;
            if (item.Source != SourceKind.Catalog && string.IsNullOrWhiteSpace(item.Family))
            {
                using var familyQuery = db.CreateCommand(); familyQuery.Transaction = transaction;
                familyQuery.CommandText = "SELECT json FROM items WHERE code=$code AND source=0 LIMIT 1";
                familyQuery.Parameters.AddWithValue("$code", item.Code);
                if (familyQuery.ExecuteScalar() is string catalogJson)
                    imported = item with { Family = JsonSerializer.Deserialize<PriceItem>(catalogJson)?.Family };
            }
            if (string.IsNullOrWhiteSpace(imported.SubDescription))
            {
                using var subQuery = db.CreateCommand(); subQuery.Transaction = transaction;
                subQuery.CommandText = "SELECT json FROM items WHERE id=$id";
                subQuery.Parameters.AddWithValue("$id", imported.Id);
                if (subQuery.ExecuteScalar() is string existingJson &&
                    JsonSerializer.Deserialize<PriceItem>(existingJson)?.SubDescription is { Length: > 0 } keptSub)
                    imported = imported with { SubDescription = keptSub };
            }
            DeleteSyntheticProduct(db, transaction, imported.Code);
            UpsertProductDetails(db, transaction, imported, manualDescription: false, manualSubDescription: false, manualFamily: false);
            imported = ApplyProductOverrides(db, transaction, imported);
            id.Value = imported.Id; code.Value = imported.Code; search.Value = SearchKey(imported.Description);
            source.Value = (int)imported.Source; json.Value = JsonSerializer.Serialize(imported with { FamilyDescription = null, ImageId = null, FamilyImageId = null });
            command.ExecuteNonQuery();
        }
        using var link = db.CreateCommand(); link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO import_items(import_hash,item_id) VALUES($hash,$id)";
        link.Parameters.AddWithValue("$hash", batch.Hash);
        var linkedId = link.Parameters.Add("$id", SqliteType.Text);
        foreach (var item in batch.Items) { linkedId.Value = item.Id; link.ExecuteNonQuery(); }
        foreach (var family in batch.Families ?? [])
            UpsertFamily(db, transaction, family, manual: false);
        foreach (var item in batch.Items.Where(x => !string.IsNullOrWhiteSpace(x.GroupCode) && !string.IsNullOrWhiteSpace(x.GroupDescription)))
            UpsertGroupDescription(db, transaction, item.GroupCode!, item.GroupDescription!, manual: false);
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
        var normalized = item with { Code = ProductCode.Normalize(item.DisplayCode), FamilyDescription = null, ImageId = null, FamilyImageId = null };
        using var db = Open(); using var tx = db.BeginTransaction();
        using var previousRead = db.CreateCommand(); previousRead.Transaction = tx;
        previousRead.CommandText = "SELECT json FROM items WHERE id=$id";
        previousRead.Parameters.AddWithValue("$id", normalized.Id);
        var previous = previousRead.ExecuteScalar() is string previousJson
            ? JsonSerializer.Deserialize<PriceItem>(previousJson) : null;
        UpsertProductDetails(db, tx, normalized,
            manualDescription: previous is null || previous.Description != normalized.Description,
            manualSubDescription: previous is null || previous.SubDescription != normalized.SubDescription,
            manualFamily: previous is null || previous.Family != normalized.Family);
        using var command = db.CreateCommand(); command.Transaction = tx;
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

        using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT id,json FROM items WHERE code=$code";
        read.Parameters.AddWithValue("$code", normalized.Code);
        var matches = new List<(string Id, PriceItem Item)>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                matches.Add((reader.GetString(0), JsonSerializer.Deserialize<PriceItem>(reader.GetString(1))!));
        var savedDetails = ReadProductDetails(db, tx, normalized.Code);
        foreach (var (id, existing) in matches)
        {
            var updated = existing with
            {
                Description = savedDetails?.ManualDescription == true ? savedDetails.Description : existing.Description,
                SubDescription = savedDetails?.ManualSubDescription == true ? savedDetails.SubDescription : existing.SubDescription,
                Family = savedDetails?.ManualFamily == true ? savedDetails.Family : existing.Family,
                FamilyDescription = null,
                ImageId = null,
                FamilyImageId = null
            };
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = "UPDATE items SET search=$search,json=$json WHERE id=$id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$search", SearchKey(updated.Description));
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(updated));
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static Dictionary<string, string> LoadProductImages(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT code,image_id FROM product_images";
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public string? FindImage(string hash)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM images WHERE hash=$hash"; command.Parameters.AddWithValue("$hash", hash);
        return command.ExecuteScalar() as string;
    }

    public string AddImage(string name, string hash, byte[] data, byte[] thumbnail)
    {
        if (data.Length == 0 || thumbnail.Length == 0) throw new InvalidOperationException("Imagem vazia.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var find = db.CreateCommand(); find.Transaction = tx;
        find.CommandText = "SELECT id FROM images WHERE hash=$hash"; find.Parameters.AddWithValue("$hash", hash);
        if (find.ExecuteScalar() is string existing) { tx.Commit(); return existing; }
        var id = Guid.NewGuid().ToString("N");
        using var insert = db.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO images(id,name,hash,data,thumb,created) VALUES($id,$name,$hash,$data,$thumb,$created)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? "Imagem" : name.Trim());
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$data", data);
        insert.Parameters.AddWithValue("$thumb", thumbnail);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        insert.ExecuteNonQuery(); tx.Commit(); return id;
    }

    public List<ImageInfo> Images()
    {
        var layoutUse = new Dictionary<string, int>();
        foreach (var template in Templates())
            foreach (var id in template.Elements.Where(x => x.IsImage && !ImageSources.IsDynamic(x.ImageId)).Select(x => x.ImageId).Distinct())
                layoutUse[id] = layoutUse.GetValueOrDefault(id) + 1;
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT i.id, i.name, length(i.data), i.thumb, (SELECT COUNT(*) FROM product_images p WHERE p.image_id=i.id), (SELECT COUNT(*) FROM family_images f WHERE f.image_id=i.id) FROM images i ORDER BY i.name COLLATE NOCASE, i.rowid";
        var result = new List<ImageInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new ImageInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetFieldValue<byte[]>(3), reader.GetInt32(4), layoutUse.GetValueOrDefault(reader.GetString(0)), reader.GetInt32(5)));
        return result;
    }

    public byte[]? GetImage(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT data FROM images WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as byte[];
    }

    public void RenameImage(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("O nome da imagem não pode ficar vazio.");
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE images SET name=$name WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name.Trim());
        command.ExecuteNonQuery();
    }

    public int DeleteImage(string id)
    {
        var layouts = Templates().Where(t => t.Elements.Any(e => e.IsImage && e.ImageId == id)).Select(t => t.Name).ToList();
        if (layouts.Count > 0)
            throw new InvalidOperationException($"A imagem está em uso nos layouts: {string.Join(", ", layouts)}. Remova-a dos layouts antes de excluir.");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var links = db.CreateCommand(); links.Transaction = tx;
        links.CommandText = "DELETE FROM product_images WHERE image_id=$id"; links.Parameters.AddWithValue("$id", id);
        var removed = links.ExecuteNonQuery();
        using var familyLinks = db.CreateCommand(); familyLinks.Transaction = tx;
        familyLinks.CommandText = "DELETE FROM family_images WHERE image_id=$id"; familyLinks.Parameters.AddWithValue("$id", id);
        removed += familyLinks.ExecuteNonQuery();
        using var image = db.CreateCommand(); image.Transaction = tx;
        image.CommandText = "DELETE FROM images WHERE id=$id"; image.Parameters.AddWithValue("$id", id);
        image.ExecuteNonQuery(); tx.Commit(); return removed;
    }

    public void SetProductImage(string code, string? imageId)
    {
        var key = ProductCode.Normalize(code);
        using var db = Open(); using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        if (string.IsNullOrWhiteSpace(imageId))
        {
            command.CommandText = "DELETE FROM product_images WHERE code=$code";
            command.Parameters.AddWithValue("$code", key);
        }
        else
        {
            using var exists = db.CreateCommand(); exists.Transaction = tx;
            exists.CommandText = "SELECT 1 FROM images WHERE id=$id"; exists.Parameters.AddWithValue("$id", imageId);
            if (exists.ExecuteScalar() is null) throw new InvalidOperationException("Imagem não encontrada na biblioteca.");
            command.CommandText = "INSERT INTO product_images(code,image_id) VALUES($code,$image) ON CONFLICT(code) DO UPDATE SET image_id=$image";
            command.Parameters.AddWithValue("$code", key); command.Parameters.AddWithValue("$image", imageId);
        }
        command.ExecuteNonQuery(); tx.Commit();
    }

    private static Dictionary<string, string> LoadFamilyImages(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT key,image_id FROM family_images";
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public void SetFamilyImage(string familyCode, string? imageId)
    {
        if (string.IsNullOrWhiteSpace(familyCode)) throw new InvalidOperationException("Informe o código da família.");
        var key = FamilyCode.Key(familyCode);
        using var db = Open(); using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        if (string.IsNullOrWhiteSpace(imageId))
        {
            command.CommandText = "DELETE FROM family_images WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
        }
        else
        {
            using var exists = db.CreateCommand(); exists.Transaction = tx;
            exists.CommandText = "SELECT 1 FROM images WHERE id=$id"; exists.Parameters.AddWithValue("$id", imageId);
            if (exists.ExecuteScalar() is null) throw new InvalidOperationException("Imagem não encontrada na biblioteca.");
            command.CommandText = "INSERT INTO family_images(key,image_id) VALUES($key,$image) ON CONFLICT(key) DO UPDATE SET image_id=$image";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$image", imageId);
        }
        command.ExecuteNonQuery(); tx.Commit();
    }

    public void SaveFamily(FamilyInfo family)
    {
        if (string.IsNullOrWhiteSpace(family.Code) || string.IsNullOrWhiteSpace(family.Description))
            throw new InvalidOperationException("Informe o código e a descrição da família.");
        using var db = Open(); using var tx = db.BeginTransaction();
        UpsertFamily(db, tx, family, manual: true);
        tx.Commit();
    }

    public void SaveGroupDescription(string groupCode, string description)
    {
        if (string.IsNullOrWhiteSpace(groupCode) || string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Informe o código e a descrição do grupo.");
        using var db = Open(); using var tx = db.BeginTransaction();
        UpsertGroupDescription(db, tx, groupCode, description, manual: true);
        tx.Commit();
    }

    private static void UpdateGroupDescription(SqliteConnection db, SqliteTransaction tx, string id, string description)
    {
        using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT json FROM items WHERE id=$id"; read.Parameters.AddWithValue("$id", id);
        if (read.ExecuteScalar() is not string json) return;
        var item = JsonSerializer.Deserialize<PriceItem>(json)!;
        if (!string.IsNullOrWhiteSpace(item.GroupCode))
            UpsertGroupDescription(db, tx, item.GroupCode, description, manual: false);
    }

    private static void UpsertFamily(SqliteConnection db, SqliteTransaction tx, FamilyInfo family, bool manual)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO families(key,code,description,manual) VALUES($key,$code,$description,$manual)
            ON CONFLICT(key) DO UPDATE SET
                code=CASE WHEN families.manual=1 AND $manual=0 THEN families.code ELSE excluded.code END,
                description=CASE WHEN families.manual=1 AND $manual=0 THEN families.description ELSE excluded.description END,
                manual=MAX(families.manual,excluded.manual)
            """;
        command.Parameters.AddWithValue("$key", FamilyCode.Key(family.Code));
        command.Parameters.AddWithValue("$code", family.Code.Trim());
        command.Parameters.AddWithValue("$description", family.Description.Trim());
        command.Parameters.AddWithValue("$manual", manual ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void UpsertGroupDescription(SqliteConnection db, SqliteTransaction tx, string code, string description, bool manual)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO group_descriptions(key,code,description,manual) VALUES($key,$code,$description,$manual)
            ON CONFLICT(key) DO UPDATE SET
                code=CASE WHEN group_descriptions.manual=1 AND $manual=0 THEN group_descriptions.code ELSE excluded.code END,
                description=CASE WHEN group_descriptions.manual=1 AND $manual=0 THEN group_descriptions.description ELSE excluded.description END,
                manual=MAX(group_descriptions.manual,excluded.manual)
            """;
        command.Parameters.AddWithValue("$key", FamilyCode.Key(code));
        command.Parameters.AddWithValue("$code", code.Trim());
        command.Parameters.AddWithValue("$description", description.Trim());
        command.Parameters.AddWithValue("$manual", manual ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void UpsertProductDetails(
        SqliteConnection db, SqliteTransaction tx, PriceItem item,
        bool manualDescription, bool manualSubDescription, bool manualFamily)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO product_details(code,display_code,description,sub_description,family,manual_description,manual_sub_description,manual_family)
            VALUES($code,$display,$description,$sub,$family,$manual_description,$manual_sub_description,$manual_family)
            ON CONFLICT(code) DO UPDATE SET
                display_code=excluded.display_code,
                description=CASE WHEN product_details.manual_description=1 AND $manual_description=0 THEN product_details.description ELSE excluded.description END,
                sub_description=CASE WHEN product_details.manual_sub_description=1 AND $manual_sub_description=0 THEN product_details.sub_description ELSE excluded.sub_description END,
                family=CASE WHEN product_details.manual_family=1 AND $manual_family=0 THEN product_details.family ELSE excluded.family END,
                manual_description=MAX(product_details.manual_description,excluded.manual_description),
                manual_sub_description=MAX(product_details.manual_sub_description,excluded.manual_sub_description),
                manual_family=MAX(product_details.manual_family,excluded.manual_family)
            """;
        command.Parameters.AddWithValue("$code", item.Code);
        command.Parameters.AddWithValue("$display", item.DisplayCode);
        command.Parameters.AddWithValue("$description", item.Description);
        command.Parameters.AddWithValue("$sub", (object?)item.SubDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$family", (object?)item.Family ?? DBNull.Value);
        command.Parameters.AddWithValue("$manual_description", manualDescription ? 1 : 0);
        command.Parameters.AddWithValue("$manual_sub_description", manualSubDescription ? 1 : 0);
        command.Parameters.AddWithValue("$manual_family", manualFamily ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static ProductDetails? ReadProductDetails(SqliteConnection db, SqliteTransaction tx, string code)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT code,display_code,description,sub_description,family,
                   manual_description,manual_sub_description,manual_family
            FROM product_details WHERE code=$code
            """;
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ProductDetails(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5) != 0, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0);
    }

    private static PriceItem ApplyProductOverrides(SqliteConnection db, SqliteTransaction tx, PriceItem item)
    {
        var details = ReadProductDetails(db, tx, item.Code);
        if (details is null) return item;
        return item with
        {
            Description = details.ManualDescription ? details.Description : item.Description,
            SubDescription = details.ManualSubDescription ? details.SubDescription : item.SubDescription,
            Family = details.ManualFamily ? details.Family : item.Family
        };
    }

    private static void DeleteSyntheticProduct(SqliteConnection db, SqliteTransaction tx, string code)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "DELETE FROM items WHERE id=$id AND source=$source";
        command.Parameters.AddWithValue("$id", "product:" + code);
        command.Parameters.AddWithValue("$source", (int)SourceKind.Catalog);
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

    private static Dictionary<string, string> LoadGroupDescriptions(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT key,description FROM group_descriptions";
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public void DeleteItem(string id) => DeleteItems([id]);

    public int DeleteItems(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return 0;
        using var db = Open(); using var tx = db.BeginTransaction();
        using var temp = db.CreateCommand(); temp.Transaction = tx;
        temp.CommandText = "CREATE TEMP TABLE IF NOT EXISTS delete_item_ids(id TEXT PRIMARY KEY)";
        temp.ExecuteNonQuery();
        using (var clear = db.CreateCommand()) { clear.Transaction = tx; clear.CommandText = "DELETE FROM delete_item_ids"; clear.ExecuteNonQuery(); }

        using var addId = db.CreateCommand(); addId.Transaction = tx;
        addId.CommandText = "INSERT OR IGNORE INTO delete_item_ids(id) VALUES($id)";
        var idParameter = addId.Parameters.Add("$id", SqliteType.Text);
        addId.Prepare();
        foreach (var id in ids.Distinct())
        {
            idParameter.Value = id;
            addId.ExecuteNonQuery();
        }

        var affected = new HashSet<string>();
        var preservedProducts = new List<PriceItem>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT i.json
                FROM items i INNER JOIN delete_item_ids d ON d.id=i.id
                WHERE i.source<>0
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                preservedProducts.Add(JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!);
        }
        using (var readImports = db.CreateCommand())
        {
            readImports.Transaction = tx;
            readImports.CommandText = """
                SELECT DISTINCT l.import_hash
                FROM import_items l INNER JOIN delete_item_ids d ON d.id=l.item_id
                """;
            using var reader = readImports.ExecuteReader();
            while (reader.Read()) affected.Add(reader.GetString(0));
        }
        using var links = db.CreateCommand(); links.Transaction = tx;
        links.CommandText = """
            DELETE FROM import_items
            WHERE item_id IN (SELECT id FROM delete_item_ids)
            """;
        links.ExecuteNonQuery();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            DELETE FROM items
            WHERE id IN (SELECT id FROM delete_item_ids)
            """;
        var removed = command.ExecuteNonQuery();
        foreach (var product in preservedProducts.GroupBy(x => x.Code).Select(x => x.Last()))
            CreateProductCatalogItemIfNeeded(db, tx, product);
        RemoveEmptyImports(db, tx, affected);
        using var clean = db.CreateCommand(); clean.Transaction = tx;
        clean.CommandText = "DELETE FROM delete_item_ids";
        clean.ExecuteNonQuery();
        tx.Commit(); return removed;
    }

    private static void CreateProductCatalogItemIfNeeded(SqliteConnection db, SqliteTransaction tx, PriceItem item)
    {
        using var exists = db.CreateCommand(); exists.Transaction = tx;
        exists.CommandText = "SELECT 1 FROM items WHERE code=$code LIMIT 1";
        exists.Parameters.AddWithValue("$code", item.Code);
        if (exists.ExecuteScalar() is not null) return;

        var details = ReadProductDetails(db, tx, item.Code);
        var source = ApplyProductOverrides(db, tx, item) with
        {
            Id = "product:" + item.Code,
            Code = item.Code,
            DisplayCode = details?.DisplayCode ?? item.DisplayCode,
            Price = 0,
            NormalPrice = null,
            Start = null,
            End = null,
            Source = SourceKind.Catalog,
            GroupCode = null,
            GroupDescription = null,
            Quantity = 1,
            Rule = QuantityRule.UnitPrice,
            FileName = "Cadastro persistido",
            Sheet = "",
            Row = 0,
            FamilyDescription = null,
            ImageId = null,
            FamilyImageId = null
        };
        using var insert = db.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO items(id,code,search,source,json)
            VALUES($id,$code,$search,$source,$json)
            """;
        insert.Parameters.AddWithValue("$id", source.Id);
        insert.Parameters.AddWithValue("$code", source.Code);
        insert.Parameters.AddWithValue("$search", SearchKey(source.Description));
        insert.Parameters.AddWithValue("$source", (int)source.Source);
        insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(source));
        insert.ExecuteNonQuery();
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
            using var registry = db.CreateCommand(); registry.Transaction = tx;
            registry.CommandText = "DELETE FROM imports WHERE hash=$hash";
            registry.Parameters.AddWithValue("$hash", hash); registry.ExecuteNonQuery();
            tx.Commit(); return 0;
        }
        using var candidatesQuery = db.CreateCommand(); candidatesQuery.Transaction = tx;
        candidatesQuery.CommandText = """
            SELECT json FROM items WHERE id IN
                (SELECT item_id FROM import_items WHERE import_hash=$hash)
                AND NOT EXISTS
                (SELECT 1 FROM import_items other
                 WHERE other.item_id=items.id AND other.import_hash<>$hash)
            """;
        candidatesQuery.Parameters.AddWithValue("$hash", hash);
        var preservedProducts = new List<PriceItem>();
        using (var reader = candidatesQuery.ExecuteReader())
            while (reader.Read())
            {
                var item = JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!;
                if (item.Source != SourceKind.Catalog) preservedProducts.Add(item);
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
        foreach (var product in preservedProducts.GroupBy(x => x.Code).Select(x => x.Last()))
            CreateProductCatalogItemIfNeeded(db, tx, product);
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
        using var candidatesQuery = db.CreateCommand(); candidatesQuery.Transaction = tx;
        candidatesQuery.CommandText = $"SELECT json FROM items WHERE {expiredFilter}";
        candidatesQuery.Parameters.AddWithValue("$today", today);
        var preservedProducts = new List<PriceItem>();
        using (var reader = candidatesQuery.ExecuteReader())
            while (reader.Read())
                preservedProducts.Add(JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!);
        using var remove = db.CreateCommand(); remove.Transaction = tx;
        remove.CommandText = $"DELETE FROM items WHERE {expiredFilter}";
        remove.Parameters.AddWithValue("$today", today);
        var removed = remove.ExecuteNonQuery();
        using var orphan = db.CreateCommand(); orphan.Transaction = tx;
        orphan.CommandText = "DELETE FROM import_items WHERE item_id NOT IN (SELECT id FROM items)";
        orphan.ExecuteNonQuery();
        foreach (var product in preservedProducts.GroupBy(x => x.Code).Select(x => x.Last()))
            CreateProductCatalogItemIfNeeded(db, tx, product);
        RemoveEmptyImports(db, tx, affected);
        tx.Commit(); return removed;
    }

    private static string SearchKey(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).ToArray());
    public List<PriceItem> Search(string text, SourceKind? source, int limit = 500)
        => SearchPage(text, source, 0, limit);

    public List<PriceItem> SearchPage(string text, SourceKind? source, int offset, int limit)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM items WHERE ($source IS NULL OR source=$source) AND (instr(search,$text)>0 OR code=$code) ORDER BY search,source,id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$source", source is null ? DBNull.Value : (object)(int)source);
        command.Parameters.AddWithValue("$text", SearchKey(text.Trim()));
        command.Parameters.AddWithValue("$code", text.Trim().TrimStart('0'));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var families = LoadFamilies(db);
        var groups = LoadGroupDescriptions(db);
        var productImages = LoadProductImages(db);
        var familyImages = LoadFamilyImages(db);
        using var reader = command.ExecuteReader(); var result = new List<PriceItem>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<PriceItem>(reader.GetString(0))!;
            item = item with { ImageId = productImages.GetValueOrDefault(item.Code) };
            if (!string.IsNullOrWhiteSpace(item.Family))
            {
                var familyKey = FamilyCode.Key(item.Family);
                item = item with { FamilyDescription = families.GetValueOrDefault(familyKey), FamilyImageId = familyImages.GetValueOrDefault(familyKey) };
            }
            if (item.Source == SourceKind.Promotion && !string.IsNullOrWhiteSpace(item.GroupCode)
                && groups.TryGetValue(FamilyCode.Key(item.GroupCode), out var groupDescription))
                item = item with { GroupDescription = groupDescription };
            result.Add(item);
        }
        return result;
    }

    public List<string> SearchFamilies(string text, SourceKind? source)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT DISTINCT json_extract(json,'$.Family') FROM items WHERE ($source IS NULL OR source=$source) AND (instr(search,$text)>0 OR code=$code) AND json_extract(json,'$.Family') IS NOT NULL AND json_extract(json,'$.Family')<>'' ORDER BY 1 COLLATE NOCASE";
        command.Parameters.AddWithValue("$source", source is null ? DBNull.Value : (object)(int)source);
        command.Parameters.AddWithValue("$text", SearchKey(text.Trim()));
        command.Parameters.AddWithValue("$code", text.Trim().TrimStart('0'));
        var families = LoadFamilies(db);
        var labels = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var label = families.TryGetValue(FamilyCode.Key(code), out var description)
                ? $"{code} - {description}" : code;
            labels.Add(label);
        }
        return labels.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
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