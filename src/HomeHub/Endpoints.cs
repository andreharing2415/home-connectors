using System.Collections.Immutable;
using HomeConnectors;
using HomeHub.Client;
using HomeHub.Vault;

namespace HomeHub;

/// <summary>
/// The hub's HTTP surface. Every device route takes an id and looks it up in
/// the hub's own catalog — a device the owner did not list is a 404, whatever a
/// client sends. Every device answer is a <see cref="HomeReply{T}"/>, success or
/// not, so the vendor's own failure code reaches the client intact.
/// </summary>
public static class Endpoints
{
    public static void MapHub(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var token = context.HttpContext.RequestServices.GetRequiredService<ClientToken>();

            return token.Accepts(context.HttpContext.Request.Headers.Authorization)
                ? await next(context).ConfigureAwait(false)
                : Results.Unauthorized();
        });

        api.MapGet("/health", (HomeCatalog catalog, OpenBaoVault vault) =>
            Json(HomeReply.Success(new HubHealth(true, vault.IsOpen, catalog.All.Length))));

        api.MapGet("/devices", (HomeCatalog catalog) => Json(HomeReply.Success(catalog.All)));

        api.MapGet("/devices/{id}/state", async (string id, HomeCatalog catalog, IHomeDeviceGateway gateway, CancellationToken ct) =>
            catalog.Find(id) is { } device
                ? Json(await gateway.ReadStateAsync(device, ct).ConfigureAwait(false))
                : Results.NotFound());

        api.MapPost("/devices/{id}/command", async (string id, HomeCommand command, HomeCatalog catalog, IHomeDeviceGateway gateway, CancellationToken ct) =>
        {
            if (catalog.Find(id) is not { } device)
            {
                return Results.NotFound();
            }

            // The same refusals the desktop executor applies before any network:
            // a sensor takes no command, a setpoint stays inside the device's range.
            if (device.IsSensor)
            {
                return Json(HomeReply.Failure<bool>(HomeReplyCodes.Rejected, $"{device.DisplayName} só mede; não recebe comando."));
            }

            if (command.Temperature is { } t && (!device.HasSetpoint || t < device.MinSetpoint || t > device.MaxSetpoint))
            {
                return Json(HomeReply.Failure<bool>(HomeReplyCodes.Rejected,
                    $"{device.DisplayName} aceita de {device.MinSetpoint} a {device.MaxSetpoint} °C."));
            }

            return Json(await gateway.SendAsync(device, command, ct).ConfigureAwait(false));
        });

        api.MapPost("/devices/{id}/stream", async (string id, HomeCatalog catalog, IHomeDeviceGateway gateway, CancellationToken ct) =>
            catalog.Find(id) is { } device
                ? Json(await gateway.AllocateStreamAsync(device, ct).ConfigureAwait(false))
                : Results.NotFound());

        api.MapGet("/devices/{id}/frame", async (string id, HomeCatalog catalog, IHomeDeviceGateway gateway, CancellationToken ct) =>
        {
            if (catalog.Find(id) is not { } device)
            {
                return Results.NotFound();
            }

            var frame = await gateway.CaptureFrameAsync(device, ct).ConfigureAwait(false);

            // A frame of the front door is the most private thing the house
            // produces: it goes out once, uncached, and is never written down.
            return frame.Ok && frame.Value is { } jpeg
                ? Results.File(jpeg.Jpeg.ToArray(), "image/jpeg")
                : Json(HomeReply.Failure<byte[]>(frame.Code, frame.Message));
        });

        api.MapGet("/providers", (IHomeDeviceGateway gateway) =>
            Json(HomeReply.Success(Enum.GetValues<HomeProvider>()
                .Select(p => new ProviderStatus(p, gateway.IsConfigured(p)))
                .ToImmutableArray())));

        api.MapGet("/providers/{provider}/discover", async (HomeProvider provider, IHomeDeviceGateway gateway, CancellationToken ct) =>
            Json(await gateway.DiscoverAsync(provider, ct).ConfigureAwait(false)));

        api.MapPut("/providers/{provider}/credentials", (HomeProvider provider, CredentialsBody body, IHomeCredentialStore store) =>
        {
            var credentials = new HomeCredentials(provider, body.AccessId ?? string.Empty, body.Secret ?? string.Empty, body.Region ?? string.Empty);

            if (string.IsNullOrWhiteSpace(credentials.AccessId) && string.IsNullOrWhiteSpace(credentials.Secret))
            {
                return Json(HomeReply.Failure<bool>(HomeReplyCodes.Rejected, "Credencial vazia."));
            }

            store.Save(credentials);
            return Json(HomeReply.Success(true));
        });

        api.MapDelete("/providers/{provider}/credentials", (HomeProvider provider, IHomeCredentialStore store) =>
        {
            store.Clear(provider);
            return Json(HomeReply.Success(true));
        });

        api.MapPost("/consumption", async (ConsumptionQuery query, IConsumptionStore store, CancellationToken ct) =>
        {
            var series = await store.QueryAsync(
                query.DeviceIds.IsDefault ? [] : query.DeviceIds,
                query.Metrics.IsDefault ? [] : query.Metrics,
                query.Since,
                query.Until,
                query.Granularity,
                ct).ConfigureAwait(false);

            return Json(HomeReply.Success(series));
        });
    }

    private static IResult Json<T>(T value) => Results.Json(value, HubWire.Json);
}
