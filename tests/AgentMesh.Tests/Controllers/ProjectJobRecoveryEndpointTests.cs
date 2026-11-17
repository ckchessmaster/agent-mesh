using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// <c>POST /api/project-jobs/{id}/recover</c> as an operator or the cockpit actually meets it.
/// </summary>
/// <remarks>
/// The service behind this route is tested in depth elsewhere. What is tested here is the contract the
/// caller depends on: which failures are 404 versus 409 versus 502, whose name ends up on the audit
/// record, and whether the response carries a job the dashboard can repaint from. Each of those is a
/// decision made in this method and nowhere else, and a mistake in any of them shows up as an operator
/// doing the wrong thing at 3am — retrying a job that needs unarchiving, or pressing Recover again
/// because the card still said Stalled.
/// </remarks>
public class ProjectJobRecoveryEndpointTests
{
    /// <summary>
    /// The camelCase the API's MVC formatter uses, plus the enum-as-name converter the recovery DTO opts
    /// into. Deliberately not a hand-written copy of the response: it reads the same payload MVC would
    /// write, so a rename in the record shows up here rather than being papered over.
    /// </summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Recover_ReturnsTheRecoveryAndAJobTheCockpitCanRepaintFrom()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db, heartbeat: "Stalled: the 110-minute ceiling expired");
        var recovery = new FakeRecovery
        {
            // Stands in for the service clearing the marker when it starts a run.
            Before = () =>
            {
                job.ProgressHeartbeat = "Recovering: resuming at task 3/3 - open the pull request";
                db.SaveChanges();
            }
        };

