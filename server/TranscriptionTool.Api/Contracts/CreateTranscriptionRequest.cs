using System.ComponentModel.DataAnnotations;

namespace TranscriptionTool.Api.Contracts;

/// <summary>
/// Request contract for creating a new transcription job.
/// Expects multipart/form-data with the audio file and an optional instrument hint.
/// </summary>
public sealed class CreateTranscriptionRequest
{
    /// <summary>
    /// Audio file to transcribe (wav/mp3/flac).
    /// </summary>
    [Required]
    public IFormFile? Audio { get; set; }

    // alto|tenor|baritone|soprano|auto
    /// <summary>
    /// Optional instrument hint to guide transposition and analysis.
    /// Allowed values: alto, tenor, baritone, soprano, auto.
    /// </summary>
    [Required]
    [RegularExpression("^(alto|tenor|baritone|soprano|auto)$", ErrorMessage = "Invalid instrument hint")] 
    public string InstrumentHint { get; set; } = "auto";
}


