namespace VtexChallenge.Data;

/// <summary>One execution of the importer. Every row it creates carries this Id.</summary>
public class ImportRun
{
    public Guid Id { get; set; }
    public string? Source { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int EntriesRead { get; set; }
    public int ProductsInserted { get; set; }
    public int LinksCreated { get; set; }
    public int Rejected { get; set; }
}
