namespace VtexChallenge.Import;

public class ImportReport
{
    public Guid ImportId { get; init; }
    public int Read { get; set; }
    public int ProductsInserted { get; set; }
    public int LinkedToExisting { get; set; }
    public int LinksCreated { get; set; }
    public int AlreadyLinked { get; set; }
    public int Rejected { get; set; }
    public List<string> Notes { get; } = [];

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
