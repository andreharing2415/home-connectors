using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace HomeHub.Vault;

/// <summary>
/// The hub's vault: an OpenBao server it starts, initializes once and unseals on
/// every start, then reads with a token scoped to the house's secrets.
/// <para>
/// First run: one unseal key (a single box has no second operator to hold a
/// share), a KV v2 mount, a policy limited to that mount's house folder, and a
/// periodic token under that policy. The unseal key, the token and the root
/// token are kept in <see cref="MachineSecretFile"/>; the root token stays only
/// so the hub can mint a new house token if the old one lapsed while the box
/// was off longer than its period.
/// </para>
/// <para>
/// A vault that is already answering on the address is used as it is — the bao
/// left running by a hub that crashed, or one the owner runs as a service of
/// its own.
/// </para>
/// </summary>
public sealed class OpenBaoVault : IDisposable
{
    private const string Policy = "homehub";
    private const string TokenPeriod = "720h";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HubOptions _options;
    private readonly ILogger<OpenBaoVault> _log;
    private readonly MachineSecretFile _keys;
    private readonly HttpClient _http;
    private Process? _process;
    private VaultKeys? _current;

    public OpenBaoVault(IOptions<HubOptions> options, ILogger<OpenBaoVault> log)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _keys = new MachineSecretFile(Path.Combine(_options.DataDirectory, "openbao", "keys.bin"));
        _http = new HttpClient { BaseAddress = _options.OpenBao.Address, Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>The house token, or null while the vault is not open.</summary>
    public string? Token => _current?.HubToken;

    public bool IsOpen => _current is not null;

    /// <summary>Starts the vault if it is the hub's to run, initializes it on first run, unseals it and checks the token.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_options.OpenBao.Manage && !await AnswersAsync(cancellationToken).ConfigureAwait(false))
        {
            Start();
        }

        var health = await WaitForHealthAsync(active: false, cancellationToken).ConfigureAwait(false);

        var keys = health == HttpStatusCode.NotImplemented
            ? await InitializeAsync(cancellationToken).ConfigureAwait(false)
            : ReadKeys();

        if (health is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotImplemented)
        {
            await PutAsync("v1/sys/unseal", new { key = keys.UnsealKey }, null, cancellationToken).ConfigureAwait(false);
            HubLog.VaultUnsealed(_log);
        }

        // Raft elects its single node a moment after the unseal; writes before
        // that land on read-only storage.
        await WaitForHealthAsync(active: true, cancellationToken).ConfigureAwait(false);
        await EnsureLayoutAsync(keys, cancellationToken).ConfigureAwait(false);
        keys = await EnsureHubTokenAsync(keys, cancellationToken).ConfigureAwait(false);
        _current = keys;
        HubLog.VaultReady(_log, _options.OpenBao.Address);
    }

    /// <summary>Extends the house token's period. Called on start and twice a day.</summary>
    public async Task RenewAsync(CancellationToken cancellationToken)
    {
        if (_current is not { } keys)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/token/renew-self");
        request.Headers.Add("X-Vault-Token", keys.HubToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            HubLog.RenewRefused(_log, (int)response.StatusCode);
            _current = await EnsureHubTokenAsync(keys with { HubToken = string.Empty }, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _http.Dispose();

        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process?.Dispose();
    }

    private void Start()
    {
        var home = Path.Combine(_options.DataDirectory, "openbao");
        Directory.CreateDirectory(Path.Combine(home, "data"));

        var config = Path.Combine(home, "config.hcl");
        var address = _options.OpenBao.Address;
        File.WriteAllText(config, string.Create(CultureInfo.InvariantCulture, $$"""
            storage "raft" {
              path    = "{{Path.Combine(home, "data").Replace('\\', '/')}}"
              node_id = "homehub"
            }

            listener "tcp" {
              address         = "{{address.Host}}:{{address.Port}}"
              cluster_address = "{{address.Host}}:{{address.Port + 1}}"
              tls_disable     = true
            }

            api_addr     = "{{address.GetLeftPart(UriPartial.Authority)}}"
            cluster_addr = "http://{{address.Host}}:{{address.Port + 1}}"
            ui           = false
            """));

        if (!File.Exists(_options.OpenBao.Executable))
        {
            throw new FileNotFoundException("OpenBao não encontrado. Rode scripts/install-hub.ps1.", _options.OpenBao.Executable);
        }

        var log = File.AppendText(Path.Combine(home, "openbao.log"));
        log.AutoFlush = true;

        var start = new ProcessStartInfo(_options.OpenBao.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("server");
        start.ArgumentList.Add("-config=" + config);

        _process = Process.Start(start) ?? throw new InvalidOperationException("O OpenBao não subiu.");
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) { log.WriteLine(e.Data); } } };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        HubLog.VaultStarted(_log, _process.Id);
    }

