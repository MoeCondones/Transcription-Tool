using Microsoft.EntityFrameworkCore;
using TranscriptionTool.Api.Domain;
using TranscriptionTool.Api;
using TranscriptionTool.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// DB: SQL Server in prod via env TRANSC_DB=sqlserver; SQLite fallback for dev
var dbKind = Environment.GetEnvironmentVariable("TRANSC_DB") ?? "sqlite";
if (dbKind.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
{
    var cs = builder.Configuration.GetConnectionString("SqlServer")
             ?? "Server=localhost,1433;Database=Transc;User Id=sa;Password=Your_password123;TrustServerCertificate=True";
    builder.Services.AddDbContext<AppDb>(opt => opt.UseSqlServer(cs));
}
else
{
    var cs = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=transc.db";
    builder.Services.AddDbContext<AppDb>(opt => opt.UseSqlite(cs));
}

// app services
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddScoped<ITranscriptionService, TranscriptionService>();
builder.Services.AddScoped<IProcessingPipeline, ProcessingPipeline>();
builder.Services.AddScoped<ISeparationService, SeparationService>();
builder.Services.AddScoped<IPitchDetectionService, PitchDetectionService>();
builder.Services.AddScoped<INotationExportService, NotationExportService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Ensure database exists (SQLite dev convenience)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
}

// Map endpoints
app.MapPost("/transcriptions", async (HttpRequest http, AppDb db, ITranscriptionService svc) =>
{
    if (!http.HasFormContentType) return Results.BadRequest("multipart/form-data expected");
    var form = await http.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    var hint = form["instrumentHint"].FirstOrDefault() ?? "auto";
    if (file == null || file.Length == 0) return Results.BadRequest("audio file missing");

    var entity = new Transcription
    {
        Id = Guid.NewGuid(),
        SourceFileName = file.FileName,
        InstrumentHint = hint,
        Status = "queued",
    };
    await db.Transcriptions.AddAsync(entity);

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    await db.AudioBlobs.AddAsync(new AudioBlob
    {
        Id = Guid.NewGuid(),
        TranscriptionId = entity.Id,
        Kind = "original",
        Content = ms.ToArray(),
    });
    await db.SaveChangesAsync();
    // enqueue background processing so status will transition queued -> processing -> done
    svc.ScheduleProcessing(entity.Id);

    // Processing deferred to background in later step
    return Results.Accepted($"/transcriptions/{entity.Id}", new { id = entity.Id });
})
   .WithName("CreateTranscription");

app.MapGet("/transcriptions/{id}", async (Guid id, AppDb db) =>
{
    var t = await db.Transcriptions.FindAsync(id);
    return t is null ? Results.NotFound() : Results.Ok(new
    {
        id = t.Id,
        status = t.Status,
        instrument = t.InstrumentHint,
        key = t.KeySignature,
        meter = t.Meter,
    });
})
   .WithName("GetTranscription");

app.MapGet("/transcriptions/{id}/export", (Guid id, string format) => Results.Ok(new { id, format }))
   .WithName("ExportTranscription");

app.MapPost("/transcriptions/{id}/transpose", async (Guid id, string target, AppDb db) =>
{
    var t = await db.Transcriptions.FindAsync(id);
    if (t is null) return Results.NotFound();
    // Build a JSON notes payload from DB and call worker in transpose mode
    var notes = await db.NoteEvents.Where(n => n.TranscriptionId == id).Select(n => new { n.StartSeconds, n.EndSeconds, n.Midi }).ToListAsync();
    var tempIn = Path.Combine(Path.GetTempPath(), $"{id}-notes.json");
    var tempOutJson = Path.Combine(Path.GetTempPath(), $"{id}-{target}-notes.json");
    var tempXml = Path.Combine(Path.GetTempPath(), $"{id}-{target}.musicxml");
    await File.WriteAllBytesAsync(tempIn, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { meta = new { tempo = 120, instrument = t.InstrumentHint }, notes }), default);

    var py = Environment.GetEnvironmentVariable("PYTHON_EXEC") ?? "python3";
    var worker = Environment.GetEnvironmentVariable("WORKER_SCRIPT") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", "process.py"));
    var info = new System.Diagnostics.ProcessStartInfo
    {
        FileName = py,
        ArgumentList = { worker, "--input-json", tempIn, "--instrument", target, "--output-json", tempOutJson, "--output-musicxml", tempXml },
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    using var proc = System.Diagnostics.Process.Start(info)!;
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0) return Results.Problem("Transpose worker failed");
    var xml = await File.ReadAllBytesAsync(tempXml);
    await db.ExportArtifacts.AddAsync(new ExportArtifact { Id = Guid.NewGuid(), TranscriptionId = id, Format = $"musicxml-{target}", Content = xml, CreatedAt = DateTime.UtcNow });
    await db.SaveChangesAsync();
    return Results.Ok(new { id, target });
})
   .WithName("TransposeTranscription");

app.MapPatch("/transcriptions/{id}/notes", (Guid id) => Results.Ok(new { id, updated = true }))
   .WithName("PatchNotes");

app.Run();

