namespace UniMap.ServicePoints.API.Text;

/// <summary>
/// Turkish-safe text normalization. Must stay in sync with the SQL function "Services".normalize_text.
/// </summary>
public static class TurkishNormalizer
{
    private const string From = "çÇğĞıIİöÖşŞüÜ";
    private const string To = "ccggiiioossuu";

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        return string.Create(input.Length, input, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var idx = From.IndexOf(source[i]);
                span[i] = idx >= 0 ? To[idx] : source[i];
            }
        }).ToLowerInvariant();
    }
}
