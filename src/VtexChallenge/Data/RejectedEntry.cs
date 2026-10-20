namespace VtexChallenge.Data;

public class RejectedEntry
{
    public int Id { get; set; }
    public string? SellerName { get; set; }
    public string? SellerProductId { get; set; }
    public string? Name { get; set; }
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public string Reason { get; set; } = "";
    public string? Source { get; set; }
    public DateTime RejectedAt { get; set; }
    public Guid? ImportId { get; set; }
}
