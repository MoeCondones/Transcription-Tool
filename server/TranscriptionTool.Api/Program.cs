using Microsoft.EntityFrameworkCore;
using TranscriptionTool.Api.Domain;
using TranscriptionTool.Api;

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

builder.Services.AddScoped<ITranscriptionService, TranscriptionService>();

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

app.MapPost("/transcriptions/{id}/transpose", (Guid id, string target) => Results.Ok(new { id, target }))
   .WithName("TransposeTranscription");

app.MapPatch("/transcriptions/{id}/notes", (Guid id) => Results.Ok(new { id, updated = true }))
   .WithName("PatchNotes");

app.Run();