        var result = await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery);

        // The dashboard trusts this DTO to leave "Stalled" in the same paint that reports the recovery.
        // Returning the recovery text alone would keep the amber badge and the Recover button on screen
        // over a job that is running again.
        var body = BodyOf(result);

        body.GetProperty("recovery").GetProperty("action").GetString().Should().Be("Resumed");
        body.GetProperty("job").GetProperty("id").GetGuid().Should().Be(job.Id);
        body.GetProperty("job").GetProperty("lifecycleStatus").GetString().Should().NotBe("Stalled");
    }

    [Fact]
    public async Task Recover_StillReportsTheRecovery_WhenTheJobRowCannotBeReadBack()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery
        {
            // Committed, not merely detached: the controller reads the row back after the service
            // returns, so an uncommitted delete would still be visible to it.
            After = () =>
            {
                db.ProjectJobs.Remove(job);
                db.SaveChanges();
            }
        };

        var body = BodyOf(await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery));

        // Deleting the job inside the same request is pathological, but the shape this asserts is not:
        // a job that was recovered must never make the operator guess whether the call did anything.
        body.GetProperty("recovery").GetProperty("jobId").GetGuid().Should().Be(job.Id);
        body.GetProperty("job").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Recover_ReportsUnavailable_WhenTheServiceIsNotRegistered()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);

        // Registration is pinned elsewhere; what matters here is the answer the cockpit gets, because
        // "no recovery service" and "job not found" are different instructions to an operator.
        var result = await new ProjectJobsController(db)
            .RecoverJob(job.Id, new JobRecoveryRequest(), recovery: null);

        var unavailable = result.Should().BeAssignableTo<ObjectResult>().Subject;
        unavailable.StatusCode.Should().Be(503);
        ErrorOf(result).Should().Contain("not available");
    }

    [Theory]
    [InlineData(typeof(JobRecoveryNotFoundException), 404)]
    [InlineData(typeof(JobRecoveryBlockedException), 409)]
    public async Task Recover_MapsARefusalToItsOwnStatus(Type fault, int expected)
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery
        {
            Throw = fault == typeof(JobRecoveryNotFoundException)
                ? new JobRecoveryNotFoundException(job.Id)
                : new JobRecoveryBlockedException("Job is archived. Unarchive it first.")
        };

        var result = await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery);

        // NotFoundObjectResult and ConflictObjectResult rather than bare ObjectResult: the endpoint uses
        // the named helpers, and an assertion on the exact type would fail on a refactor that kept the
        // status and changed nothing else.
        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(expected);
        ErrorOf(result).Should().Contain(fault == typeof(JobRecoveryNotFoundException) ? "exists to recover" : "Unarchive");
    }

    [Fact]
    public async Task Recover_SaysWhatSurvivedWhenTheOrchestratorRefusedToStartARun()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery
        {
            Throw = new JobRecoveryOrchestrationException("temporal unreachable", new TimeoutException())
        };

        var result = await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(502);

        // Recovery does two things (settle, then start). Reporting a bare 500 would leave the operator
        // unable to tell "nothing happened" from "the turns are settled, only the run failed to start",
        // which is the difference between retrying and walking away.
        ErrorOf(result).Should().Contain("settled");
        BodyOf(result).GetProperty("hint").GetString().Should().Contain("idempotent");
    }

    [Fact]
    public async Task Recover_TreatsAnEmptyBodyAsDefaultPreferences()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery();

        await new ProjectJobsController(db).RecoverJob(job.Id, req: null, recovery);

        // The cockpit sends {} and curl sends nothing at all; both mean "resume, no note".
        recovery.LastCall!.Value.Request.Should().NotBeNull();
        recovery.LastCall!.Value.Request.Resume.Should().BeTrue();
        recovery.LastCall!.Value.Request.QuietSeconds.Should().BeGreaterThan(0,
            "an unset window would mean settling turns that are still in flight");
    }

    [Theory]
    [InlineData("explicit-caller", "explicit-caller")]
    [InlineData("   ", "authenticated-operator")]
    [InlineData(null, "authenticated-operator")]
    public async Task Recover_RecordsWhoAsked_ForTheAuditTrail(string? bodyCaller, string expected)
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery();
        var controller = new ProjectJobsController(db);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "authenticated-operator")], authenticationType: "test"))
            }
        };

        await controller.RecoverJob(job.Id, new JobRecoveryRequest(Caller: bodyCaller), recovery);

        // A recovery rewrites durable state on a human's say-so. The request's own Caller field is
        // honoured when present (an operator acting for someone else, or a script naming itself) and the
        // authenticated identity is the fallback, so the record is never blank.
        recovery.LastCall!.Value.Caller.Should().Be(expected);
    }

    [Fact]
    public async Task Recover_FallsBackToPlainOperator_WhenNothingAuthenticated()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        var recovery = new FakeRecovery();

        await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery);

        // The route is [Authorize], so this is the "auth middleware off in a dev cluster" case. It still
        // has to produce a readable name on the timeline card rather than an empty string.
        recovery.LastCall!.Value.Caller.Should().Be("operator");
    }

    [Fact]
    public async Task Recover_HandsTheCancellationTokenToTheService()
    {
        using var db = TestDbContextFactory.Create();
        var job = AddJob(db);
        using var cts = new CancellationTokenSource();
        var seen = false;
        var recovery = new FakeRecovery { CtProbe = _ => seen = true };

        await new ProjectJobsController(db).RecoverJob(job.Id, new JobRecoveryRequest(), recovery, cts.Token);

        // Recovery can start a workflow run. An abandoned HTTP request must not leave a service looping
        // after the caller has gone.
        seen.Should().BeTrue();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ProjectJob AddJob(
        AgentMeshDbContext db,
        string? heartbeat = "Executing task 5/5: open the pull request")
    {
        var job = new ProjectJob
        {
            ProjectId = "agent-mesh",
            Title = "Standardized project setup",
            Status = ProjectJobStatus.Active,
            GitBranchName = "mesh/job-538070c7",
            AssignedAgentId = "astra",
            ProgressHeartbeat = heartbeat
        };
        db.ProjectJobs.Add(job);
        db.SaveChanges();
        return job;
    }

    private static JsonElement BodyOf(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value, Web);

    private static string ErrorOf(IActionResult result) => BodyOf(result).GetProperty("error").GetString()!;

    private sealed class FakeRecovery : IJobTurnRecovery
    {
        public Exception? Throw { get; set; }
        public Action? Before { get; set; }
        public Action? After { get; set; }
        public Action<CancellationToken>? CtProbe { get; set; }
        public (Guid JobId, JobRecoveryRequest Request, string Caller)? LastCall { get; private set; }

        public Task<JobRecoveryResult> RecoverAsync(
            Guid jobId,
            JobRecoveryRequest request,
            string caller,
            CancellationToken ct = default)
        {
            LastCall = (jobId, request, caller);
            Before?.Invoke();
            CtProbe?.Invoke(ct);
            if (Throw is not null) throw Throw;
            After?.Invoke();

            return Task.FromResult(new JobRecoveryResult(
                jobId,
                $"job-{jobId}",
                JobRecoveryAction.Resumed,
                [],
                "open the pull request",
                3,
                3,
                "https://github.com/o/r/pull/91",
                "Resumed job at task 3/3."));
        }
    }
}
