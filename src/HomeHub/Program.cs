using System.Text.Json.Serialization;
using HomeConnectors;
using HomeConnectors.Adapters;
using HomeConnectors.Adapters.Secrets;
using HomeHub;
using HomeHub.Client;
using HomeHub.Vault;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working directory is System32; settings and the
    // bundled OpenBao live beside the executable.
    ContentRootPath = AppContext.BaseDirectory,
});

// The installer writes this machine's paths here, so a reinstall over the
// published files does not have to edit appsettings.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddWindowsService(options => options.ServiceName = "HomeHub");
builder.Services.AddSystemd();

builder.Services.AddOptions<HubOptions>().Bind(builder.Configuration.GetSection(HubOptions.SectionName));
var hub = builder.Configuration.GetSection(HubOptions.SectionName).Get<HubOptions>() ?? new HubOptions();
Directory.CreateDirectory(hub.DataDirectory);

// The catalog is its own file so the owner can edit it without touching the
// hub's settings — and so the desktop assistant and the hub can share one list.
builder.Configuration.AddJsonFile(hub.CatalogPath, optional: false, reloadOnChange: false);
builder.Services.AddOptions<CatalogOptions>()
    .Bind(builder.Configuration.GetSection(hub.CatalogSection))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CatalogOptions>, CatalogOptionsValidator>();
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<CatalogOptions>>().Value.ToCatalog());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in HubWire.Json.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton<ClientToken>();

// The vault, and the stores that read it.
builder.Services.AddSingleton<OpenBaoVault>();
builder.Services.AddHostedService<VaultTokenRenewer>();
builder.Services.AddSingleton<IHomeSecretStore>(provider =>
{
    var vault = provider.GetRequiredService<OpenBaoVault>();
    return new OpenBaoSecretStore(hub.OpenBao.Address, () => vault.Token, hub.OpenBao.Mount, hub.OpenBao.Prefix);
});
builder.Services.AddSingleton<IHomeCredentialStore, SecretStoreCredentialStore>();

// Cameras: one ffmpeg per watched camera, served on loopback.
builder.Services.AddSingleton(provider => new HikvisionStreamServer(
    Path.Combine(hub.DataDirectory, "hikvision-streams"),
    provider.GetRequiredService<TimeProvider>(),
    hub.FfmpegPath));

builder.Services.AddHomeConnectors();

// Consumption: the hub is the one sampler, so no minute is counted twice.
builder.Services.AddSingleton(new ConsumptionStoreOptions { DatabasePath = Path.Combine(hub.DataDirectory, "consumo.db") });
builder.Services.AddSingleton(provider => new SqliteConsumptionStore(
    provider.GetRequiredService<ConsumptionStoreOptions>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IConsumptionStore>(provider => provider.GetRequiredService<SqliteConsumptionStore>());
builder.Services.AddHostedService(provider => new ConsumptionSampler(
    provider.GetRequiredService<HomeCatalog>(),
    provider.GetRequiredService<IHomeDeviceGateway>(),
    provider.GetRequiredService<SqliteConsumptionStore>(),
    provider.GetRequiredService<TimeProvider>()));

var app = builder.Build();

// The vault opens before the first request can ask it for a key.
await app.Services.GetRequiredService<OpenBaoVault>().OpenAsync(CancellationToken.None).ConfigureAwait(false);

var token = app.Services.GetRequiredService<ClientToken>();
HubLog.Ready(app.Logger, app.Services.GetRequiredService<HomeCatalog>().All.Length, token.Path);

app.MapHub();

await app.RunAsync().ConfigureAwait(false);
