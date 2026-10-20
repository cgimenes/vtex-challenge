using System.Text.Json;
using VtexChallenge.Data;
using VtexChallenge.Import;

var paths = args.Where(a => !a.StartsWith("--")).ToArray();
var entriesPath = paths.ElementAtOrDefault(0) ?? "ProductEntry.json";
var dbPath = paths.ElementAtOrDefault(1) ?? "catalog.db";

using var db = CatalogDbContext.Open(dbPath);
SchemaUpgrader.Upgrade(db);

// stream entries one by one instead of loading the whole file
await using var file = File.OpenRead(entriesPath);
var entries = JsonSerializer.DeserializeAsyncEnumerable<SellerProductEntry>(file);

var report = await new CatalogConsolidator(db).ImportAsync(entries, Path.GetFileName(entriesPath));
Console.WriteLine(report);
Console.WriteLine();
Console.WriteLine($"Save this import id to undo this import later: {report.ImportId}");
