using Microsoft.EntityFrameworkCore;
using VtexChallenge.Import;

namespace VtexChallenge.Data;

/// <summary>
/// The provided database was not created by EF, so instead of faking a migrations baseline
/// we apply one small versioned upgrade tracked by PRAGMA user_version.
/// v1: SellerProductId INTEGER -> TEXT (seller ids are GUIDs), uniqueness constraints,
///     Product.NormalizedKey (+ index) used for duplicate detection.
/// v2: RejectedEntry table, audit log of feed rows that could not be imported.
/// v3: ImportRun table + ImportId on Product, SellerProduct and RejectedEntry, so one import can be undone.
/// </summary>
public static class SchemaUpgrader
{
    private record ProductRow(int Id, string Name, string? Brand);

    public const int CurrentVersion = 3;

    public static void Upgrade(CatalogDbContext db)
    {
        var version = db.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version >= CurrentVersion) return;

        using var tx = db.Database.BeginTransaction();

        if (version < 1)
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

        if (version < 2)
        {
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
        }

        if (version < 3)
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

        db.Database.ExecuteSqlRaw($"PRAGMA user_version = {CurrentVersion}");
        tx.Commit();
    }
}
