using System.Text.Json;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Communication.Messages;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="NamedPipeServer.JsonOptions"/> serialization behavior and the
/// taskId-extraction logic that runs inside HandleClientAsync after a SubmitTask response.
///
/// These tests document and validate the exact JSON contract used on the wire so that
/// changes to JsonOptions do not silently break task-owner routing.
/// </summary>
public class NamedPipeServerJsonTests
{
    // Expose the internal options through the public property
    private static JsonSerializerOptions Options => NamedPipeServer.JsonOptions;

    // ── PipeMessage round-trip ────────────────────────────────────────────────

    [Fact]
    public void PipeMessage_SerializeDeserialize_RoundTrips()
    {
        var original = new PipeMessage
        {
            Type      = MessageTypes.TaskUpdate,
            RequestId = "req-42",
            Payload   = [0x01, 0x02, 0x03],
        };

        var json        = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<PipeMessage>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Type,      roundTripped.Type);
        Assert.Equal(original.RequestId, roundTripped.RequestId);
        Assert.Equal(original.Payload,   roundTripped.Payload);
    }

    [Fact]
    public void PipeMessage_SerializesToCamelCase()
    {
        var msg  = new PipeMessage { Type = "ping", RequestId = "r1" };
        var json = JsonSerializer.Serialize(msg, Options);

        // Property names must be camelCase to match the TypeScript client
        Assert.Contains("\"type\"",      json);
        Assert.Contains("\"requestId\"", json);
    }

    [Fact]
    public void PipeMessage_DeserializesCaseInsensitive()
    {
        // TypeScript may send PascalCase or mixed-case; options must handle it
        const string json = """{"Type":"ping","RequestId":"r99"}""";
        var msg = JsonSerializer.Deserialize<PipeMessage>(json, Options);

        Assert.NotNull(msg);
        Assert.Equal("ping", msg.Type);
        Assert.Equal("r99",  msg.RequestId);
    }

    // ── taskId extraction from SubmitTask response payload ───────────────────
    //
    // After a SubmitTask succeeds the server extracts the taskId from the JSON
    // payload to register task-owner routing.  These tests cover the logic in
    // HandleClientAsync's inner try/catch that now logs on parse failures.

    [Fact]
    public void TaskIdExtraction_ValidPayload_ExtractsTaskId()
    {
        // Simulate the payload that a SubmitTask response carries
        var payloadJson = """{"taskId":"abc-123","status":"queued"}""";
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadJson);

        using var doc = JsonDocument.Parse(payloadBytes);
        var found = doc.RootElement.TryGetProperty("taskId", out var el);

        Assert.True(found);
        Assert.Equal("abc-123", el.GetString());
    }

    [Fact]
    public void TaskIdExtraction_MissingTaskId_PropertyNotFound()
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes("""{"status":"queued"}""");

        using var doc = JsonDocument.Parse(payloadBytes);
        var found = doc.RootElement.TryGetProperty("taskId", out _);

        Assert.False(found);
    }

    [Fact]
    public void TaskIdExtraction_EmptyTaskId_TreatedAsAbsent()
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes("""{"taskId":""}""");

        using var doc = JsonDocument.Parse(payloadBytes);
        doc.RootElement.TryGetProperty("taskId", out var el);

        // Matches the guard: !string.IsNullOrEmpty(taskId)
        Assert.True(string.IsNullOrEmpty(el.GetString()));
    }

    [Fact]
    public void TaskIdExtraction_MalformedJson_ThrowsJsonException()
    {
        // Validates that the catch block in HandleClientAsync will actually fire
        // for malformed payloads (and now logs instead of silently swallowing)
        var malformed = System.Text.Encoding.UTF8.GetBytes("<<< not json >>>");

        // JsonReaderException (subclass of JsonException) is thrown for malformed JSON
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(malformed));
    }

    [Fact]
    public void TaskIdExtraction_NullPayload_NullTaskId()
    {
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes("""{"taskId":null}""");

        using var doc = JsonDocument.Parse(payloadBytes);
        doc.RootElement.TryGetProperty("taskId", out var el);

        Assert.True(string.IsNullOrEmpty(el.GetString()));
    }

    // ── Enum serialization (string enums in JSON options) ─────────────────────

    [Fact]
    public void JsonOptions_ByteArraySerializesAsBase64()
    {
        // byte[] Payload should serialize as base64 (default STJ behavior)
        var msg  = new PipeMessage { Payload = [72, 101, 108, 108, 111] }; // "Hello"
        var json = JsonSerializer.Serialize(msg, Options);

        // base64("Hello") = "SGVsbG8="
        Assert.Contains("SGVsbG8=", json);
    }
}
