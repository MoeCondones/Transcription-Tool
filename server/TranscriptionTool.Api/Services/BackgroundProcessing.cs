using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using TranscriptionTool.Api.Domain;

namespace TranscriptionTool.Api.Services;

/// <summary>
/// Simple in-memory background queue and worker scaffold.
/// Later we can replace with a durable queue (e.g., Hangfire, Azure Queue, or RabbitMQ).
/// </summary>
public interface ITranscriptionQueue
{
    void Enqueue(Guid transcriptionId);
}

public sealed class TranscriptionQueue : ITranscriptionQueue
{
    private readonly ConcurrentQueue<Guid> queue = new();

    public void Enqueue(Guid transcriptionId) => queue.Enqueue(transcriptionId);

    public bool TryDequeue(out Guid id) => queue.TryDequeue(out id);
}

public sealed class TranscriptionWorker : BackgroundService
{
    private readonly ILogger<TranscriptionWorker> logger;
    private readonly IServiceProvider services;
    private readonly TranscriptionQueue queue;

    private readonly IProcessingPipeline pipeline;

    public TranscriptionWorker(ILogger<TranscriptionWorker> logger, IServiceProvider services, TranscriptionQueue queue, IProcessingPipeline pipeline)
    {
        this.logger = logger;
        this.services = services;
        this.queue = queue;
        this.pipeline = pipeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Transcription worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!queue.TryDequeue(out var id))
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();

                var t = await db.Transcriptions.FirstOrDefaultAsync(x => x.Id == id, stoppingToken);
                if (t == null) continue;
                t.Status = "processing";
                await db.SaveChangesAsync(stoppingToken);

                try
                {
                    await pipeline.RunAsync(id, stoppingToken);
                    t.Status = "done";
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Pipeline failed for {Id}", id);
                    t.Status = "error";
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker error");
            }
        }
    }
}


