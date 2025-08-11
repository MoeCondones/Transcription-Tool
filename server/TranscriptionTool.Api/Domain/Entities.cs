using Microsoft.EntityFrameworkCore;

namespace TranscriptionTool.Api.Domain;

/// <summary>
/// Represents a transcription job and its derived metadata.
/// </summary>
public class Transcription
{
    public Guid Id { get; set; }
    public string SourceFileName { get; set; } = string.Empty;
    public string InstrumentHint { get; set; } = "auto";
    public string Status { get; set; } = "queued";
    public string? KeySignature { get; set; }
    public string? Meter { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AudioBlob> Blobs { get; set; } = new List<AudioBlob>();
    public ICollection<NoteEvent> Notes { get; set; } = new List<NoteEvent>();
}

public class AudioBlob
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }
    /// <summary>
    /// original: uploaded mix; isolated: sax-only track after separation.
    /// </summary>
    public string Kind { get; set; } = "original"; // original|isolated
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class NoteEvent
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public int Midi { get; set; }
    public int? Velocity { get; set; }
    public int? Measure { get; set; }
}

public class ExportArtifact
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }
    /// <summary>
    /// Export format content, e.g., MusicXML, MIDI, JSON preview.
    /// </summary>
    public string Format { get; set; } = "musicxml"; // musicxml|pdf|midi|json
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// EF Core DbContext for the transcription domain.
/// </summary>
public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<Transcription> Transcriptions => Set<Transcription>();
    public DbSet<AudioBlob> AudioBlobs => Set<AudioBlob>();
    public DbSet<NoteEvent> NoteEvents => Set<NoteEvent>();
    public DbSet<ExportArtifact> ExportArtifacts => Set<ExportArtifact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transcription>().HasKey(x => x.Id);
        modelBuilder.Entity<AudioBlob>().HasKey(x => x.Id);
        modelBuilder.Entity<NoteEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<ExportArtifact>().HasKey(x => x.Id);
    }
}


