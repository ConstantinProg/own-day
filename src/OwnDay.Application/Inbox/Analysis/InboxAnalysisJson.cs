using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace OwnDay.Application.Inbox.Analysis;

public static class InboxAnalysisJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            MaxDepth = 16
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new ExplicitUtcInstantConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class ExplicitUtcInstantConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } text ||
                (!text.EndsWith('Z') && !text.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !reader.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero)
            {
                throw new JsonException("An explicit UTC instant is required.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
