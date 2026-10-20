using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;

namespace VtexChallenge.Import;

public class CatalogConsolidator(CatalogDbContext db)
{
    private const int BatchSize = 500;

    private record Candidate(Product Product, string Brand, string Name);

    /// <summary>State of one import: the run being recorded and the notes collected so far.</summary>
    private class Session(ImportRun run)
    {
        public ImportRun Run { get; } = run;
        public List<string> Notes { get; } = [];
    }

    /// <summary>Lookups for one chunk of the feed, fetched from the database for just that chunk.</summary>
    private class Batch
    {
        // normalized name -> products with that name (brand may differ or be missing)
        public Dictionary<string, List<Candidate>> ByName { get; } = [];
        // (seller, sellerProductId) -> product
        public Dictionary<(string, string), Product> Skus { get; } = [];
        // (seller, product) pairs already linked
        public HashSet<(string, Product)> Links { get; } = [];
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
            await ImportEntriesAsync(new Session(run), entries);
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
            Id = Guid.NewGuid(), Source = source, StartedAt = DateTime.UtcNow, State = ImportState.Running,
        };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return run;
    }

    private async Task ImportEntriesAsync(Session session, IAsyncEnumerable<SellerProductEntry?> entries)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        await foreach (var chunk in entries.Chunk(BatchSize))
            await ImportChunkAsync(session, chunk);

        await CompleteRunAsync(session);
        await tx.CommitAsync();
    }

    /// <summary>Memory stays bounded by the chunk: look up only what it needs, save, then forget it.</summary>
    private async Task ImportChunkAsync(Session session, SellerProductEntry?[] chunk)
    {
        var items = chunk.Select(Check).ToList();
        var batch = await LoadBatchAsync(items.Where(i => i.Valid is not null).Select(i => i.Valid!).ToList());

        foreach (var item in items)
            ProcessEntry(session, batch, item);

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
    private async Task<Batch> LoadBatchAsync(List<ValidEntry> valids)
    {
        var batch = new Batch();
        if (valids.Count == 0) return batch;

        var names = valids.Select(v => ProductKey.Normalize(v.Name)).Distinct().ToList();
        var products = await db.Products.Where(p => names.Contains(p.NormalizedName!)).ToListAsync();
        foreach (var p in products)
            Index(batch.ByName, p);

        var productIds = products.Select(p => p.Id).ToList();
        foreach (var bySeller in valids.GroupBy(v => v.Seller))
        {
            var seller = bySeller.Key;
            var skus = bySeller.Select(v => v.Sku).Distinct().ToList();
            var links = await db.SellerProducts
                .Include(sp => sp.Product)
                .Where(sp => sp.SellerName == seller && (skus.Contains(sp.SellerProductId) || productIds.Contains(sp.ProductId)))
                .ToListAsync();
            foreach (var sp in links)
            {
                batch.Skus[(seller, sp.SellerProductId)] = sp.Product!;
                batch.Links.Add((seller, sp.Product!));
            }
        }
        return batch;
    }

    private void ProcessEntry(Session session, Batch batch, Checked item)
    {
        var run = session.Run;
        run.EntriesRead++;
        if (item.Valid is not { } valid)
        {
            Reject(session, item.Entry, item.Error!);
            return;
        }

        var match = Find(batch.ByName, valid.Brand, valid.Name);

        if (batch.Skus.TryGetValue((valid.Seller, valid.Sku), out var owned))
        {
            if (match == owned) run.AlreadyLinked++;
            else Reject(session, item.Entry, $"seller reused this id for a different product (\"{owned.Name}\")");
            return;
        }

        var product = match is null ? InsertProduct(session, batch, valid) : FillGaps(session, match, valid);
        LinkSeller(session, batch, item.Entry!, valid, product);
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

    private Product InsertProduct(Session session, Batch batch, ValidEntry entry)
    {
        var product = new Product
        {
            Name = entry.Name, Brand = entry.Brand, Category = entry.Category,
            NormalizedKey = ProductKey.Build(entry.Brand, entry.Name),
            NormalizedName = ProductKey.Normalize(entry.Name), ImportId = session.Run.Id,
        };
        db.Products.Add(product);
        Index(batch.ByName, product);
        session.Run.ProductsInserted++;
        return product;
    }

    /// <summary>Catalog stays source of truth: an existing product only gets its missing brand/category filled.</summary>
    private static Product FillGaps(Session session, Product product, ValidEntry entry)
    {
        // the product is tracked (loaded for this chunk), so plain assignment is saved with the chunk
        product.Brand ??= entry.Brand;
        product.Category ??= entry.Category;
        product.NormalizedKey = ProductKey.Build(product.Brand, product.Name);
        session.Run.LinkedToExisting++;
        return product;
    }

    private void LinkSeller(Session session, Batch batch, SellerProductEntry entry, ValidEntry valid, Product product)
    {
        if (!batch.Links.Add((valid.Seller, product)))
        {
            var label = $"{entry.SellerName}/{entry.Id} \"{entry.Name}\"";
            session.Notes.Add($"{label}: seller already offers \"{product.Name}\" under another id, kept first");
            return;
        }
        batch.Skus[(valid.Seller, valid.Sku)] = product;

        db.SellerProducts.Add(new SellerProduct
        {
            SellerName = valid.Seller, SellerProductId = valid.Sku, Product = product, ImportId = session.Run.Id,
        });
        session.Run.LinksCreated++;
    }

    private void Reject(Session session, SellerProductEntry? entry, string reason)
    {
        session.Run.Rejected++;
        db.RejectedEntries.Add(new RejectedEntry
        {
            SellerName = entry?.SellerName, SellerProductId = entry?.Id, Name = entry?.Name,
            Brand = entry?.Brand, Category = entry?.Category,
            Reason = reason, RejectedAt = DateTime.UtcNow, ImportId = session.Run.Id,
        });
    }

    private async Task CompleteRunAsync(Session session)
    {
        var run = session.Run;
        run.State = ImportState.Completed;
        run.FinishedAt = DateTime.UtcNow;
        run.Notes = session.Notes.Count > 0 ? string.Join('\n', session.Notes) : null;
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
        run.Notes = null;

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

    private static void Index(Dictionary<string, List<Candidate>> byName, Product p)
    {
        var c = new Candidate(p, ProductKey.Normalize(p.Brand), ProductKey.Normalize(p.Name));
        if (!byName.TryGetValue(c.Name, out var list)) byName[c.Name] = list = [];
        list.Add(c);
    }

    /// <summary>
    /// Exact brand+name wins. If either side has no brand, fall back to name only,
    /// but only when that is unambiguous (avoids false merges).
    /// </summary>
    private static Product? Find(Dictionary<string, List<Candidate>> byName, string? brand, string name)
    {
        if (!byName.TryGetValue(ProductKey.Normalize(name), out var list)) return null;
        var nb = ProductKey.Normalize(brand);

        var exact = list.Where(c => c.Brand == nb).Select(c => c.Product).Distinct().ToList();
        if (exact.Count > 0) return exact.OrderBy(p => p.Id).First();

        var loose = list.Where(c => nb == "" || c.Brand == "").Select(c => c.Product).Distinct().ToList();
        return loose.Count == 1 ? loose[0] : null;
    }
}
