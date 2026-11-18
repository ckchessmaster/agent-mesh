using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// Resolves effective, decrypted MCP server configurations assigned to a persona.
/// </summary>
public interface IPersonaMcpResolver
{
    Task<List<ResolvedMcpServerConfig>> ResolveForPersonaAsync(string? personaId, CancellationToken ct = default);
}

public class PersonaMcpResolver(
    AgentMeshDbContext db,
    ICredentialEncryptionService? encryptionService = null,
    ILogger<PersonaMcpResolver>? logger = null) : IPersonaMcpResolver
{
    public Task<List<ResolvedMcpServerConfig>> ResolveForPersonaAsync(
        string? personaId,
        CancellationToken ct = default) =>
        ResolveForPersonaAsync(db, personaId, encryptionService, logger, ct);

    public static Task<List<ResolvedMcpServerConfig>> ResolveForPersonaAsync(
        AgentMeshDbContext db,
        string? personaId,
        ICredentialEncryptionService? encryptionService,
        CancellationToken ct = default) =>
        ResolveForPersonaAsync(db, personaId, encryptionService, logger: null, ct);

    public static async Task<List<ResolvedMcpServerConfig>> ResolveForPersonaAsync(
        AgentMeshDbContext db,
        string? personaId,
        ICredentialEncryptionService? encryptionService,
        ILogger? logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personaId))
        {
            return new List<ResolvedMcpServerConfig>();
        }

        var normalizedPersonaId = McpControllerHelpers.NormalizeId(personaId);

        var assignments = await db.PersonaMcpServers
            .AsNoTracking()
            .Include(pm => pm.McpServer)
            .Where(pm => pm.PersonaId == normalizedPersonaId &&
                         pm.Enabled &&
                         pm.McpServer != null &&
                         pm.McpServer.Enabled)
            .OrderBy(pm => pm.McpServer!.Name)
            .ThenBy(pm => pm.McpServerId)
            .ToListAsync(ct);

        var resolved = new List<ResolvedMcpServerConfig>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var server = assignment.McpServer!;
            if (!IsTransportConfigured(server))
            {
                logger?.LogWarning(
                    "Skipping MCP server '{ServerId}' for persona '{PersonaId}': invalid {Transport} configuration.",
                    server.Id, normalizedPersonaId, server.Transport);
                continue;
            }

            var env = DecryptMergedMap(
                encryptionService,
                server.EnvEncrypted,
                assignment.EnvOverridesEncrypted,
                server.Id,
                normalizedPersonaId,
                "env",
                logger);

            var headers = DecryptMergedMap(
                encryptionService,
                server.HeadersEncrypted,
                assignment.HeadersOverridesEncrypted,
                server.Id,
                normalizedPersonaId,
                "headers",
                logger);

            resolved.Add(new ResolvedMcpServerConfig
            {
                Id = server.Id,
                Name = server.Name,
                Transport = server.Transport,
                Command = server.Transport == McpTransportType.Stdio ? server.Command?.Trim() : null,
                Args = server.Transport == McpTransportType.Stdio
                    ? McpControllerHelpers.ParseStringList(server.ArgsJson)
                    : new List<string>(),
                Url = server.Transport != McpTransportType.Stdio ? server.Url?.Trim() : null,
                Env = env,
                Headers = headers,
                TimeoutSeconds = server.TimeoutSeconds > 0 ? server.TimeoutSeconds : 30,
                AllowedTools = McpControllerHelpers.ParseNullableStringList(assignment.AllowedToolsJson),
                BlockedTools = McpControllerHelpers.ParseNullableStringList(assignment.BlockedToolsJson)
            });
        }

        return resolved;
    }

    private static Dictionary<string, string> DecryptMergedMap(
        ICredentialEncryptionService? encryptionService,
        string? baseCipherText,
        string? overrideCipherText,
        string serverId,
        string personaId,
        string mapKind,
        ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(baseCipherText) && string.IsNullOrWhiteSpace(overrideCipherText))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (encryptionService == null)
        {
            logger?.LogWarning(
                "Credential encryption service unavailable when resolving {MapKind} for MCP server '{ServerId}' on persona '{PersonaId}'; omitting encrypted secrets.",
                mapKind, serverId, personaId);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return McpSecretProtector.MergeAndDecrypt(encryptionService, baseCipherText, overrideCipherText);
    }

    private static bool IsTransportConfigured(McpServer server)
    {
        if (server.Transport == McpTransportType.Stdio)
        {
            return !string.IsNullOrWhiteSpace(server.Command);
        }

        return !string.IsNullOrWhiteSpace(server.Url) &&
               Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
