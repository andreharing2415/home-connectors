# home-connectors

Local-first connectors for the devices in a Brazilian house — pool heating, digital
showers, split air conditioners, solar inverters, pool analyzers, robot vacuums,
cameras — behind one small interface, plus **HomeHub**: a service that owns those
connections, keeps their keys in an [OpenBao](https://openbao.org) vault and serves
the house over HTTP to any client (a desktop assistant, a phone app).

> Conectores locais para a casa: aquecimento Tholz, chuveiro Zagonel, ar Daikin,
> inversor GoodWe, analisador Blue Riiot, aspirador Xiaomi/Dreame, câmeras Hikvision
> e tudo que fala Tuya — atrás de uma interface só, com um hub que guarda as chaves
> num cofre OpenBao e serve a casa por HTTP.

## Connectors

| Provider | Talks to | How | Keys |
|---|---|---|---|
| **Tholz** (Smart Connect) | pool/water heating, filter, waterfall, RGB light | LAN TCP 4000, JSON, one kept connection per module | none |
| **Zagonel** (Smart Banho) | digital shower: temperature, flow, power, setpoint | vendor MQTT broker over WebSocket | none (device id) |
| **Daikin** (Brazilian Wi-Fi module) | split AC: power, setpoint, room temperature | LAN 15914, AES-128-CFB | device key from the Daikin Smart AC app |
| **GoodWe** (DT family) | solar inverter, smart meter | LAN Modbus TCP 502 | none |
| **Blue Riiot** (Blue Connect) | pool water: pH, ORP, salt, temperature | vendor cloud, AWS SigV4 | account e-mail/password |
| **Xiaomi / Dreame** (Mijia vacuums) | vacuum control; map read/edit/restore | miIO UDP 54321; Xiaomi cloud (QR login) for the map | device token |
| **Hikvision** | NVR cameras: stream (HLS via ffmpeg), still frame | LAN ISAPI + RTSP | NVR user/password |
| **Tuya** | lights, plugs, curtains, locks, anything Smart Life | Tuya IoT cloud | Access ID/Secret |

Every adapter implements `IHomeDeviceGateway` (`HomeConnectors.Abstractions`):
read state, send a command, allocate a camera stream, capture a frame, discover
devices. Failures are values (`HomeReply<T>`), never exceptions, so a client can
reason about "offline", "unauthorized" and "rejected" the same way for every vendor.

Several of these protocols are undocumented and were worked out from the vendors'
own apps for interoperability with hardware the owner bought. A firmware update
can change them; tests pin the framing each adapter relies on.

## Layout

```
src/HomeConnectors.Abstractions  the model: devices, commands, states, replies, ports
src/HomeConnectors               one adapter per vendor + consumption sampler/store + secret stores
src/HomeHub                      the service: vault, adapters, HTTP API (Windows service or systemd)
src/HomeHub.Client               the wire contract and an IHomeDeviceGateway over HTTP
tests/HomeConnectors.Tests       protocol framing, parsing, wire round-trips
```

## HomeHub

One process talks to the house. That matters: a Tholz module serves one kept
connection at a time, and two programs polling it starve each other.

- **Vault.** On first start the hub launches OpenBao (raft storage, loopback only),
  initializes it with one unseal key, creates a KV v2 mount and a policy limited to
  the house's folder, and mints a periodic token under that policy. The unseal key
  and tokens are kept in `keys.bin`, DPAPI-protected to the machine on Windows
  (0600 elsewhere), in a data folder only SYSTEM/Administrators can read. Every
  later start unseals the vault and renews the token.
- **Catalog.** A JSON file lists the devices (`Devices: [{ Id, Name, Room, Class,
  Provider, Host, Slot, ... }]`). It is the allowlist: the API only ever acts on a
  device id found there, so a client cannot point the hub at an arbitrary host.
- **API.** `http://127.0.0.1:8787/api`, bearer token from `client-token` in the
  data folder.

| Method | Route | |
|---|---|---|
| GET | `/health` | hub up, vault open, device count |
| GET | `/devices` | the catalog |
| GET | `/devices/{id}/state` | live state |
| POST | `/devices/{id}/command` | `{ "power": "On", "temperature": 38 }` |
| POST | `/devices/{id}/stream` | camera stream URL |
| GET | `/devices/{id}/frame` | JPEG |
| GET | `/providers` | which providers have keys (never the keys) |
| GET | `/providers/{p}/discover` | devices the vendor account sees |
| PUT / DELETE | `/providers/{p}/credentials` | write-only key management |
| POST | `/consumption` | water/energy history by day, week or month |

### Install (Windows)

Requires the .NET 10 SDK. From an elevated PowerShell:

```powershell
.\scripts\install-hub.ps1 -CatalogPath C:\path\to\home.json
```

It downloads OpenBao (checked against the release's `checksums.txt`), publishes
the hub to `C:\Program Files\HomeHub`, locks `C:\ProgramData\HomeHub`, registers
the `HomeHub` service (delayed auto start, restart on failure) and grants the
current user read access to the client token. Re-running it upgrades in place
and keeps the vault.

Then give the hub its keys:

```powershell
$t = (Get-Content C:\ProgramData\HomeHub\client-token).Trim()
Invoke-RestMethod -Method Put http://127.0.0.1:8787/api/providers/Tuya/credentials `
  -Headers @{ Authorization = "Bearer $t" } -ContentType application/json `
  -Body '{"accessId":"...","secret":"...","region":"us"}'
```

### Not yet

- Remote access (phone outside the house): bind beyond loopback behind TLS, or
  reach the box over a mesh VPN such as Tailscale.
- Camera streams are served on loopback, for a client on the same machine.
- Vacuum map editing is a library (`XiaomiVacuumMapService`), not yet an API.

## Build and test

```
dotnet test
```

## License

MIT. Not affiliated with any of the vendors named above; their names are used
only to say what the code talks to.
