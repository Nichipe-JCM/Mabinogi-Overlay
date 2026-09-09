using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;

namespace TestOverlay.Update;

public sealed record UpdateRequest(string Target, string ZipPath, string CurrentVersion, string Tag, int ParentPid, long ParentStartTicks, string Language);
public sealed record UpdateMessage(string Kind, string? Text = null, UpdateRequest? Request = null);

public sealed class UpdateChannel : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    public UpdateChannel(PipeStream pipe)
    {
        _reader = new StreamReader(pipe, leaveOpen: true);
        _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    }
    public async Task SendAsync(UpdateMessage message, CancellationToken token) => await _writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
    public async Task<UpdateMessage> ReadAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (await _reader.ReadAsync(character.AsMemory(), token) != 0)
        {
            if (character[0] == '\n') return JsonSerializer.Deserialize<UpdateMessage>(line.ToString()) ?? throw new InvalidDataException("Invalid update message.");
            if (line.Length >= 16384) throw new InvalidDataException("Update message too large.");
            line.Append(character[0]);
        }
        throw new IOException("Update connection closed.");
    }
    public void Dispose() { _reader.Dispose(); _writer.Dispose(); }
    public static void VerifyClient(NamedPipeServerStream pipe, Process process)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) || id != process.Id) throw new IOException("Unexpected updater connection.");
    }
    public static void VerifyServer(NamedPipeClientStream pipe, int processId)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) || id != processId) throw new IOException("Unexpected update host.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint processId);
}
