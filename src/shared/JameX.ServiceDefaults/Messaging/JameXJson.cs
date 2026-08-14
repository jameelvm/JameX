using System.Text.Json;
using System.Text.Json.Serialization;

namespace JameX.ServiceDefaults.Messaging;

/// <summary>
/// One serializer configuration shared by every service, for both HTTP payloads
/// and event bodies. Services that disagree about serialization silently fail
/// to talk to each other, so this is deliberately not per-service.
/// </summary>
public static class JameXJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums stay numeric on the wire. The contract enums document that
        // their numeric values are part of the contract, so a consumer built
        // against an older version still deserializes known members correctly
        // instead of throwing on an unrecognised string.
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
