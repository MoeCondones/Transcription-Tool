using System.ComponentModel.DataAnnotations;

namespace TranscriptionTool.Api.Contracts;

public sealed class CreateTranscriptionRequest
{
    [Required]
    public IFormFile? Audio { get; set; }

    // alto|tenor|baritone|soprano|auto
    [Required]
    [RegularExpression("^(alto|tenor|baritone|soprano|auto)$", ErrorMessage = "Invalid instrument hint")] 
    public string InstrumentHint { get; set; } = "auto";
}


