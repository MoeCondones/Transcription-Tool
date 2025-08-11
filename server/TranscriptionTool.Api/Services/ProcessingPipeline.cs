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

        var notes = await pitch.DetectNotesAsync(transcriptionId, iso, ct);
        await db.NoteEvents.AddRangeAsync(notes, ct);

        var mx = await export.GenerateAsync(t, notes, "musicxml", ct);
        var js = await export.GenerateAsync(t, notes, "json", ct);
        await db.ExportArtifacts.AddRangeAsync(mx, js);

        await db.SaveChangesAsync(ct);
    }
}


