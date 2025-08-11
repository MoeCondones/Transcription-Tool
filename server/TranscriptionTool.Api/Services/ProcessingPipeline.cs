using System.Text;
using Microsoft.EntityFrameworkCore;
using TranscriptionTool.Api.Domain;

namespace TranscriptionTool.Api.Services;

/// <summary>
/// Separation stage: from mix to sax-only audio (stubbed for now).
/// Replace with Demucs/Spleeter integration later.
/// </summary>
public interface ISeparationService
{
    Task<byte[]> IsolateSaxAsync(byte[] mix, CancellationToken ct);
}

public sealed class SeparationService : ISeparationService
{
    public Task<byte[]> IsolateSaxAsync(byte[] mix, CancellationToken ct) => Task.FromResult(mix);
}

/// <summary>
/// Pitch/rhythm stage: detect notes with timing (stubbed for now).
/// Replace with Python worker call and real detection later.
/// </summary>
public interface IPitchDetectionService
{
    Task<IReadOnlyList<NoteEvent>> DetectNotesAsync(Guid transcriptionId, byte[] audio, CancellationToken ct);
}

public sealed class PitchDetectionService : IPitchDetectionService
{
    public Task<IReadOnlyList<NoteEvent>> DetectNotesAsync(Guid transcriptionId, byte[] audio, CancellationToken ct)
    {
        var list = new List<NoteEvent>
        {
            new NoteEvent
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcriptionId,
                StartSeconds = 0,
                EndSeconds = 1,
                Midi = 69,
                Velocity = 90,
                Measure = 1,
            }
        };
        return Task.FromResult<IReadOnlyList<NoteEvent>>(list);
    }
}

/// <summary>
/// Notation/export stage (stub): produce MusicXML/MIDI/JSON artifacts from detected notes.
/// </summary>
public interface INotationExportService
{
    Task<ExportArtifact> GenerateAsync(Transcription t, IEnumerable<NoteEvent> notes, string format, CancellationToken ct);
}

public sealed class NotationExportService : INotationExportService
{
    public Task<ExportArtifact> GenerateAsync(Transcription t, IEnumerable<NoteEvent> notes, string format, CancellationToken ct)
    {
        format = format.ToLowerInvariant();
        byte[] content;
        if (format == "musicxml")
        {
            // Minimal MusicXML-like placeholder
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<!-- Placeholder MusicXML; replace with real engraving pipeline -->");
            sb.AppendLine("<score-partwise version=\"3.1\"><part id=\"P1\"><measure number=\"1\"><note><pitch><step>A</step><octave>4</octave></pitch><duration>1</duration></note></measure></part></score-partwise>");
            content = Encoding.UTF8.GetBytes(sb.ToString());
        }
        else if (format == "json")
        {
            var json = System.Text.Json.JsonSerializer.Serialize(notes.Select(n => new { n.StartSeconds, n.EndSeconds, n.Midi }));
            content = Encoding.UTF8.GetBytes(json);
        }
        else if (format == "midi")
        {
            // Simple placeholder binary
            content = Encoding.UTF8.GetBytes(@"MThd\0\0\0\6\0\1\0\1\0\96");
        }
        else
        {
            content = Encoding.UTF8.GetBytes("unsupported");
        }

        return Task.FromResult(new ExportArtifact
        {
            Id = Guid.NewGuid(),
            TranscriptionId = t.Id,
            Format = format,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        });
    }
}

/// <summary>
/// End-to-end pipeline orchestrator (stub implementation).
/// </summary>
public interface IProcessingPipeline
{
    Task RunAsync(Guid transcriptionId, CancellationToken ct);
}

public sealed class ProcessingPipeline : IProcessingPipeline
{
    private readonly AppDb db;
    private readonly ISeparationService separation;
    private readonly IPitchDetectionService pitch;
    private readonly INotationExportService export;

    public ProcessingPipeline(AppDb db, ISeparationService separation, IPitchDetectionService pitch, INotationExportService export)
    {
        this.db = db;
        this.separation = separation;
        this.pitch = pitch;
        this.export = export;
    }

    public async Task RunAsync(Guid transcriptionId, CancellationToken ct)
    {
        var t = await db.Transcriptions.FirstOrDefaultAsync(x => x.Id == transcriptionId, ct);
        if (t == null) return;

        var mix = await db.AudioBlobs.Where(b => b.TranscriptionId == transcriptionId && b.Kind == "original").Select(b => b.Content).FirstAsync(ct);
        var iso = await separation.IsolateSaxAsync(mix, ct);
        await db.AudioBlobs.AddAsync(new AudioBlob
        {
            Id = Guid.NewGuid(),
            TranscriptionId = transcriptionId,
            Kind = "isolated",
            Content = iso,
        }, ct);

        // call Python worker for real note detection and MusicXML
        var tempIn = Path.Combine(Path.GetTempPath(), $"{transcriptionId}-in.wav");
        var tempNotes = Path.Combine(Path.GetTempPath(), $"{transcriptionId}-notes.json");
        var tempXml = Path.Combine(Path.GetTempPath(), $"{transcriptionId}-score.musicxml");
        await File.WriteAllBytesAsync(tempIn, iso, ct);

        var py = Environment.GetEnvironmentVariable("PYTHON_EXEC") ?? "python3";
        var worker = Environment.GetEnvironmentVariable("WORKER_SCRIPT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", "process.py"));
        var instrument = t.InstrumentHint;

        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = py,
            ArgumentList = { worker, "--input", tempIn, "--instrument", instrument, "--output-json", tempNotes, "--output-musicxml", tempXml },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(info)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Worker failed: {stderr}");
        }

        // read outputs
        var notesJson = await File.ReadAllTextAsync(tempNotes, ct);
        var root = System.Text.Json.JsonDocument.Parse(notesJson).RootElement;
        // meta
        if (root.TryGetProperty("meta", out var meta))
        {
            t.KeySignature = meta.TryGetProperty("key", out var k) ? k.GetString() : t.KeySignature;
            t.Meter = meta.TryGetProperty("meter", out var m) ? m.GetString() : t.Meter;
        }
        var parsed = root.GetProperty("notes");
        var toInsert = new List<NoteEvent>();
        foreach (var el in parsed.EnumerateArray())
        {
            toInsert.Add(new NoteEvent
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcriptionId,
                StartSeconds = el.GetProperty("start").GetDouble(),
                EndSeconds = el.GetProperty("end").GetDouble(),
                Midi = el.GetProperty("midi").GetInt32(),
            });
        }
        await db.NoteEvents.AddRangeAsync(toInsert, ct);

        var xmlBytes = await File.ReadAllBytesAsync(tempXml, ct);
        await db.ExportArtifacts.AddAsync(new ExportArtifact
        {
            Id = Guid.NewGuid(),
            TranscriptionId = transcriptionId,
            Format = "musicxml",
            Content = xmlBytes,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        // also store a JSON artifact for quick preview
        await db.ExportArtifacts.AddAsync(new ExportArtifact
        {
            Id = Guid.NewGuid(),
            TranscriptionId = transcriptionId,
            Format = "json",
            Content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(toInsert.Select(n => new { n.StartSeconds, n.EndSeconds, n.Midi })),
            CreatedAt = DateTime.UtcNow,
        }, ct);

        await db.SaveChangesAsync(ct);
    }
}


