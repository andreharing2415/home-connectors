using System.ComponentModel.DataAnnotations;
using HomeConnectors;
using Microsoft.Extensions.Options;

namespace HomeHub;

/// <summary>
/// One device as the owner writes it down. Bound from the catalog file and turned
/// into a <see cref="HomeDevice"/>; the keys are not here — they live in the vault.
/// </summary>
public sealed class HomeDeviceOptions
{
    /// <summary>The vendor's device id, exactly as the vendor shows it.</summary>
    [Required]
    [MinLength(1)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    public string Name { get; set; } = string.Empty;

    public string Room { get; set; } = string.Empty;

    public HomeDeviceClass Class { get; set; } = HomeDeviceClass.Switch;

    public HomeProvider Provider { get; set; } = HomeProvider.Tuya;

    /// <summary>Other names for it. Carried for clients that resolve speech; the hub itself only routes by id.</summary>
    public IList<string> Aliases { get; } = [];

    /// <summary>The on/off data point. Tuya switches use <c>switch_1</c>; bulbs <c>switch_led</c>; some ACs <c>switch</c>.</summary>
    public string PowerCode { get; set; } = "switch_1";

    /// <summary>The setpoint data point, for cloud climate devices. Usually <c>temp_set</c>.</summary>
    public string? TemperatureCode { get; set; }

    /// <summary>The module's address on the LAN, for a device spoken to locally.</summary>
    public string? Host { get; set; }

    /// <summary>The slot inside a Tholz module — <c>heat0</c>, <c>out1</c>.</summary>
    public string? Slot { get; set; }

    public int? MinTemperature { get; set; }

    public int? MaxTemperature { get; set; }

    /// <summary>Nameplate power in watts, for estimating kWh from run time on devices that do not meter themselves.</summary>
    public int? RatedWatts { get; set; }

    public HomeDevice ToDevice() => new(
        Id.Trim(),
        Name.Trim(),
        Room.Trim(),
        Class,
        Provider,
        [.. Aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())],
        string.IsNullOrWhiteSpace(PowerCode) ? "switch_1" : PowerCode.Trim(),
        string.IsNullOrWhiteSpace(TemperatureCode) ? null : TemperatureCode.Trim(),
        string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
        string.IsNullOrWhiteSpace(Slot) ? null : Slot.Trim(),
        MinTemperature,
        MaxTemperature,
        RatedWatts);
}

/// <summary>The house as configuration. The list is the allowlist: nothing a cloud reports beyond it is reachable.</summary>
public sealed class CatalogOptions
{
    public IList<HomeDeviceOptions> Devices { get; } = [];

    public HomeCatalog ToCatalog() => new([.. Devices.Select(d => d.ToDevice())]);
}

/// <summary>The checks on the device list that annotations cannot express.</summary>
public sealed class CatalogOptionsValidator : IValidateOptions<CatalogOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in options.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.Name))
            {
                failures.Add("Todo dispositivo da casa precisa de Id e Name.");
                continue;
            }

            if (!seen.Add(device.Id.Trim()))
            {
                failures.Add($"O dispositivo {device.Id} aparece duas vezes na lista da casa.");
            }

            if (device.Class == HomeDeviceClass.Climate
                && device.Provider is not (HomeProvider.Tholz or HomeProvider.Daikin or HomeProvider.Zagonel)
                && string.IsNullOrWhiteSpace(device.TemperatureCode))
            {
                failures.Add($"{device.Name} é Climate e precisa de TemperatureCode (normalmente temp_set).");
            }

            if (device.Provider == HomeProvider.Tholz
                && (string.IsNullOrWhiteSpace(device.Host) || string.IsNullOrWhiteSpace(device.Slot)))
            {
                failures.Add($"{device.Name} é Tholz e precisa de Host (IP do módulo) e Slot (ex. heat0).");
            }

            if (device.Provider == HomeProvider.GoodWe && string.IsNullOrWhiteSpace(device.Host))
            {
                failures.Add($"{device.Name} é GoodWe e precisa de Host (IP do dongle).");
            }

            if (device.MinTemperature is { } min && device.MaxTemperature is { } max && min > max)
            {
                failures.Add($"{device.Name} tem MinTemperature maior que MaxTemperature.");
            }

            if (!Enum.IsDefined(device.Class) || !Enum.IsDefined(device.Provider))
            {
                failures.Add($"{device.Name} tem classe ou provedor desconhecido.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
