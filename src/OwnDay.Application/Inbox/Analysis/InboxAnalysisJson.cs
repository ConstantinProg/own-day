using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using OwnDay.Domain.Inbox;

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
        options.Converters.Add(new InboxTargetKindConverter());
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new ExplicitUtcInstantConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class InboxTargetKindConverter : JsonConverter<InboxTargetKind>
    {
        public override InboxTargetKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("An exact Inbox target kind name is required.");
            }

            return reader.GetString() switch
            {
                nameof(InboxTargetKind.Action) => InboxTargetKind.Action,
                nameof(InboxTargetKind.Project) => InboxTargetKind.Project,
                nameof(InboxTargetKind.WaitingFor) => InboxTargetKind.WaitingFor,
                nameof(InboxTargetKind.Reference) => InboxTargetKind.Reference,
                nameof(InboxTargetKind.SomedayMaybe) => InboxTargetKind.SomedayMaybe,
                _ => throw new JsonException("An exact Inbox target kind name is required.")
            };
        }

        public override void Write(Utf8JsonWriter writer, InboxTargetKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Unknown Inbox target kind."));
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
