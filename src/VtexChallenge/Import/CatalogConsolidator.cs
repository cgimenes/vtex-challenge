using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;

namespace VtexChallenge.Import;

public class CatalogConsolidator(CatalogDbContext db)
{
    private const int BatchSize = 500;

    private record Candidate(Product Product, string Brand, string Name)
    {
        public static Candidate From(Product p) => new(p, ProductKey.Normalize(p.Brand), ProductKey.Normalize(p.Name));
    }

    /// <summary>Lookups for one chunk of the feed, fetched from the database for just that chunk.</summary>
    private class Batch(
        Dictionary<string, List<Candidate>> byName,
        Dictionary<(string, string), Product> skus,
        HashSet<(string, Product)> links)
    {
        // normalized name -> products with that name (brand may differ or be missing)
        public Dictionary<string, List<Candidate>> ByName { get; } = byName;
        // (seller, sellerProductId) -> product
        public Dictionary<(string, string), Product> Skus { get; } = skus;
        // (seller, product) pairs already linked
        public HashSet<(string, Product)> Links { get; } = links;

        /// <summary>Makes a product inserted during this chunk matchable by the entries that follow.</summary>
        public void AddProduct(Product product)
        {
            var candidate = Candidate.From(product);
            if (!ByName.TryGetValue(candidate.Name, out var sameName)) ByName[candidate.Name] = sameName = [];
            sameName.Add(candidate);
        }
    }

    /// <summary>A feed entry with its validation outcome.</summary>
    private record Checked(SellerProductEntry? Entry, ValidEntry? Valid, string? Error);

    /// <summary>A feed entry that passed validation, trimmed and ready to match.</summary>
    private record ValidEntry(string Seller, string Name, string Sku, string? Brand, string? Category);

    /// <summary>
    /// Imports the feed atomically: either every row is saved or none is. Failures (malformed feed,
    /// database error) are recorded on the ImportRun as Failed and returned in the report.
    /// </summary>
    public async Task<ImportReport> ImportAsync(
        IAsyncEnumerable<SellerProductEntry?> entries, string? source = null)
    {
        var run = await StartRunAsync(source);
        try
        {
            await ImportEntriesAsync(run, entries);
        }
        catch (Exception ex)
        {
            await FailRunAsync(run, ex);
        }
        return ImportReport.From(run);
    }

    /// <summary>Committed on its own, before the data transaction, so a failed import still leaves a trace.</summary>
    private async Task<ImportRun> StartRunAsync(string? source)
    {
        var run = new ImportRun
        {
            Id = Guid.NewGuid(),
            Source = source,
            StartedAt = DateTime.UtcNow,
            State = ImportState.Running,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return run;
    }

    private async Task ImportEntriesAsync(ImportRun run, IAsyncEnumerable<SellerProductEntry?> entries)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        await foreach (var chunk in entries.Chunk(BatchSize))
            await ImportChunkAsync(run, chunk);

        await CompleteRunAsync(run);
        await tx.CommitAsync();
    }

    /// <summary>Memory stays bounded by the chunk: look up only what it needs, save, then forget it.</summary>
    private async Task ImportChunkAsync(ImportRun run, SellerProductEntry?[] chunk)
    {
        var items = chunk.Select(Check);
        var batch = await LoadBatchAsync(items.Where(i => i.Valid is not null).Select(i => i.Valid!));

        foreach (var item in items)
            ProcessEntry(run, batch, item);

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Checked Check(SellerProductEntry? entry)
    {
        if (entry is null) return new Checked(null, null, "null entry");
        var (valid, error) = Validate(entry);
        return new Checked(entry, valid, error);
    }

    /// <summary>
    /// Fetches only the rows this chunk can match: products sharing a normalized name with an entry,
    /// and each seller's links that use one of the entry ids or point at one of those products.
    /// </summary>
    private async Task<Batch> LoadBatchAsync(IEnumerable<ValidEntry> valids)
    {
        if (valids.Count() == 0) return new Batch([], [], []);

        var names = valids.Select(v => ProductKey.Normalize(v.Name)).Distinct();
        var products = db.Products.Where(p => names.Contains(p.NormalizedName!));
        var byName = products
            .Select(Candidate.From)
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.ToList());

        var productIds = products.Select(p => p.Id);
        var skus = new Dictionary<(string, string), Product>();
        var links = new HashSet<(string, Product)>();
        foreach (var bySeller in valids.GroupBy(v => v.Seller))
        {
            var seller = bySeller.Key;
            var sellerSkus = bySeller.Select(v => v.Sku).Distinct();
            var sellerLinks = db.SellerProducts
                .Include(sp => sp.Product)
                .Where(sp => sp.SellerName == seller && (sellerSkus.Contains(sp.SellerProductId) || productIds.Contains(sp.ProductId)));
            foreach (var sp in sellerLinks)
            {
                skus[(seller, sp.SellerProductId)] = sp.Product!;
                links.Add((seller, sp.Product!));
            }
        }

        return new Batch(byName, skus, links);
    }

