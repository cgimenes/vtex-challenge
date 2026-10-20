using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;
using VtexChallenge.Import;

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
        Assert.Equal(r.Notes, run.Notes!.Split('\n'));
        Assert.Single(r.Notes);
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
        var r = Run(E(1, "A", "iPhone 15 Pro", "Apple"), E(2, "A", "iPhone 15  Pro", "Apple"));
        Assert.Equal((3, 1), Counts());
        Assert.Single(r.Notes);
    }
}
