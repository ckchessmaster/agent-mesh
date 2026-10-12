using AgentMesh.Core.Activities;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class ExecutionActivities(ILogger<ExecutionActivities> logger) : IExecutionActivities
{
    [Activity]
    public Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson)
    {
        logger.LogInformation("Executing subsystem {Service}:{Verb} with payload: {Payload}", 
            targetService, actionVerb, payloadJson);

        // Simulation/MVP execution
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            service = targetService,
            verb = actionVerb,
            executedAt = DateTimeOffset.UtcNow,
            status = "success"
        });

        return Task.FromResult(result);
    }
}