    private void ProcessEntry(ImportRun run, Batch batch, Checked item)
    {
        run.EntriesRead++;
        if (item.Valid is not { } valid)
        {
            Reject(run, item.Entry, item.Error!);
            return;
        }

        var match = Find(batch.ByName, valid.Brand, valid.Name);

        if (batch.Skus.TryGetValue((valid.Seller, valid.Sku), out var owned))
        {
            if (match == owned) run.AlreadyLinked++;
            else Reject(run, item.Entry, $"seller reused this id for a different product (\"{owned.Name}\")");
            return;
        }

        var product = match is null ? InsertProduct(run, batch, valid) : FillGaps(run, match, valid);
        LinkSeller(run, batch, valid, product);
    }

    private static (ValidEntry? Valid, string? Error) Validate(SellerProductEntry entry)
    {
        var name = entry.Name?.Trim();
        var seller = entry.SellerName?.Trim();
        if (string.IsNullOrEmpty(seller) || string.IsNullOrEmpty(name))
            return (null, "missing seller or name");
        if (!Guid.TryParseExact(entry.Id?.Trim(), "D", out var guid))
            return (null, "invalid GUID id");

        return (new ValidEntry(seller, name, guid.ToString("D"), Blank(entry.Brand), Blank(entry.Category)), null);
    }

    private Product InsertProduct(ImportRun run, Batch batch, ValidEntry entry)
    {
        var product = new Product
        {
            Name = entry.Name,
            Brand = entry.Brand,
            Category = entry.Category,
            NormalizedKey = ProductKey.Build(entry.Brand, entry.Name),
            NormalizedName = ProductKey.Normalize(entry.Name),
            ImportId = run.Id,
        };
        db.Products.Add(product);
        // not saved until the chunk ends, so the batch lookup can't see it yet; without this,
        // a later entry in the same chunk for the same product would insert a duplicate
        batch.AddProduct(product);
        run.ProductsInserted++;
        return product;
    }

    /// <summary>Catalog stays source of truth: an existing product only gets its missing brand/category filled.</summary>
    private static Product FillGaps(ImportRun run, Product product, ValidEntry entry)
    {
        // the product is tracked (loaded for this chunk), so plain assignment is saved with the chunk
        product.Brand ??= entry.Brand;
        product.Category ??= entry.Category;
        product.NormalizedKey = ProductKey.Build(product.Brand, product.Name);
        run.LinkedToExisting++;
        return product;
    }

    private void LinkSeller(ImportRun run, Batch batch, ValidEntry valid, Product product)
    {
        // seller already offers this product under another id: keep the first link
        if (!batch.Links.Add((valid.Seller, product))) return;
        batch.Skus[(valid.Seller, valid.Sku)] = product;

        db.SellerProducts.Add(new SellerProduct
        {
            SellerName = valid.Seller,
            SellerProductId = valid.Sku,
            Product = product,
            ImportId = run.Id,
        });
        run.LinksCreated++;
    }

    private void Reject(ImportRun run, SellerProductEntry? entry, string reason)
    {
        run.Rejected++;
        db.RejectedEntries.Add(new RejectedEntry
        {
            SellerName = entry?.SellerName,
            SellerProductId = entry?.Id,
            Name = entry?.Name,
            Brand = entry?.Brand,
            Category = entry?.Category,
            Reason = reason,
            RejectedAt = DateTime.UtcNow,
            ImportId = run.Id,
        });
    }

    private async Task CompleteRunAsync(ImportRun run)
    {
        run.State = ImportState.Completed;
        run.FinishedAt = DateTime.UtcNow;
        db.ImportRuns.Update(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>The data transaction was disposed uncommitted, so none of this run's data was saved.</summary>
    private async Task FailRunAsync(ImportRun run, Exception ex)
    {
        db.ChangeTracker.Clear();
        run.State = ImportState.Failed;
        run.Error = $"{Describe(ex)} (after {run.EntriesRead} entries, nothing was saved)";
        run.FinishedAt = DateTime.UtcNow;
        run.EntriesRead = run.ProductsInserted = run.LinksCreated = run.Rejected = 0;
        run.LinkedToExisting = run.AlreadyLinked = 0;

        await db.ImportRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.State, run.State)
            .SetProperty(r => r.Error, run.Error)
            .SetProperty(r => r.FinishedAt, run.FinishedAt));
    }

    private static string Describe(Exception ex) => ex switch
    {
        JsonException j => $"malformed feed: {j.Message}",
        DbUpdateException { InnerException: { } inner } => $"database error: {inner.Message}",
        _ => ex.Message,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Exact brand+name wins. If either side has no brand, fall back to name only,
    /// but only when that is unambiguous (avoids false merges).
    /// </summary>
    private static Product? Find(Dictionary<string, List<Candidate>> byName, string? brand, string name)
    {
        if (!byName.TryGetValue(ProductKey.Normalize(name), out var list)) return null;
        var nb = ProductKey.Normalize(brand);

        var exact = list.Where(c => c.Brand == nb).Select(c => c.Product).Distinct();
        if (exact.Count() > 0) return exact.OrderBy(p => p.Id).First();

        var loose = list.Where(c => nb == "" || c.Brand == "").Select(c => c.Product).Distinct();
        return loose.FirstOrDefault();
    }
}
