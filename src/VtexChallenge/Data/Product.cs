namespace VtexChallenge.Data;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public string? NormalizedKey { get; set; }
    /// <summary>Normalized name only (no brand). Indexed; used to fetch match candidates.</summary>
    public string? NormalizedName { get; set; }
    public Guid? ImportId { get; set; }
}
