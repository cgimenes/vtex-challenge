using VtexChallenge.Import;

namespace VtexChallenge.Tests;

public class ProductKeyTests
{
    [Theory]
    [InlineData("iPhone 15  Pro", "iphone 15 pro")]
    [InlineData("  Câmera Canon  ", "camera canon")]
    [InlineData("Smart TV Samsung 55\"", "smart tv samsung 55")]
    [InlineData("Tablet iPad Pro 12.9''", "tablet ipad pro 12.9")]
    [InlineData("Processador AMD Ryzen 9", "processor amd ryzen 9")]
    [InlineData("Roteador WiFi 6 TP-Link", "router wifi 6 tp-link")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize(string? input, string expected) =>
        Assert.Equal(expected, ProductKey.Normalize(input));

    [Fact]
    public void BrandCaseDoesNotMatter() =>
        Assert.Equal(ProductKey.Build("BLACK+DECKER", "Grass Trimmer String"),
                     ProductKey.Build("Black+Decker", "Grass  Trimmer String"));

    [Fact]
    public void CategoryNotPartOfKey() =>
        Assert.Equal("canon|camera", ProductKey.Build("Canon", "Camera"));
}
