using AgentMesh.Cli.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Cli.Adapters;

/// <summary>
/// Implemented by adapters that can report what their own driver actually supports.
/// </summary>
/// <remarks>
/// <para>
/// This is option C of the capability-discovery design. The runner is the process that invokes the
/// binary, so it is the only component positioned to know what that binary accepts and what the
/// account behind it can see.
/// </para>
/// <para>
/// The reason it is not simply "the API asks harder": in a Docker/Kubernetes deployment the API
/// image (<c>deploy/docker/Dockerfile.api</c>, bare <c>aspnet:10.0</c>) has no driver binaries at
/// all, so a request-path probe of <c>agy</c> can never succeed there no matter how healthy the
/// runners are. The information only exists next to the binary.
/// </para>
/// <para>
/// Deliberately an opt-in marker rather than a member of <see cref="IAgentAdapter"/>: adapters
/// with no discovery surface (<c>claude</c>, <c>copilot</c>, <c>generic</c>) must not be pushed
/// into inventing an answer to satisfy an interface. They return no declaration and the API keeps
/// doing what it does today for them.
/// </para>
/// </remarks>
public interface IDescribesOwnCapabilities
{
    /// <summary>
    /// Reads the driver's real capabilities from the driver itself.
    /// </summary>
    /// <remarks>
    /// Never returns null and must never return a plausible default. Opting into this interface is
    /// a promise to always have something to say: either the models the driver reported, or an
    /// empty list with an <c>UnavailableReason</c> explaining why there are none. A fabricated entry
    /// is worse than an empty list with a reason, because the declaration is rendered as a picker
    /// the operator then trusts.
    /// <para>
    /// The null that callers see comes from <em>not implementing this interface</em>, not from a
    /// null return - see <c>MeshClient.DescribeCapabilitiesAsync</c>.
    /// </para>
    /// </remarks>
    Task<DriverCapabilityDeclaration> DescribeCapabilitiesAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default);
}
