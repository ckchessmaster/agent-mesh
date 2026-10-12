using System.Text.Json;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMesh.Tests.Activities;

public class ActivitiesTests
{
    [Theory]
    [InlineData("platform_ops", "stage_journal_entry", "{\"title\":\"Sprint Goal\"}")]
    [InlineData("github", "apply_diff", "{\"repo\":\"agent-mesh\",\"branch\":\"main\"}")]
    [InlineData("aws", "provision_s3", "{\"bucket\":\"data-lake\"}")]
    public async Task ExecutionActivities_ReturnsSuccessJson(string service, string verb, string payload)
    {
        // Arrange
        var activity = new ExecutionActivities(NullLogger<ExecutionActivities>.Instance);

        // Act
        var resultJson = await activity.ExecuteSubsystemAction(service, verb, payload);

        // Assert
        resultJson.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("service").GetString().Should().Be(service);
        root.GetProperty("verb").GetString().Should().Be(verb);
    }

    [Fact]
    public async Task NotificationActivities_WhenWebhookNotConfigured_LogsAndCompletesCleanly()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Discord:WebhookUrl"] = null
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var httpClient = new HttpClient();
        var activity = new NotificationActivities(httpClient, config, NullLogger<NotificationActivities>.Instance);

        var input = new AgentTaskWorkflowInput(
            JobId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            TargetService: "kubernetes",
            ActionVerb: "delete_pod",
            PayloadJson: "{\"pod\":\"api\"}",
            RequestedByAgentId: "gemini-spark"
        );

        // Act
        var act = async () => await activity.SendDiscordApprovalRequest(input);

        // Assert
        await act.Should().NotThrowAsync();
    }
}
