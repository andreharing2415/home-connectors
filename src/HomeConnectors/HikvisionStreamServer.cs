using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using HomeConnectors;

namespace HomeConnectors.Adapters;

/// <summary>
/// Runs one ffmpeg per Hikvision camera being watched — transcoding its RTSP
/// sub-stream to HLS segments on disk — and serves those segments over a
/// loopback-only HTTP listener that the console's WebView2 can play with the
/// same hls.js it already uses for Tuya's cloud streams.
/// <para>
/// Nothing here leaves the machine: the listener binds to 127.0.0.1 only, and
/// the NVR's username and password are burned into the RTSP URL passed to
/// ffmpeg as one argument — never through a shell, so nothing in a password
/// can inject an extra ffmpeg flag.
/// </para>
/// </summary>
public sealed class HikvisionStreamServer : IDisposable
{
    private const int MaxWaitAttempts = 20;

    private static readonly TimeSpan WaitInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly string _ffmpegPath;
    private readonly string _rootDirectory;
    private readonly TimeProvider _clock;
    private readonly HttpListener _listener;
    private readonly Uri _baseUri;

    /// <summary>Where the playlists are served from; loopback, random port.</summary>
    public Uri BaseUri => _baseUri;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public HikvisionStreamServer(string rootDirectory, TimeProvider clock, string? ffmpegPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _rootDirectory = rootDirectory;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? ResolveFfmpeg() : ffmpegPath;

        Directory.CreateDirectory(_rootDirectory);

        var port = FindFreeLoopbackPort();
        _baseUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));

        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUri.ToString());
        _listener.Start();

        _ = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Starts (or reuses) the transcode for one device and waits for its
    /// playlist to exist before answering. The wait is what keeps the verifier
    /// honest: without it, the probe that follows would fetch a URL nothing is
    /// serving yet and call that a failure.
    /// </summary>
    private static string ResolveFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(bundled)) { return bundled; }
        var userLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(userLink) ? userLink : "ffmpeg";
    }

    public async Task<HomeReply<CameraFeed>> AllocateAsync(
        HomeDevice device,
        string host,
        int rtspPort,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!IsValidChannel(device.Id))
        {
            return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "O id do canal tem que ser só dígitos.");
        }

        Session session;

        lock (_gate)
        {
            ReapIdleSessions();

            if (!_sessions.TryGetValue(device.Id, out var existing) || existing.Process.HasExited)
            {
                existing?.Dispose();
                _sessions.Remove(device.Id);

                var directory = Path.Combine(_rootDirectory, device.Id);
                Directory.CreateDirectory(directory);
                Clean(directory);

                var rtspUrl = string.Create(CultureInfo.InvariantCulture,
                    $"rtsp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{host}:{rtspPort.ToString(CultureInfo.InvariantCulture)}/Streaming/Channels/{device.Id}02");

                try
                {
                    existing = Session.Start(_ffmpegPath, rtspUrl, directory);
                }
                catch (Win32Exception failure)
                {
                    return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream,
                        failure.NativeErrorCode is 2 or 3
                            ? "Não foi possível abrir a câmera: FFmpeg não encontrado. Instale o FFmpeg e tente novamente."
                            : "Não foi possível iniciar o vídeo da câmera. Verifique a instalação do FFmpeg.");
                }
                catch (InvalidOperationException)
                {
                    return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream,
                        "Não foi possível iniciar o vídeo da câmera. Tente novamente.");
                }
                _sessions[device.Id] = existing;
            }

            existing.LastAccess = _clock.GetUtcNow();
            session = existing;
        }

        var playlist = Path.Combine(session.Directory, "index.m3u8");

        for (var attempt = 0; attempt < MaxWaitAttempts; attempt++)
        {
            if (session.Process.HasExited)
            {
                return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream,
                    "O ffmpeg encerrou antes de transmitir. Confira o endereço, o usuário e a senha do NVR.");
            }

            if (File.Exists(playlist) && new FileInfo(playlist).Length > 0)
            {
                var url = new Uri(_baseUri, string.Create(CultureInfo.InvariantCulture, $"{device.Id}/index.m3u8"));
                return HomeReply.Success(new CameraFeed(url, "hls", _clock.GetUtcNow().AddMinutes(5)));
            }

            await Task.Delay(WaitInterval, _clock, cancellationToken).ConfigureAwait(false);
        }

        return HomeReply.Failure<CameraFeed>(HomeReplyCodes.NoStream, "A câmera não respondeu a tempo.");
    }

    /// <summary>Only digits: the channel becomes part of an RTSP path and a filesystem path, never anything else.</summary>
    private static bool IsValidChannel(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    /// <summary>Idle sessions are reaped lazily, the next time any camera is asked for — there is no background timer.</summary>
    private void ReapIdleSessions()
    {
        var now = _clock.GetUtcNow();
        var idle = _sessions.Where(kv => now - kv.Value.LastAccess > IdleTimeout).Select(kv => kv.Key).ToList();

        foreach (var key in idle)
        {
            _sessions[key].Dispose();
            _sessions.Remove(key);
        }
    }

    private static void Clean(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int FindFreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = ServeAsync(context);
        }
    }

    /// <summary>
    /// Exactly two shapes are served: <c>{deviceId}/index.m3u8</c> and
    /// <c>{deviceId}/segNNN.ts</c>. Anything else — including a path carrying
    /// <c>..</c> — is a 404 before the filesystem is ever touched.
    /// </summary>
    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length != 2
                || segments.Any(s => s.Contains("..", StringComparison.Ordinal))
                || (!segments[1].EndsWith(".m3u8", StringComparison.Ordinal) && !segments[1].EndsWith(".ts", StringComparison.Ordinal)))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var filePath = Path.Combine(_rootDirectory, segments[0], segments[1]);

            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = segments[1].EndsWith(".m3u8", StringComparison.Ordinal)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t";
            context.Response.Headers.Add("Cache-Control", "no-cache");
            // The console lives at https://jarvis.local and this listener at
            // http://127.0.0.1 — a different origin. hls.js fetches with XHR,
            // so without this header the browser drops every reply and the
            // player reports a fatal network error. The listener is loopback-only
            // and serves nothing but public-to-this-machine segments.
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            // ffmpeg may still hold the segment open for writing; read-share it
            // rather than fail the request while a rotation is in flight.
            await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            context.Response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
            context.Response.OutputStream.Close();
        }
        catch (IOException)
        {
        }
        catch (HttpListenerException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }

        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _shutdown.Dispose();
    }

    private sealed class Session : IDisposable
    {
        public required Process Process { get; init; }

        public required string Directory { get; init; }

        public DateTimeOffset LastAccess { get; set; }

        public static Session Start(string ffmpegPath, string rtspUrl, string directory)
        {
            var playlist = Path.Combine(directory, "index.m3u8");
            var segments = Path.Combine(directory, "seg%03d.ts");

            var info = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Built as a list, not a formatted string: .NET escapes each entry
            // for the process it starts, so nothing in the RTSP URL — including
            // a password with quotes or spaces in it — can smuggle in another
            // ffmpeg flag.
            info.ArgumentList.Add("-rtsp_transport");
            info.ArgumentList.Add("tcp");
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(rtspUrl);
            info.ArgumentList.Add("-an");
            // Hikvision sub-streams are HEVC; Chromium's MSE — hence hls.js in
            // WebView2 — cannot play that reliably, so re-encode to H.264. The
            // sub-stream is 640x360: cheap even on software x264.
            info.ArgumentList.Add("-c:v");
            info.ArgumentList.Add("libx264");
            info.ArgumentList.Add("-preset");
            info.ArgumentList.Add("veryfast");
            info.ArgumentList.Add("-tune");
            info.ArgumentList.Add("zerolatency");
            info.ArgumentList.Add("-profile:v");
            info.ArgumentList.Add("main");
            info.ArgumentList.Add("-pix_fmt");
            info.ArgumentList.Add("yuv420p");
            info.ArgumentList.Add("-g");
            info.ArgumentList.Add("30");
            info.ArgumentList.Add("-sc_threshold");
            info.ArgumentList.Add("0");
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add("hls");
            info.ArgumentList.Add("-hls_time");
            info.ArgumentList.Add("2");
            info.ArgumentList.Add("-hls_list_size");
            info.ArgumentList.Add("4");
            info.ArgumentList.Add("-hls_flags");
            info.ArgumentList.Add("delete_segments+append_list");
            info.ArgumentList.Add("-hls_segment_filename");
            info.ArgumentList.Add(segments);
            info.ArgumentList.Add(playlist);

            var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg não iniciou.");

            // Nothing reads these, but leaving them unredirected under
            // CreateNoWindow risks ffmpeg blocking on a full pipe buffer once
            // it starts logging; draining them keeps that from ever happening.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return new Session { Process = process, Directory = directory, LastAccess = DateTimeOffset.MinValue };
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            Process.Dispose();
        }
    }
}