    private async Task<bool> AnswersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri("v1/sys/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// The vault's own health codes: 200 active, 429 standby (raft still
    /// electing), 503 sealed, 501 never initialized. <paramref name="active"/>
    /// waits for 200; otherwise any answer that says what state it is in will do.
    /// </summary>
    private async Task<HttpStatusCode> WaitForHealthAsync(bool active, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (true)
        {
            try
            {
                using var response = await _http.GetAsync(new Uri("v1/sys/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                var code = response.StatusCode;

                if (code == HttpStatusCode.OK
                    || (!active && code is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotImplemented))
                {
                    return code;
                }
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < deadline)
            {
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException("O cofre não ficou pronto em 30 s. Veja openbao.log na pasta de dados do hub.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<VaultKeys> InitializeAsync(CancellationToken cancellationToken)
    {
        if (_keys.Exists)
        {
            // A keys file beside an empty vault means the data folder was lost
            // or swapped. Initializing over it would orphan every secret the
            // old keys still open, so this stops and says so.
            throw new InvalidOperationException("O cofre está vazio mas já existe keys.bin. Restaure a pasta openbao/data ou apague keys.bin conscientemente.");
        }

        var init = await PutAsync("v1/sys/init", new { secret_shares = 1, secret_threshold = 1 }, null, cancellationToken).ConfigureAwait(false);
        var unseal = init["keys_base64"]![0]!.GetValue<string>();
        var root = init["root_token"]!.GetValue<string>();

        // Kept before anything else can fail: losing the unseal key between
        // init and the first save would lock the vault for good.
        var keys = new VaultKeys(unseal, root, string.Empty);
        _keys.Write(JsonSerializer.Serialize(keys, Json));
        return keys;
    }

    /// <summary>
    /// The KV mount and the house policy. Checked on every start rather than
    /// only after init, so a first run cut short between init and here heals
    /// on the next.
    /// </summary>
    private async Task EnsureLayoutAsync(VaultKeys keys, CancellationToken cancellationToken)
    {
        var mount = _options.OpenBao.Mount;
        var prefix = _options.OpenBao.Prefix;

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"v1/sys/mounts/{mount}"))
        {
            request.Headers.Add("X-Vault-Token", keys.RootToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await PostAsync($"v1/sys/mounts/{mount}", new { type = "kv", options = new { version = "2" } }, keys.RootToken, cancellationToken).ConfigureAwait(false);
                HubLog.VaultInitialized(_log, mount, Policy);
            }
        }

        var policy = $$"""
            path "{{mount}}/data/{{prefix}}/*"     { capabilities = ["create", "read", "update", "delete"] }
            path "{{mount}}/metadata/{{prefix}}/*" { capabilities = ["read", "list", "delete"] }
            """;
        await PutAsync($"v1/sys/policies/acl/{Policy}", new { policy }, keys.RootToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VaultKeys> EnsureHubTokenAsync(VaultKeys keys, CancellationToken cancellationToken)
    {
        if (keys.HubToken.Length > 0 && await TokenWorksAsync(keys.HubToken, cancellationToken).ConfigureAwait(false))
        {
            return keys;
        }

        var created = await PostAsync("v1/auth/token/create", new
        {
            policies = new[] { Policy },
            period = TokenPeriod,
            no_parent = true,
            display_name = "homehub",
        }, keys.RootToken, cancellationToken).ConfigureAwait(false);

        var fresh = keys with { HubToken = created["auth"]!["client_token"]!.GetValue<string>() };
        _keys.Write(JsonSerializer.Serialize(fresh, Json));
        HubLog.TokenIssued(_log);
        return fresh;
    }

    private async Task<bool> TokenWorksAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/auth/token/lookup-self");
        request.Headers.Add("X-Vault-Token", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private VaultKeys ReadKeys()
    {
        var text = _keys.Read() ?? throw new InvalidOperationException(
            "O cofre já foi inicializado mas keys.bin não existe ou não abre nesta máquina.");
        return JsonSerializer.Deserialize<VaultKeys>(text, Json) ?? throw new InvalidOperationException("keys.bin ilegível.");
    }

    private Task<JsonNode> PutAsync(string path, object body, string? token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, path, body, token, cancellationToken);

    private Task<JsonNode> PostAsync(string path, object body, string? token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, body, token, cancellationToken);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType()) };

        if (token is not null)
        {
            request.Headers.Add("X-Vault-Token", token);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The vault's error list names the problem, never a secret.
            throw new InvalidOperationException($"Cofre recusou {method} {path}: HTTP {(int)response.StatusCode} {text}");
        }

        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private sealed record VaultKeys(string UnsealKey, string RootToken, string HubToken);
}

/// <summary>Keeps the house token alive: renews it now and every twelve hours.</summary>
public sealed class VaultTokenRenewer(OpenBaoVault vault, ILogger<VaultTokenRenewer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));

        do
        {
            try
            {
                await vault.RenewAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
            {
                HubLog.RenewFailed(log, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
