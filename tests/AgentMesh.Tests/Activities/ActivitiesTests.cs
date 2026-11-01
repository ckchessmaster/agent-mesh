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

    [Fact]
    public async Task MergeJobPullRequestAsync_WhenPrUrlIsNull_ThrowsNonRetryableApplicationFailure_WithNoPrUrl()
    {
        // Arrange
        var mockPullRequests = new Moq.Mock<AgentMesh.Infrastructure.Services.IGitHubPullRequestService>();
        var config = new ConfigurationBuilder().Build();
        var activity = new GitActivities(NullLogger<GitActivities>.Instance, mockPullRequests.Object, config);
        var input = new MergePullRequestInput(JobId: Guid.NewGuid().ToString("N"), TargetBranch: "main", PrUrl: null);

        // Act
        var act = async () => await activity.MergeJobPullRequestAsync(input);

        // Assert
        var ex = await act.Should().ThrowAsync<Temporalio.Exceptions.ApplicationFailureException>();
        ex.Which.NonRetryable.Should().BeTrue();
        ex.Which.ErrorType.Should().Be("no_pr_url");
    }

    [Fact]
    public void Worker_AllRegisteredActivities_HaveUniqueNames()
    {
        var exec = new ExecutionActivities(NullLogger<ExecutionActivities>.Instance);
        var git = new GitActivities(
            NullLogger<GitActivities>.Instance,
            new Moq.Mock<AgentMesh.Infrastructure.Services.IGitHubPullRequestService>().Object,
            new ConfigurationBuilder().Build());
        var notif = new NotificationActivities(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<NotificationActivities>.Instance);

        var db = AgentMesh.Tests.Helpers.TestDbContextFactory.Create();
        var dbAct = new DatabaseActivities(db, NullLogger<DatabaseActivities>.Instance);

        var allDefs = new List<Temporalio.Activities.ActivityDefinition>();
        allDefs.AddRange(Temporalio.Activities.ActivityDefinition.CreateAll(exec));
        allDefs.AddRange(Temporalio.Activities.ActivityDefinition.CreateAll(git));
        allDefs.AddRange(Temporalio.Activities.ActivityDefinition.CreateAll(notif));
        allDefs.AddRange(Temporalio.Activities.ActivityDefinition.CreateAll(dbAct));

        var names = allDefs.Select(d => d.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }
}
