using System.Data.Common;
using System.Text.Json;
using VtexChallenge.Data;
using VtexChallenge.Import;

// exit codes: 0 ok, 1 import failed (rolled back), 2 bad input or database
var (entriesPath, dbPath) = ParseArgs(args);

if (CheckInputs(entriesPath, dbPath) is { } inputError)
    return Fail(2, inputError);

using var db = CatalogDbContext.Open(dbPath);
if (TryUpgrade(db) is { } dbError)
    return Fail(2, $"Cannot use database {dbPath}: {dbError}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

ImportReport report;
try
{
    report = await ImportFileAsync(db, entriesPath, cts.Token);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    return Fail(2, $"Cannot read {entriesPath}: {ex.Message}");
}

return PrintResult(report);

static (string EntriesPath, string DbPath) ParseArgs(string[] args)
{
    var paths = args.Where(a => !a.StartsWith("--")).ToArray();
    return (paths.ElementAtOrDefault(0) ?? "ProductEntry.json", paths.ElementAtOrDefault(1) ?? "catalog.db");
}

static string? CheckInputs(string entriesPath, string dbPath)
{
    if (!File.Exists(entriesPath)) return $"Entries file not found: {entriesPath}";
    // opening a missing path would make SQLite silently create an empty database
    if (!File.Exists(dbPath)) return $"Database not found: {dbPath}";
    return null;
}

/// <summary>Returns the error message, or null when the database is ready to use.</summary>
static string? TryUpgrade(CatalogDbContext db)
{
    try
    {
        SchemaUpgrader.Upgrade(db);
        return null;
    }
    catch (Exception ex) when (ex is InvalidOperationException or DbException)
    {
        return ex.Message;
    }
}

static async Task<ImportReport> ImportFileAsync(CatalogDbContext db, string entriesPath, CancellationToken ct)
{
    // stream entries one by one instead of loading the whole file
    await using var file = File.OpenRead(entriesPath);
    var entries = JsonSerializer.DeserializeAsyncEnumerable<SellerProductEntry>(file,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    return await new CatalogConsolidator(db).ImportAsync(entries, Path.GetFileName(entriesPath), ct);
}

static int PrintResult(ImportReport report)
{
    Console.WriteLine(report);
    Console.WriteLine();
    if (report.State != ImportState.Completed)
        return Fail(1, $"Import {report.ImportId} FAILED, nothing was saved. See ImportRun.Error for details.");

    Console.WriteLine($"Save this import id to undo this import later: {report.ImportId}");
    return 0;
}

static int Fail(int code, string message)
{
    Console.Error.WriteLine(message);
    return code;
}
