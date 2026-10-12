using AgentMesh.Core.Activities;
using AgentMesh.Core.Services;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class ExecutionActivities(
    ILogger<ExecutionActivities> logger,
    IStorageService? storageService = null,
    IConfiguration? config = null) : IExecutionActivities
{
    [Activity]
    public async Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson, Guid? jobId = null)
    {
        logger.LogInformation("Executing subsystem {Service}:{Verb} for Job {JobId} with payload: {Payload}", 
            targetService, actionVerb, jobId, payloadJson);

        // Simulation/MVP execution
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            service = targetService,
            verb = actionVerb,
            executedAt = DateTimeOffset.UtcNow,
            status = "success"
        });

        var threshold = config?.GetValue<int>("Storage:S3:ThresholdBytes", 65536) ?? 65536;

        if (result.Length > threshold && storageService != null && jobId.HasValue)
        {
            var s3Key = $"jobs/{jobId.Value}/result.json";
            var s3Uri = await storageService.UploadTextAsync(s3Key, result);
            logger.LogInformation("Offloaded result for job {JobId} ({Bytes} bytes) to {S3Uri}", jobId, result.Length, s3Uri);

            result = System.Text.Json.JsonSerializer.Serialize(new
            {
                service = targetService,
                verb = actionVerb,
                _offloaded = true,
                s3_uri = s3Uri,
                byte_size = result.Length,
                executedAt = DateTimeOffset.UtcNow,
                status = "success"
            });
        }

        return result;
    }
}
