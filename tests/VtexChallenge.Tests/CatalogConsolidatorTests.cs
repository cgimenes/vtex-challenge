using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;
using VtexChallenge.Import;
using VtexChallenge.Migrations;

namespace VtexChallenge.Tests;

public class CatalogConsolidatorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.db");

    public CatalogConsolidatorTests()
    {
        // minimal legacy schema, same as the provided catalog.db
        using var db = CatalogDbContext.Open(_path);
        db.Database.ExecuteSqlRaw("CREATE TABLE Product (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Brand TEXT, Category TEXT)");
        db.Database.ExecuteSqlRaw("CREATE TABLE SellerProduct (Id INTEGER PRIMARY KEY AUTOINCREMENT, SellerName TEXT NOT NULL, ProductId INTEGER CONSTRAINT FK_Product_Id REFERENCES Product (Id) NOT NULL, SellerProductId INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("INSERT INTO Product (Name, Brand, Category) VALUES ('Smart TV Samsung 55\"','Samsung','Electronics'),('Bed Frame Wood King',NULL,'Furniture'),('iPhone 15 Pro','Apple',NULL)");
    }

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private static string G(int n) => $"{n:x8}-0000-4000-8000-000000000000";
    private static SellerProductEntry E(int id, string seller, string name, string? brand = null, string? cat = null) =>
        new(G(id), seller, name, brand, cat);

    private ImportReport Run(params SellerProductEntry[] entries)
    {
        using var db = CatalogDbContext.Open(_path);
        SchemaUpgrader.Upgrade(db);
        return new CatalogConsolidator(db).ImportAsync(entries.Select(e => (SellerProductEntry?)e).ToAsyncEnumerable()).GetAwaiter().GetResult();
    }

    private (int products, int links) Counts()
    {
        using var db = CatalogDbContext.Open(_path);
        return (db.Products.Count(), db.SellerProducts.Count());
    }

    [Fact]
    public void ExistingProductOnlyGetsLink()
    {
        var r = Run(E(1, "A", "Smart TV Samsung 55", "Samsung"));
        Assert.Equal(0, r.ProductsInserted);
        Assert.Equal((3, 1), Counts());
    }

    [Fact]
    public void VariantsAcrossSellersCollapseToOneProduct()
    {
        Run(E(1, "A", "Smart TV Samsung  55\"", "Samsung"), E(2, "B", "smart tv samsung 55", "SAMSUNG"));
        var (products, links) = Counts();
        Assert.Equal(3, products);
        Assert.Equal(2, links);
    }

    [Fact]
    public void NewProductIsInserted()
    {
        var r = Run(E(1, "A", "Brand New Gadget", "Acme", "Misc"));
        Assert.Equal(1, r.ProductsInserted);
        Assert.Equal((4, 1), Counts());
    }

    [Fact]
    public void InvalidGuidRejected()
    {
        var r = Run(new SellerProductEntry("not-a-guid", "A", "iPhone 15 Pro", "Apple", null));
        Assert.Equal(1, r.Rejected);
        Assert.Equal((3, 0), Counts());
        using var db = CatalogDbContext.Open(_path);
        var row = Assert.Single(db.RejectedEntries.ToList());
        Assert.Equal("not-a-guid", row.SellerProductId);
        Assert.Equal("iPhone 15 Pro", row.Name);
        Assert.Equal("invalid GUID id", row.Reason);
    }

    [Fact]
    public void SameSellerSameIdDifferentProductRejected()
    {
        var r = Run(E(1, "A", "iPhone 15 Pro", "Apple"), E(1, "A", "Smart TV Samsung 55", "Samsung"));
        Assert.Equal(1, r.Rejected);
        Assert.Equal((3, 1), Counts());
        using var db = CatalogDbContext.Open(_path);
        Assert.Contains("reused this id", Assert.Single(db.RejectedEntries.ToList()).Reason);
    }

    [Fact]
    public void SameIdAcrossSellersIsFine()
    {
        var r = Run(E(1, "A", "iPhone 15 Pro", "Apple"), E(1, "B", "iPhone 15 Pro", "Apple"));
        Assert.Equal(0, r.Rejected);
        Assert.Equal((3, 2), Counts());
    }

    [Fact]
    public void RerunIsIdempotent()
    {
        var entries = new[] { E(1, "A", "iPhone 15 Pro", "Apple"), E(2, "B", "Brand New Gadget", "Acme") };
        Run(entries);
        var before = Counts();
        var r = Run(entries);
        Assert.Equal(before, Counts());
        Assert.Equal(2, r.AlreadyLinked);
    }

    [Fact]
    public void MissingBrandMatchesUniqueNameAndFillsGap()
    {
        Run(E(1, "A", "Bed Frame Wood King", "Zinus"));
        using var db = CatalogDbContext.Open(_path);
        Assert.Equal(3, db.Products.Count());
        Assert.Equal("Zinus", db.Products.Single(p => p.Name == "Bed Frame Wood King").Brand);
    }

    [Fact]
    public void ExistingValuesAreNotOverwrittenButGapsAreFilled()
    {
        Run(E(1, "A", "Smart TV Samsung 55", "Samsung", "TVs"), E(2, "A", "iPhone 15 Pro", "Apple", "Phones"));
        using var db = CatalogDbContext.Open(_path);
        Assert.Equal("Electronics", db.Products.Single(p => p.Brand == "Samsung").Category);
        Assert.Equal("Phones", db.Products.Single(p => p.Brand == "Apple").Category);
    }

    [Fact]
    public void EveryCreatedRowCarriesTheImportId()
    {
        var r = Run(E(1, "A", "Brand New Gadget", "Acme"), E(2, "A", "iPhone 15 Pro", "Apple"),
                    new SellerProductEntry("bad", "A", "X", null, null));
        using var db = CatalogDbContext.Open(_path);
        var run = db.ImportRuns.Single();
        Assert.Equal(r.ImportId, run.Id);
        Assert.Equal((3, 2, 1, 1), (run.EntriesRead, run.LinksCreated, run.Rejected, run.ProductsInserted));
        Assert.NotNull(run.FinishedAt);
        Assert.All(db.SellerProducts, s => Assert.Equal(run.Id, s.ImportId));
        Assert.All(db.RejectedEntries, x => Assert.Equal(run.Id, x.ImportId));
        Assert.Equal(run.Id, db.Products.Single(p => p.Name == "Brand New Gadget").ImportId);
        Assert.Null(db.Products.Single(p => p.Name == "iPhone 15 Pro").ImportId);
    }

    [Fact]
    public void RunStoresEverythingTheReportShows()
    {
        var r = Run(E(1, "A", "iPhone 15 Pro", "Apple"), E(1, "A", "iPhone 15  Pro", "Apple"),
                    E(2, "A", "iPhone 15 Pro", "Apple"), E(3, "B", "Smart TV Samsung 55", "Samsung"));
        using var db = CatalogDbContext.Open(_path);
        var run = db.ImportRuns.Single();
        Assert.Equal(r.LinkedToExisting, run.LinkedToExisting);
        Assert.Equal(r.AlreadyLinked, run.AlreadyLinked);
    }

    private static async IAsyncEnumerable<SellerProductEntry?> ThenThrow(Exception ex, params SellerProductEntry[] first)
    {
        foreach (var e in first) yield return e;
        await Task.Yield();
        throw ex;
    }

    private ImportReport RunStream(IAsyncEnumerable<SellerProductEntry?> stream)
    {
        using var db = CatalogDbContext.Open(_path);
        SchemaUpgrader.Upgrade(db);
        return new CatalogConsolidator(db).ImportAsync(stream, "test").GetAwaiter().GetResult();
    }

    [Fact]
    public void CompletedRunIsMarkedCompleted()
    {
        var r = Run(E(1, "A", "iPhone 15 Pro", "Apple"));
        Assert.Equal(ImportState.Completed, r.State);
        using var db = CatalogDbContext.Open(_path);
        Assert.Equal(ImportState.Completed, db.ImportRuns.Single().State);
        Assert.Null(db.ImportRuns.Single().Error);
    }

    [Fact]
    public void FeedFailingMidwayRollsBackEverythingAndMarksRunFailed()
    {
        var r = RunStream(ThenThrow(new System.Text.Json.JsonException("bad token"),
            E(1, "A", "Brand New Gadget", "Acme"), E(2, "A", "iPhone 15 Pro", "Apple"),
            new SellerProductEntry("bad", "A", "X", null, null)));

        Assert.Equal(ImportState.Failed, r.State);
        Assert.Contains("malformed feed", r.Error);
        Assert.Equal((3, 0), Counts());          // no product, no link
        using var db = CatalogDbContext.Open(_path);
        Assert.Empty(db.RejectedEntries);          // rejects rolled back too
        var run = db.ImportRuns.Single();          // but the run row survives
        Assert.Equal(ImportState.Failed, run.State);
        Assert.Contains("bad token", run.Error);
        Assert.Equal(0, run.LinksCreated);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public void FailedRunDoesNotBlockNextImport()
    {
        RunStream(ThenThrow(new Exception("boom"), E(1, "A", "iPhone 15 Pro", "Apple")));
        var ok = Run(E(1, "A", "iPhone 15 Pro", "Apple"));
        Assert.Equal(ImportState.Completed, ok.State);
        Assert.Equal((3, 1), Counts());
    }

    [Fact]
    public void DatabaseNewerThanToolIsRefused()
    {
        using (var db = CatalogDbContext.Open(_path))
        {
            SchemaUpgrader.Upgrade(db);
            db.Database.ExecuteSqlRaw("PRAGMA user_version = 99");
        }
        using var db2 = CatalogDbContext.Open(_path);
        Assert.Throws<InvalidOperationException>(() => SchemaUpgrader.Upgrade(db2));
    }

    // feed larger than one chunk (500), so lookups must see rows saved by earlier chunks
    [Fact]
    public void DuplicatesAcrossChunksStillCollapse()
    {
        var entries = Enumerable.Range(1, 1200).Select(i => E(i, "A", $"Chunk Product {i % 10}", "Acme")).ToArray();
        var r = Run(entries);
        Assert.Equal(10, r.ProductsInserted);
        Assert.Equal((3 + 10, 10), Counts());
        Assert.Equal(1200, r.Read);
    }

    [Fact]
    public void SellerIdReuseIsDetectedAcrossChunks()
    {
        var filler = Enumerable.Range(2, 599).Select(i => E(i, "A", $"Filler {i}", "Acme"));
        var r = Run([E(1, "A", "Chunk P0", "Acme"), .. filler, E(1, "A", "Chunk P1", "Acme")]);
        Assert.Equal(1, r.Rejected);
        Assert.Equal(601, r.Read);
    }

    [Fact]
    public void ExistingCatalogProductMatchedInLaterChunk()
    {
        var filler = Enumerable.Range(1, 600).Select(i => E(i, "A", $"Filler {i}", "Acme"));
        var r = Run([.. filler, E(1000, "B", "smart tv  samsung 55\"", "Samsung")]);
        Assert.Equal(600, r.ProductsInserted);
        Assert.Equal(1, r.LinkedToExisting);
        Assert.Equal((3 + 600, 601), Counts());
    }

    [Fact]
    public void TwoRunsGetDifferentIds()
    {
        var a = Run(E(1, "A", "iPhone 15 Pro", "Apple"));
        var b = Run(E(2, "B", "iPhone 15 Pro", "Apple"));
        Assert.NotEqual(a.ImportId, b.ImportId);
    }

    [Fact]
    public void InjectionStringStoredVerbatim()
    {
        const string evil = "TestBrand'; DROP TABLE Product; --";
        Run(E(1, "A", "Security Test Product", evil, "Electronics"));
        using var db = CatalogDbContext.Open(_path);
        Assert.Equal(evil, db.Products.Single(p => p.Name == "Security Test Product").Brand);
        Assert.Equal(4, db.Products.Count());
    }

    [Fact]
    public void SameSellerTwoSkusForSameProductKeepsFirst()
    {
        Run(E(1, "A", "iPhone 15 Pro", "Apple"), E(2, "A", "iPhone 15  Pro", "Apple"));
        Assert.Equal((3, 1), Counts());
    }
}
