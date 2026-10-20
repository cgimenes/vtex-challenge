using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;
using VtexChallenge.Import;

namespace VtexChallenge.Migrations;

/// <summary>
/// The provided database was not created by EF, so instead of faking a migrations baseline
/// we apply versioned steps tracked by PRAGMA user_version. Step N upgrades version N-1 to N;
/// to add a migration, write a method and append it to <see cref="Steps"/>.
/// </summary>
public static class SchemaUpgrader
{
    private record ProductRow(int Id, string Name, string? Brand);

    private static readonly Action<CatalogDbContext>[] Steps =
    [
        V1_SellerProductIdAsText,
        V2_RejectedEntry,
        V3_ImportRun,
        V4_ImportRunReportFields,
        V5_ImportRunState,
        V6_DropRejectedEntrySource,
        V7_ProductNormalizedName,
    ];

    public static int CurrentVersion => Steps.Length;

    public static void Upgrade(CatalogDbContext db)
    {
        var version = db.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version > CurrentVersion)
            throw new InvalidOperationException(
                $"Database schema version {version} is newer than this tool supports ({CurrentVersion}). Update the tool.");
        if (version == CurrentVersion) return;

        using var tx = db.Database.BeginTransaction();
        for (var i = version; i < Steps.Length; i++)
            Steps[i](db);
        db.Database.ExecuteSqlRaw($"PRAGMA user_version = {CurrentVersion}");
        tx.Commit();
    }

    /// <summary>
    /// SellerProductId INTEGER -> TEXT (seller ids are GUIDs), uniqueness constraints,
    /// Product.NormalizedKey (+ index) used for duplicate detection.
    /// </summary>
    private static void V1_SellerProductIdAsText(CatalogDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE SellerProduct_new (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SellerName TEXT NOT NULL,
                ProductId INTEGER NOT NULL CONSTRAINT FK_Product_Id REFERENCES Product (Id),
                SellerProductId TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            INSERT INTO SellerProduct_new (Id, SellerName, ProductId, SellerProductId)
            SELECT Id, SellerName, ProductId, CAST(SellerProductId AS TEXT) FROM SellerProduct
            """);
        db.Database.ExecuteSqlRaw("DROP TABLE SellerProduct");
        db.Database.ExecuteSqlRaw("ALTER TABLE SellerProduct_new RENAME TO SellerProduct");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_SellerProduct_SellerName_SellerProductId ON SellerProduct (SellerName, SellerProductId)");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_SellerProduct_SellerName_ProductId ON SellerProduct (SellerName, ProductId)");

        db.Database.ExecuteSqlRaw("ALTER TABLE Product ADD COLUMN NormalizedKey TEXT");
        // raw SQL on purpose: the Product entity already maps columns added by later versions
        var rows = db.Database.SqlQueryRaw<ProductRow>("SELECT Id, Name, Brand FROM Product").ToList();
        foreach (var r in rows)
            db.Database.ExecuteSqlInterpolated($"UPDATE Product SET NormalizedKey = {ProductKey.Build(r.Brand, r.Name)} WHERE Id = {r.Id}");
        db.Database.ExecuteSqlRaw("CREATE INDEX IX_Product_NormalizedKey ON Product (NormalizedKey)");
    }

    /// <summary>RejectedEntry table, audit log of feed rows that could not be imported.</summary>
    private static void V2_RejectedEntry(CatalogDbContext db) =>
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE RejectedEntry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SellerName TEXT,
                SellerProductId TEXT,
                Name TEXT,
                Brand TEXT,
                Category TEXT,
                Reason TEXT NOT NULL,
                Source TEXT,
                RejectedAt TEXT NOT NULL
            )
            """);

    /// <summary>ImportRun table + ImportId on Product, SellerProduct and RejectedEntry, so one import can be undone.</summary>
    private static void V3_ImportRun(CatalogDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE ImportRun (
                Id TEXT PRIMARY KEY,
                Source TEXT,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT,
                EntriesRead INTEGER NOT NULL DEFAULT 0,
                ProductsInserted INTEGER NOT NULL DEFAULT 0,
                LinksCreated INTEGER NOT NULL DEFAULT 0,
                Rejected INTEGER NOT NULL DEFAULT 0
            )
            """);
        foreach (var table in new[] { "Product", "SellerProduct", "RejectedEntry" })
        {
            db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN ImportId TEXT REFERENCES ImportRun (Id)");
            db.Database.ExecuteSqlRaw($"CREATE INDEX IX_{table}_ImportId ON {table} (ImportId)");
        }
    }

    /// <summary>ImportRun gets LinkedToExisting, AlreadyLinked and Notes so a run fully describes its report.</summary>
    private static void V4_ImportRunReportFields(CatalogDbContext db)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE ImportRun ADD COLUMN LinkedToExisting INTEGER NOT NULL DEFAULT 0");
        db.Database.ExecuteSqlRaw("ALTER TABLE ImportRun ADD COLUMN AlreadyLinked INTEGER NOT NULL DEFAULT 0");
        db.Database.ExecuteSqlRaw("ALTER TABLE ImportRun ADD COLUMN Notes TEXT");
    }

    /// <summary>ImportRun gets State (Running/Completed/Failed) and Error.</summary>
    private static void V5_ImportRunState(CatalogDbContext db)
    {
        // runs recorded before v5 were written inside the import transaction, so they all completed
        db.Database.ExecuteSqlRaw("ALTER TABLE ImportRun ADD COLUMN State TEXT NOT NULL DEFAULT 'Completed'");
        db.Database.ExecuteSqlRaw("ALTER TABLE ImportRun ADD COLUMN Error TEXT");
    }

    /// <summary>RejectedEntry.Source dropped, the source file is on its ImportRun.</summary>
    private static void V6_DropRejectedEntrySource(CatalogDbContext db) =>
        db.Database.ExecuteSqlRaw("ALTER TABLE RejectedEntry DROP COLUMN Source");

    /// <summary>Product.NormalizedName (+ index), so match candidates are fetched per batch instead of loading the catalog.</summary>
    private static void V7_ProductNormalizedName(CatalogDbContext db)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Product ADD COLUMN NormalizedName TEXT");
        var rows = db.Database.SqlQueryRaw<ProductRow>("SELECT Id, Name, Brand FROM Product").ToList();
        foreach (var r in rows)
            db.Database.ExecuteSqlInterpolated($"UPDATE Product SET NormalizedName = {ProductKey.Normalize(r.Name)} WHERE Id = {r.Id}");
        db.Database.ExecuteSqlRaw("CREATE INDEX IX_Product_NormalizedName ON Product (NormalizedName)");
    }
}
