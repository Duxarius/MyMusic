using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MyMusic.Services;

/// <summary>
/// Small, local-only HTTP byte-range server used by Google Cast receivers to read a selected library file.
/// It only serves the currently selected file and uses an unguessable per-session path token.
/// </summary>
public sealed class CastMediaServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private string? _filePath;
    private string? _artworkPath;
    private string? _token;
    private Task? _acceptLoop;
    private int _port;

    public string? ArtworkUrl => !string.IsNullOrWhiteSpace(_artworkPath) && !string.IsNullOrWhiteSpace(_token) && _port > 0
        ? $"http://{GetBoundAddress()}:{_port}/artwork/{_token}" : null;

    private string? _boundAddress;

    private string? GetBoundAddress() => _boundAddress;

    public string Start(string filePath, string? artworkPath = null)
    {
        Stop();
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected music file no longer exists.", filePath);
        _filePath = Path.GetFullPath(filePath);
        _artworkPath = !string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath) ? Path.GetFullPath(artworkPath) : null;
        _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var ip = GetLanAddress();
        _boundAddress = ip;
        _listener = new TcpListener(IPAddress.Parse(ip), 0);
        _listener.Start(8);
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _stop = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_listener, _stop.Token);
        return $"http://{ip}:{_port}/stream/{_token}";
    }

    private static string GetLanAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => new { a.Address, HasGateway = n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork) }))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address) && !x.Address.ToString().StartsWith("169.254."))
            .OrderByDescending(x => x.HasGateway)
            .Select(x => x.Address.ToString()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(addresses)) throw new InvalidOperationException("No active IPv4 LAN address was found. Check that this PC is connected to your home network.");
        return addresses;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(requestLine)) return;
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? range = null;
                string? requestPath = parts.Length > 1 ? parts[1] : null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();

                if (parts.Length >= 2 && (parts[0] == "GET" || parts[0] == "HEAD") && requestPath == $"/artwork/{_token}" && !string.IsNullOrWhiteSpace(_artworkPath) && File.Exists(_artworkPath))
                {
                    var artworkPath = _artworkPath;
                    var imageBytes = await File.ReadAllBytesAsync(artworkPath, token);
                    await WriteHeaderAsync(stream, "200 OK", GetImageMimeType(artworkPath), imageBytes.LongLength, null, token);
                    if (parts[0] == "GET") await stream.WriteAsync(imageBytes, token);
                    return;
                }

                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD") || requestPath != $"/stream/{_token}" || string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
                {
                    await WriteHeaderAsync(stream, "404 Not Found", "text/plain", 0, null, token);
                    return;
                }

                var path = _filePath;
                var info = new FileInfo(path);
                long start = 0, end = info.Length - 1;
                var partial = false;
                if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = range[6..].Split(',')[0].Trim().Split('-', 2);
                    if (spec.Length == 2 && long.TryParse(spec[0], out var parsedStart) && parsedStart >= 0 && parsedStart < info.Length)
                    {
                        start = parsedStart;
                        if (long.TryParse(spec[1], out var parsedEnd)) end = Math.Clamp(parsedEnd, start, info.Length - 1);
                        partial = true;
                    }
                    else if (spec.Length == 2 && string.IsNullOrEmpty(spec[0]) && long.TryParse(spec[1], out var suffix) && suffix > 0)
                    {
                        start = Math.Max(0, info.Length - suffix); partial = true;
                    }
                }
                var length = Math.Max(0, end - start + 1);
                var type = GetMimeType(path);
                var status = partial ? "206 Partial Content" : "200 OK";
                await WriteHeaderAsync(stream, status, type, length, partial ? $"bytes {start}-{end}/{info.Length}" : null, token, info.Length);
                if (parts[0] == "HEAD" || length == 0) return;
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[128 * 1024];
                var remaining = length;
                while (remaining > 0 && !token.IsCancellationRequested)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    if (read <= 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, read), token);
                    remaining -= read;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task WriteHeaderAsync(NetworkStream stream, string status, string mime, long length, string? contentRange, CancellationToken token, long? totalLength = null)
    {
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        header.Append("Content-Type: ").Append(mime).Append("\r\n");
        header.Append("Content-Length: ").Append(length).Append("\r\n");
        header.Append("Accept-Ranges: bytes\r\n");
        header.Append("Connection: close\r\n");
        header.Append("Cache-Control: no-store\r\n");
        if (contentRange is not null) header.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        if (status == "416 Range Not Satisfiable") header.Append("Content-Range: bytes */").Append(totalLength ?? 0).Append("\r\n");
        header.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(bytes, token);
    }

    private static string GetImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp",
        ".gif" => "image/gif", ".bmp" => "image/bmp", ".avif" => "image/avif", _ => "application/octet-stream"
    };

    public static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".mp4" => "audio/mp4", ".aac" => "audio/aac",
        ".wav" => "audio/wav", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma", ".aiff" or ".aif" => "audio/aiff", _ => "application/octet-stream"
    };

    public void Stop()
    {
        try { _stop?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _stop?.Dispose(); _stop = null;
        _filePath = null; _artworkPath = null; _token = null; _boundAddress = null; _port = 0; _acceptLoop = null;
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
