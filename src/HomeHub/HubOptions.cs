namespace HomeHub;

/// <summary>The hub's own settings, bound from the <c>Hub</c> section.</summary>
public sealed class HubOptions
{
    public const string SectionName = "Hub";

    /// <summary>Where the hub keeps its state: vault data, keys file, client token, consumption database, camera transcodes.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary>The JSON file that lists the house's devices.</summary>
    public string CatalogPath { get; set; } = Path.Combine(DefaultDataDirectory(), "home.json");

    /// <summary>The section inside <see cref="CatalogPath"/> that holds <c>Devices</c>. Colon-separated, e.g. <c>Jarvis:Home</c>.</summary>
    public string CatalogSection { get; set; } = "Home";

    /// <summary>The ffmpeg binary the camera transcodes run. Empty finds it beside the hub or on PATH.</summary>
    public string? FfmpegPath { get; set; }

    public OpenBaoOptions OpenBao { get; set; } = new();

    private static string DefaultDataDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeHub")
        : "/var/lib/homehub";
}

/// <summary>Where the vault is, and whether the hub runs it.</summary>
public sealed class OpenBaoOptions
{
    /// <summary>The vault's API root. Loopback by default: nothing else on the network should reach it.</summary>
    public Uri Address { get; set; } = new("http://127.0.0.1:8200/");

    /// <summary>
    /// True: the hub starts <see cref="Executable"/> as a child, initializes it
    /// on first run and unseals it on every start. False: the vault is someone
    /// else's to run, and the hub only reads its token from the keys file.
    /// </summary>
    public bool Manage { get; set; } = true;

    /// <summary>The <c>bao</c> binary.</summary>
    public string Executable { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "openbao", "bao.exe")
        : "/usr/bin/bao";

    /// <summary>The KV v2 mount the hub keeps its secrets in.</summary>
    public string Mount { get; set; } = "home";

    /// <summary>The folder inside the mount, so one vault can hold more than one house.</summary>
    public string Prefix { get; set; } = "house";
}
