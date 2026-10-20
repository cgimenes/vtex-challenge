using VtexChallenge.Data;

namespace VtexChallenge.Import;

public class ImportReport
{
    public Guid ImportId { get; init; }
    public int Read { get; init; }
    public int ProductsInserted { get; init; }
    public int LinkedToExisting { get; init; }
    public int LinksCreated { get; init; }
    public int AlreadyLinked { get; init; }
    public int Rejected { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public static ImportReport From(ImportRun run) => new()
    {
        ImportId = run.Id,
        Read = run.EntriesRead,
        ProductsInserted = run.ProductsInserted,
        LinkedToExisting = run.LinkedToExisting,
        LinksCreated = run.LinksCreated,
        AlreadyLinked = run.AlreadyLinked,
        Rejected = run.Rejected,
        Notes = run.Notes?.Split('\n') ?? [],
    };

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Import id:                   {ImportId}",
            $"Entries read:                 {Read}",
            $"New products inserted:        {ProductsInserted}",
            $"Matched existing products:    {LinkedToExisting}",
            $"Seller links created:         {LinksCreated}",
            $"Already linked (skipped):     {AlreadyLinked}",
            $"Rejected:                     {Rejected} (saved in RejectedEntry table)",
        };
        if (Notes.Count > 0) { lines.Add("Notes:"); lines.AddRange(Notes.Select(n => "  " + n)); }
        return string.Join(Environment.NewLine, lines);
    }
}
