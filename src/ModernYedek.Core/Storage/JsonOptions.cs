using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernYedek.Core.Storage;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = Create(true);
    public static readonly JsonSerializerOptions Compact = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
