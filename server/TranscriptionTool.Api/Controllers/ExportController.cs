using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TranscriptionTool.Api.Domain;

namespace TranscriptionTool.Api.Controllers;

[ApiController]
[Route("transcriptions/{id}/export")] 
public class ExportController : ControllerBase
{
    private readonly AppDb db;
    public ExportController(AppDb db) => this.db = db;

    [HttpGet]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string format = "musicxml")
    {
        var art = await db.ExportArtifacts.Where(a => a.TranscriptionId == id && a.Format == format).FirstOrDefaultAsync();
        if (art == null) return NotFound();
        var mime = format switch
        {
            "musicxml" => "application/vnd.recordare.musicxml+xml",
            "midi" => "audio/midi",
            "json" => "application/json",
            _ => "application/octet-stream"
        };
        return File(art.Content, mime, fileDownloadName: $"{id}.{format}");
    }
}


