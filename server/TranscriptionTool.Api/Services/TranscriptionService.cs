using TranscriptionTool.Api.Services;

namespace TranscriptionTool.Api;

/// <summary>
/// Facade for scheduling and managing transcription jobs.
/// </summary>
public interface ITranscriptionService
{
    void ScheduleProcessing(Guid transcriptionId);
}

/// <summary>
/// Default implementation that places jobs on an in-memory queue.
/// </summary>
public sealed class TranscriptionService : ITranscriptionService
{
    private readonly TranscriptionQueue queue;
    public TranscriptionService(TranscriptionQueue queue) => this.queue = queue;

    public void ScheduleProcessing(Guid transcriptionId) => queue.Enqueue(transcriptionId);
}


