using AgentMesh.Cli.Models;
using AgentMesh.Core.Models;

namespace AgentMesh.Cli.Services;

/// <summary>
/// Resolves the identity a runner registers *as*.
/// </summary>
/// <remarks>
/// A persona is the character; a driver is the runtime that executes for it. Registration pushes
/// <c>preset.Id/Name/Role</c> onto the driver row in <c>agents</c>, so a persona runner that copies
/// the persona's name and role into the preset renames the driver to whichever persona last ran on
/// it - and gives the driver a job description that belongs to a persona. Both showed up on the
/// drivers tab.
///
/// The persona identity belongs on <see cref="AgentPresetConfig.PersonaId"/> /
/// <see cref="AgentPresetConfig.PersonaName"/> / <see cref="AgentPresetConfig.PersonaRole"/>, which
/// adapters use to build the prompt and which never reach the driver row.
/// </remarks>
public static class DriverIdentity
{
    /// <summary>
    /// Neutral role for drivers (from Core, so the API and the runner cannot drift apart: this
    /// value is written straight onto the driver row by registration).
    /// </summary>
    public const string DefaultRole = AgentKind.DriverRole;

    /// <summary>
    /// The placeholder the API used before drivers stopped inheriting persona roles. Treated as
    /// "no role recorded" so a stale value cannot survive a re-registration.
    /// </summary>
    public const string LegacyPlaceholderRole = "Autonomous Agent";

    /// <summary>
    /// Applies the driver's authoritative identity to <paramref name="preset"/>.
    /// </summary>
    /// <param name="preset">The local preset about to be registered.</param>
    /// <param name="driverId">Normalised driver id the runner registers against.</param>
    /// <param name="remoteDriver">The driver row as the API knows it, or null if it could not be read.</param>
    /// <param name="personaRole">
    /// The role of the persona this runner hosts. A driver row whose role equals it was renamed by
    /// a previous persona runner, so it is not the driver's own identity.
    /// </param>
    public static void ApplyTo(
        AgentPresetConfig preset,
        string driverId,
        RemoteAgentDto? remoteDriver,
        string? personaRole = null)
    {
        preset.Name = ResolveName(remoteDriver?.Name, preset.Name, driverId);
        preset.Role = ResolveRole(remoteDriver?.Role, preset.Role, personaRole);
    }

    /// <summary>
    /// Prefers the server's name, then a locally-chosen name, and repairs names written by the old
    /// code path (which looked like <c>"Rook (@agy)"</c>).
    /// </summary>
    /// <remarks>
    /// The remote name is distrusted for the same reason as the local one: it was very likely
    /// written by a previous persona runner. Falling through to <paramref name="driverId"/> is what
    /// makes an already-polluted deployment heal itself on the next registration, with no migration.
    /// </remarks>
    internal static string ResolveName(string? remoteName, string? localName, string driverId)
    {
        var remote = remoteName?.Trim();
        if (!string.IsNullOrWhiteSpace(remote) && !LooksLikePersonaClone(remote!)) return remote!;

        var local = localName?.Trim();
        if (!string.IsNullOrWhiteSpace(local) && !LooksLikePersonaClone(local!)) return local!;

        return driverId;
    }

    /// <summary>
    /// Keeps a deliberate driver role, and replaces placeholders and values copied off a persona.
    /// </summary>
    internal static string ResolveRole(string? remoteRole, string? localRole, string? personaRole = null)
    {
        foreach (var candidate in new[] { remoteRole, localRole })
        {
            var value = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (IsPlaceholderOrClone(value!, personaRole)) continue;

            return value!;
        }

        return DefaultRole;
    }

    /// <summary>
    /// True when <paramref name="role"/> is a placeholder, a value the old persona-runner path
    /// wrote, or the hosted persona's own role.
    /// </summary>
    /// <remarks>
    /// A persona role cannot be recognised from its shape ("Software Engineer" is a perfectly good
    /// driver label in principle), which is why the runner passes the persona role it is hosting:
    /// equality against it is the only reliable signal that a driver row was overwritten.
    /// </remarks>
    internal static bool IsPlaceholderOrClone(string role, string? personaRole)
    {
        if (string.Equals(role, LegacyPlaceholderRole, StringComparison.OrdinalIgnoreCase)) return true;
        if (LooksLikePersonaClone(role)) return true;

        return !string.IsNullOrWhiteSpace(personaRole) &&
               string.Equals(role, personaRole.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for values the previous persona-runner code path produced, e.g. "Rook (@agy)".</summary>
    private static bool LooksLikePersonaClone(string value) => value.Contains("(@", StringComparison.Ordinal);
}
