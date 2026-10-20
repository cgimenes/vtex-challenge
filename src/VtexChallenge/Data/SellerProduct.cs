namespace VtexChallenge.Data;

public class SellerProduct
{
    public int Id { get; set; }
    public string SellerName { get; set; } = "";
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string SellerProductId { get; set; } = "";
    public Guid? ImportId { get; set; }
}
