using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SAGIDE.Service.Communication.Messages;

namespace SAGIDE.Service.Communication;

public class NamedPipeServer
{
    private readonly string _pipeName;
    private readonly ILogger<NamedPipeServer> _logger;
    private readonly MessageHandler _messageHandler;
    // Per-client write lock prevents concurrent writes from HandleClientAsync and BroadcastAsync
    private record ClientEntry(NamedPipeServerStream Stream, SemaphoreSlim WriteLock);
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private CancellationTokenSource? _cts;

    // Matches TypeScript client: camelCase properties, string enums, byte[] as base64 string
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public NamedPipeServer(
        string pipeName,
        MessageHandler messageHandler,
        ILogger<NamedPipeServer> logger)
    {
        _pipeName = pipeName;
        _messageHandler = messageHandler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("Named pipe server starting on: {PipeName}", _pipeName);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var entry = new ClientEntry(server, new SemaphoreSlim(1, 1));
                _clients[clientId] = entry;
                _logger.LogInformation("Client {ClientId} connected", clientId);

                _ = HandleClientAsync(clientId, entry, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting client connection");
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    // Reads exactly 'count' bytes into buffer[0..count-1]; returns false on EOF.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
            if (read == 0) return false;
            totalRead += read;
        }
        return true;
    }

    private async Task HandleClientAsync(string clientId, ClientEntry entry, CancellationToken ct)
    {
        var stream = entry.Stream;
        try
        {
            var lengthBuffer = new byte[4];
            while (stream.IsConnected && !ct.IsCancellationRequested)
            {
                // Read the 4-byte little-endian length prefix fully before interpreting it.
                if (!await ReadExactAsync(stream, lengthBuffer, 4, ct)) break;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                // Guard against malformed frames: a negative, zero, or unreasonably large length
                // would cause an out-of-memory allocation.  10 MB is a generous upper bound for
                // a single IPC message; adjust if larger payloads are ever needed.
                const int MaxMessageBytes = 10 * 1024 * 1024;
                if (messageLength <= 0 || messageLength > MaxMessageBytes)
                {
                    _logger.LogWarning(
                        "Client {ClientId} sent invalid frame length {Len}; closing connection",
                        clientId, messageLength);
                    break;
                }
                var messageBuffer = new byte[messageLength];
                if (!await ReadExactAsync(stream, messageBuffer, messageLength, ct)) break;

                var message = JsonSerializer.Deserialize<PipeMessage>(messageBuffer, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize PipeMessage");
                _logger.LogDebug("Received message type: {Type} from {ClientId}", message.Type, clientId);

                var response = await _messageHandler.HandleAsync(message, ct);
                await SendWithLockAsync(entry, response, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            await stream.DisposeAsync();
            _logger.LogInformation("Client {ClientId} disconnected", clientId);
        }
    }

    public async Task BroadcastAsync(PipeMessage message, CancellationToken ct = default)
    {
        var tasks = _clients.Values
            .Where(e => e.Stream.IsConnected)
            .Select(e => SendWithLockAsync(e, message, ct));
        await Task.WhenAll(tasks);
    }

    // All writes go through here so the per-client SemaphoreSlim prevents interleaved frames.
    private static async Task SendWithLockAsync(ClientEntry entry, PipeMessage message, CancellationToken ct)
    {
        await entry.WriteLock.WaitAsync(ct);
        try
        {
            await SendMessageAsync(entry.Stream, message, ct);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    private static async Task SendMessageAsync(Stream stream, PipeMessage message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var length = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        foreach (var entry in _clients.Values)
        {
            await entry.Stream.DisposeAsync();
        }
        _clients.Clear();
        _logger.LogInformation("Named pipe server stopped");
    }
}
