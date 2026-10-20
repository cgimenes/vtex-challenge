using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VtexChallenge.Data;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<SellerProduct> SellerProducts => Set<SellerProduct>();
    public DbSet<RejectedEntry> RejectedEntries => Set<RejectedEntry>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();

    public static CatalogDbContext Open(string path)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False")
            .Options;
        return new CatalogDbContext(options);
    }

    // EF writes GUIDs as uppercase text; store the lowercase form the CLI prints so ids match in plain SQL
    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<Guid>().HaveConversion<LowerGuidConverter>();

    private class LowerGuidConverter() : ValueConverter<Guid, string>(g => g.ToString("D"), s => Guid.Parse(s));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("Product");
            e.HasIndex(p => p.NormalizedKey);
            e.HasIndex(p => p.NormalizedName);
        });
        b.Entity<ImportRun>(e =>
        {
            e.ToTable("ImportRun");
            e.Property(r => r.State).HasConversion<string>();
        });
        b.Entity<RejectedEntry>(e => e.ToTable("RejectedEntry"));
        b.Entity<SellerProduct>(e =>
        {
            e.ToTable("SellerProduct");
            e.HasOne(s => s.Product).WithMany().HasForeignKey(s => s.ProductId);
            e.HasIndex(s => new { s.SellerName, s.SellerProductId }).IsUnique();
            e.HasIndex(s => new { s.SellerName, s.ProductId }).IsUnique();
        });
    }
}
