using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ClashEngine.Core.Stats;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Default <see cref="IMatchUploader"/> implementation: serializes the envelope as JSON and
/// writes it to a file under <see cref="OutputDirectory"/>. A separate uploader process (or a
/// future HTTP-direct implementation) can drain the directory.
/// </summary>
/// <remarks>
/// <para>Filename: <c>match_{MatchId:N}_{EndedAt:yyyyMMddHHmmss}.json</c>.</para>
///
/// <para>The actual disk work runs on a thread-pool task -- <see cref="Upload"/> returns to the
/// caller as soon as the payload has been queued, so a slow disk can never stall the
/// SubspaceServer mainloop. Failures are caught + logged, never thrown -- match persistence
/// must not interfere with match lifecycle.</para>
/// </remarks>
public sealed class JsonFileMatchUploader : IMatchUploader
{
    private const string LogCategory = nameof(JsonFileMatchUploader);

    // Match the schema's camelCase property naming so disk-written files and HTTP-uploaded
    // payloads are byte-for-byte equivalent. Dictionary-key casing is left at default
    // (PascalCase, from ItemKind/WeaponKind .ToString()) since the schema enums require that.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _outputDirectory;
    private readonly ILogManager _log;

    public JsonFileMatchUploader(string outputDirectory, ILogManager log)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string OutputDirectory => _outputDirectory;

    public void Upload(MatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Hand the disk work to the thread pool so the mainloop returns immediately. The
        // payload is an immutable record (well, references to immutable structures) so it's
        // safe to read off-thread.
        Task.Run(() => WriteToDisk(payload));
    }

    private void WriteToDisk(MatchPayload payload)
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            string fileName = $"match_{payload.MatchId:N}_{payload.EndedAt:yyyyMMddHHmmss}.json";
            string fullPath = Path.Combine(_outputDirectory, fileName);
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            File.WriteAllText(fullPath, json);
            _log.LogM(LogLevel.Info, LogCategory,
                $"Wrote match: {fullPath} ({json.Length} bytes, {payload.Participants.Count} participants).");
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Failed to write match payload for {payload.MatchId:N}: {ex.Message}");
        }
    }
}
