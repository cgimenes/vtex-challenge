using System.Globalization;
using System.Text;

namespace VtexChallenge.Import;

/// <summary>
/// Identity of a product = normalized brand + normalized name. Category is excluded on purpose:
/// sellers disagree on it ("Photo" vs "Photography") for the same item.
/// </summary>
public static class ProductKey
{
    // Deliberately tiny and explicit: translations seen in seller feeds.
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["processador"] = "processor",
        ["roteador"] = "router",
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            // inch/quote marks vary (55, 55", 12.9''), so drop them entirely
            if (c is '"' or '\'' or '’' or '”' or '″') continue;
            sb.Append(char.ToLowerInvariant(c));
        }

        var tokens = sb.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Synonyms.GetValueOrDefault(t, t));
        return string.Join(' ', tokens);
    }

    public static string Build(string? brand, string? name) => $"{Normalize(brand)}|{Normalize(name)}";
}
