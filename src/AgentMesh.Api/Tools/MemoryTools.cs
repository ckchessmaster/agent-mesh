using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using ModelContextProtocol.Server;

namespace AgentMesh.Api.Tools;

/// <summary>
/// The <c>save_memory</c> MCP tool: the write path for lessons an agent must not have to relearn.
/// </summary>
/// <remarks>
/// <para>
/// This is the cold-start-amnesia fix. A runner that spent three turns discovering that
/// <c>dotnet test</c> needs <c>-v quiet --nologo</c> here, or that the dashboard's SignalR fixture must be
/// mocked, records that once and every later session gets it injected into its prompt stack.
/// </para>
/// <para>
/// <strong>Why <c>category</c> is a string and not an enum parameter:</strong> binding an enum makes the
/// library reject an out-of-range value with the generic "An error occurred invoking 'save_memory'",
/// which tells a model nothing about what to send instead. A string parameter plus explicit validation
/// returns the allowed list. The <c>enum</c> constraint is still advertised on the wire —
/// <see cref="MemoryToolSchema.ApplyCategoryEnum"/> patches it into the published schema at
/// <c>tools/list</c> time — so well-behaved clients never send a bad value in the first place.
/// </para>
/// <para>
/// <strong>Why the arguments are flat parameters and not a bound DTO:</strong> the job spec lists a
/// <c>SaveMemoryArgs(Category, Key, Value, IsGlobal)</c> record, and also specifies an input schema with
/// <c>category</c>/<c>key</c>/<c>value</c>/<c>is_global</c> as top-level required properties. Those two
/// cannot both hold: verified against ModelContextProtocol 2.2.0, binding a record parameter emits
/// <c>{"args": {"category": ...}}</c> with every field marked required, including <c>isGlobal</c>. The
/// schema is the contract an agent calls against and the one the DoD test pins, so the parameters stay
/// flat and no unused DTO type is introduced.
/// </para>
/// <para>
/// <strong>Why a failed write is a <c>success:false</c> payload and not an exception:</strong> the caller
/// is a model mid-turn. A structured, human-readable refusal keeps it in the conversation; a protocol
/// error costs it the turn and it usually retries the same bad argument.
/// </para>
/// </remarks>
[McpServerToolType]
public class MemoryTools(
    IAgentMemoryRepository memories,
    IMemoryContextResolver scopeResolver,
    ILogger<MemoryTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "save_memory")]
    [Description("Persist a long-term memory or lesson learned for this project or environment. Use this for environment quirks, required build/test flags, mock requirements, or codebase gotchas that future sessions must recall.")]
    public async Task<string> SaveMemory(
        [Description("The functional classification of the memory: 'environment' (paths, SDK versions, ports, daemon sockets), 'build_and_test' (required build/test flags, runner mocks, compilation quirks), or 'codebase_gotcha' (domain invariants, repository rules, architectural constraints).")]
        string category,
        [Description("Short, unique identifier (e.g. 'dashboard_vitest_mocking', 'dotnet_test_flags'). Re-saving an existing key for the same project overwrites that lesson, so use the same key to refine it.")]
        string key,
        [Description("The concrete guideline, command flag, or lesson learned. Write it as an instruction a fresh session can follow without extra context.")]
        string value,
        [Description("True if this applies across all projects (e.g. general Linux tooling); false if specific to the current project.")]
        bool? is_global = null,
        CancellationToken ct = default)
    {
        var scope = await scopeResolver.ResolveAsync(is_global, ct);

        if (string.IsNullOrWhiteSpace(scope.AgentId))
        {
            logger.LogWarning("save_memory rejected: no agent identity resolvable ({AgentSource})", scope.AgentIdSource);
            return Error(
                "unauthorized",
                "Could not resolve the calling agent. Present a bearer token or send the 'X-Agent-Id' header.",
                details: new { agentIdSource = scope.AgentIdSource });
        }

        if (string.IsNullOrWhiteSpace(scope.ProjectId))
        {
            logger.LogWarning(
                "save_memory rejected for agent {AgentId}: no project context ({ProjectSource})",
                scope.AgentId, scope.ProjectIdSource);
            return Error(
                "project_context_missing",
                $"Could not resolve a project for this call ({scope.ProjectIdSource}). Send the 'X-Project-Id' or 'X-Session-Id' header, or pass is_global=true if the lesson is not specific to one project.",
                details: new { agentIdSource = scope.AgentIdSource, projectIdSource = scope.ProjectIdSource });
        }

        if (!AgentMemoryCategories.TryNormalize(category, out var normalizedCategory))
        {
            return Error(
                "invalid_category",
                $"Invalid category '{category}'. Allowed values: {AgentMemoryCategories.AllowedValuesHint()}.",
                details: new { allowedCategories = AgentMemoryCategories.All });
        }

        var memory = AgentMemory.TryCreate(
            scope.AgentId, scope.ProjectId, normalizedCategory, key, value);

        if (memory is null)
        {
            // TryCreate is the single authority on what a valid row looks like; re-running the field
            // checks through ValidationErrors turns a null return into the specific complaint, which is
            // what lets the caller fix the argument instead of guessing at it.
            var probe = new AgentMemory
            {
                AgentId = scope.AgentId,
                ProjectId = scope.ProjectId,
                Category = normalizedCategory,
                Key = key?.Trim() ?? string.Empty,
                Value = value?.Trim() ?? string.Empty,
            };

            return Error(
                "invalid_memory",
                "Memory was not saved: " + string.Join(' ', probe.ValidationErrors()),
                details: new { keyLength = key?.Trim().Length ?? 0, valueLength = value?.Trim().Length ?? 0 });
        }

        try
        {
            var saved = await memories.UpsertMemoryAsync(memory, ct);

            logger.LogInformation(
                "Memory saved via MCP: agent={AgentId} project={ProjectId} category={Category} key={Key} id={Id}",
                saved.AgentId, saved.ProjectId, saved.Category, saved.Key, saved.Id);

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = $"Memory saved under key '{saved.Key}' for project '{saved.ProjectId}'. It will be injected into future sessions.",
                memoryId = saved.Id,
                category = saved.Category,
                agentId = saved.AgentId,
                projectId = saved.ProjectId,
                isGlobal = saved.IsGlobal,
                confidenceScore = saved.ConfidenceScore,
                updatedAt = saved.UpdatedAt,
                resolvedFrom = new
                {
                    agentId = scope.AgentIdSource,
                    projectId = scope.ProjectIdSource,
                },
            }, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A memory write is best-effort from the agent's point of view: it must never take a turn down
            // with it, and it must say so plainly so the agent can fall back to reporting the finding in
            // its response text instead of silently believing it was persisted.
            logger.LogError(
                ex,
                "save_memory failed for agent {AgentId} project {ProjectId} key {Key}",
                scope.AgentId, scope.ProjectId, key);
            return Error("storage_unavailable", $"Memory could not be persisted: {ex.Message}");
        }
    }

    private static string Error(string code, string message, object? details = null) =>
        JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            message,
            details,
        }, JsonOptions);
}
