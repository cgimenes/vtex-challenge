using VtexChallenge.Data;

namespace VtexChallenge.Import;

public class ImportReport
{
    public Guid ImportId { get; init; }
    public ImportState State { get; init; }
    public string? Error { get; init; }
    public int Read { get; init; }
    public int ProductsInserted { get; init; }
    public int LinkedToExisting { get; init; }
    public int LinksCreated { get; init; }
    public int AlreadyLinked { get; init; }
    public int Rejected { get; init; }

    public static ImportReport From(ImportRun run) => new()
    {
        ImportId = run.Id,
        State = run.State,
        Error = run.Error,
        Read = run.EntriesRead,
        ProductsInserted = run.ProductsInserted,
        LinkedToExisting = run.LinkedToExisting,
        LinksCreated = run.LinksCreated,
        AlreadyLinked = run.AlreadyLinked,
        Rejected = run.Rejected,
    };

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Import id:                   {ImportId}",
            $"State:                       {State}",
            $"Entries read:                 {Read}",
            $"New products inserted:        {ProductsInserted}",
            $"Matched existing products:    {LinkedToExisting}",
            $"Seller links created:         {LinksCreated}",
            $"Already linked (skipped):     {AlreadyLinked}",
            $"Rejected:                     {Rejected} (saved in RejectedEntry table)",
        };
        if (Error is not null) lines.Add($"Error: {Error}");
        return string.Join(Environment.NewLine, lines);
    }
}
