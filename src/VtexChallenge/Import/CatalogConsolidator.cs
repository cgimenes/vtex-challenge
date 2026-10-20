using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VtexChallenge.Data;

namespace VtexChallenge.Import;

public class CatalogConsolidator(CatalogDbContext db)
{
    private const int BatchSize = 500;

    private record Candidate(Product Product, string Brand, string Name);

    /// <summary>
    /// Imports the feed atomically: either every row is saved or none is. Failures (malformed feed,
    /// database error, cancellation) are recorded on the ImportRun as Failed and returned in the report.
    /// </summary>
    public async Task<ImportReport> ImportAsync(
        IAsyncEnumerable<SellerProductEntry?> entries, string? source = null, CancellationToken ct = default)
    {
        var run = new ImportRun
        {
            Id = Guid.NewGuid(), Source = source, StartedAt = DateTime.UtcNow, State = ImportState.Running,
        };

        // committed on its own, before the data transaction, so a failed import still leaves a trace
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        try
        {
            await ImportCoreAsync(run, entries, source, ct);
            return ImportReport.From(run);
        }
        catch (Exception ex)
        {
            // the transaction was disposed uncommitted, so none of this run's data was saved
            db.ChangeTracker.Clear();
            var error = $"{Describe(ex)} (after {run.EntriesRead} entries, nothing was saved)";
            run.State = ImportState.Failed;
            run.Error = error;
            run.FinishedAt = DateTime.UtcNow;
            run.EntriesRead = run.ProductsInserted = run.LinksCreated = run.Rejected = 0;
            run.LinkedToExisting = run.AlreadyLinked = 0;
            run.Notes = null;

            await db.ImportRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, run.State)
                .SetProperty(r => r.Error, run.Error)
                .SetProperty(r => r.FinishedAt, run.FinishedAt), CancellationToken.None);
            return ImportReport.From(run);
        }
    }

    private async Task ImportCoreAsync(
        ImportRun run, IAsyncEnumerable<SellerProductEntry?> entries, string? source, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // normalized name -> products with that name (brand may differ or be missing)
        var byName = new Dictionary<string, List<Candidate>>();
        foreach (var p in db.Products.ToList())
            Index(byName, p);

        // (seller, sellerProductId) -> product, and (seller, product) pairs
        var skus = new Dictionary<(string, string), Product>();
        var links = new HashSet<(string, Product)>();
        foreach (var sp in db.SellerProducts.Include(s => s.Product).ToList())
        {
            skus[(sp.SellerName, sp.SellerProductId)] = sp.Product!;
            links.Add((sp.SellerName, sp.Product!));
        }

        var pending = 0;
        var notes = new List<string>();

        void Reject(SellerProductEntry? e, string reason)
        {
            run.Rejected++;
            pending++;
            db.RejectedEntries.Add(new RejectedEntry
            {
                SellerName = e?.SellerName, SellerProductId = e?.Id, Name = e?.Name,
                Brand = e?.Brand, Category = e?.Category,
                Reason = reason, Source = source, RejectedAt = DateTime.UtcNow, ImportId = run.Id,
            });
        }

        await foreach (var e in entries.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            run.EntriesRead++;
            if (e is null)
            {
                Reject(null, "null entry");
                continue;
            }
            var label = $"{e.SellerName}/{e.Id} \"{e.Name}\"";

            var name = e.Name?.Trim();
            var seller = e.SellerName?.Trim();
            if (string.IsNullOrEmpty(seller) || string.IsNullOrEmpty(name))
            {
                Reject(e, "missing seller or name");
                continue;
            }
            if (!Guid.TryParseExact(e.Id?.Trim(), "D", out var guid))
            {
                Reject(e, "invalid GUID id");
                continue;
            }
            var sku = guid.ToString("D");
            var brand = Blank(e.Brand);
            var category = Blank(e.Category);

            var match = Find(byName, brand, name);

            if (skus.TryGetValue((seller, sku), out var owned))
            {
                if (match == owned) run.AlreadyLinked++;
                else Reject(e, $"seller reused this id for a different product (\"{owned.Name}\")");
                continue;
            }

            if (match is null)
            {
                match = new Product
                {
                    Name = name, Brand = brand, Category = category,
                    NormalizedKey = ProductKey.Build(brand, name), ImportId = run.Id,
                };
                db.Products.Add(match);
                Index(byName, match);
                run.ProductsInserted++;
            }
            else
            {
                // catalog stays source of truth: only fill gaps
                if ((match.Brand is null && brand is not null) || (match.Category is null && category is not null))
                {
                    match.Brand ??= brand;
                    match.Category ??= category;
                    match.NormalizedKey = ProductKey.Build(match.Brand, match.Name);
                    db.Products.Update(match); // may be detached after a batch flush
                }
                run.LinkedToExisting++;
            }

            if (!links.Add((seller, match)))
            {
                notes.Add($"{label}: seller already offers \"{match.Name}\" under another id, kept first");
                continue;
            }
            skus[(seller, sku)] = match;
            // new products have no Id until flushed; flushed ones may be detached, so use the FK
            var link = new SellerProduct { SellerName = seller, SellerProductId = sku, ImportId = run.Id };
            if (match.Id == 0) link.Product = match; else link.ProductId = match.Id;
            db.SellerProducts.Add(link);
            run.LinksCreated++;

            // flush periodically and drop tracked entities so memory stays flat
            if (++pending >= BatchSize)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        run.State = ImportState.Completed;
        run.FinishedAt = DateTime.UtcNow;
        run.Notes = notes.Count > 0 ? string.Join('\n', notes) : null;
        db.ImportRuns.Update(run);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "import cancelled",
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
