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
}
