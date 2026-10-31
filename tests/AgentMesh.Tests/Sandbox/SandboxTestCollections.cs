namespace AgentMesh.Tests.Sandbox;

/// <summary>
/// Collection name shared by every test that exercises log spillover.
/// </summary>
/// <remarks>
/// xUnit parallelises across test *collections* (one per class by default). The spillover engine writes a
/// per-run file plus a single shared <c>last_run.log</c> pointer, and the pointer is exactly what the
/// "did we preserve the whole log?" assertions read. Two classes running concurrently would each overwrite
/// the other's <c>last_run.log</c>, producing a failure that looks like a broken spillover engine but is
/// really two tests fighting over one path.
///
/// Serialising the spillover tests costs a few seconds of wall clock. The alternative — a per-test runs
/// directory — would mean never exercising the shared <c>last_run.log</c> contract that the job spec asks
/// for, which is the more expensive trade.
/// </remarks>
public static class SandboxTestCollections
{
    public const string Spillover = "SandboxSpillover";

    /// <summary>
    /// Collection name shared by every test that boots a Temporal test server.
    /// </summary>
    /// <remarks>
    /// <see cref="Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync"/> launches a
    /// <c>temporal-test-server</c> subprocess and reserves an ephemeral port. xUnit parallelises across
    /// collections, so N classes in N collections would each start their own server; on a small CI
    /// container that is N processes polling the same host for ports, and the failure mode is a
    /// server that fails to bind and a test that reports "Timeout expired" against code that is fine.
    ///
    /// Serialising costs wall clock (each server takes a second or two to come up). The alternative —
    /// sharing one environment across classes — would require a fixture whose lifetime outlives the
    /// collection and would couple every workflow test to the first one's shutdown behaviour.
    /// </remarks>
    public const string Temporal = "TemporalWorkflowEnvironment";
}
