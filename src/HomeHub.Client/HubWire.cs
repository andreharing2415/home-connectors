using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeConnectors;

namespace HomeHub.Client;

/// <summary>
/// The hub's HTTP contract, shared by the server and every client so the two
/// cannot drift: the routes, the bodies, and the JSON settings — web casing,
/// enums as their names (a phone app reads <c>"Climate"</c>, not <c>3</c>).
/// </summary>
public static class HubWire
{
    public const string TokenHeader = "Authorization";

    public static JsonSerializerOptions Json { get; } = Create();

    public static string DeviceState(string id) => $"api/devices/{Uri.EscapeDataString(id)}/state";

    public static string DeviceCommand(string id) => $"api/devices/{Uri.EscapeDataString(id)}/command";

    public static string DeviceStream(string id) => $"api/devices/{Uri.EscapeDataString(id)}/stream";

    public static string DeviceFrame(string id) => $"api/devices/{Uri.EscapeDataString(id)}/frame";

    public const string Devices = "api/devices";

    public const string Providers = "api/providers";

    public const string Health = "api/health";

    public const string Consumption = "api/consumption";

    public static string Discover(HomeProvider provider) => $"api/providers/{provider}/discover";

    public static string Credentials(HomeProvider provider) => $"api/providers/{provider}/credentials";

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ImmutableArrayConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Whether each provider has keys on the hub. Never the keys.</summary>
public sealed record ProviderStatus(HomeProvider Provider, bool Configured);

/// <summary>The keys for one provider, on their way in. The hub never sends them back out.</summary>
public sealed record CredentialsBody(string AccessId, string Secret, string Region);

/// <summary>A consumption query: which devices and metrics, over which window, at which granularity.</summary>
public sealed record ConsumptionQuery(
    ImmutableArray<string> DeviceIds,
    ImmutableArray<ConsumptionMetric> Metrics,
    DateTimeOffset Since,
    DateTimeOffset Until,
    ConsumptionGranularity Granularity);

/// <summary>The hub's own health: up, and whether its vault is open.</summary>
public sealed record HubHealth(bool Ok, bool VaultUnsealed, int Devices);

/// <summary>
/// The model uses <c>default(ImmutableArray)</c> for "none reported", and that
/// value cannot be enumerated: it is written as an empty array and read back
/// as one.
/// </summary>
internal sealed class ImmutableArrayConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? []
                : [.. JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []];

        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            if (!value.IsDefault)
            {
                foreach (var item in value)
                {
                    JsonSerializer.Serialize(writer, item, options);
                }
            }

            writer.WriteEndArray();
        }
    }
}
