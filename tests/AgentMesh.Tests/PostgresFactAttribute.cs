using Xunit;

namespace AgentMesh.Tests;

/// <summary>
/// A test that needs a real PostgreSQL instance, skipped when none is configured.
/// </summary>
/// <remarks>
/// <para>
/// Some guarantees cannot be produced by the in-memory provider the rest of this assembly runs on: it
/// enforces no unique indexes, raises no <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>,
/// and translates every LINQ query regardless of whether a relational store could. A test that asserts one
/// of those behaviours against it passes whether or not the production code does, which is worse than no
/// test at all — it retires the concern that motivated writing it.
/// </para>
/// <para>
/// Skipping rather than failing when the store is absent is the trade that keeps this suite runnable
/// anywhere: the alternative blocks every CI job on a machine without a database, and tests that block
/// get deleted rather than provisioned. The skip is reported by name, so the gap is visible in the run
/// output instead of being silently absent.
/// </para>
/// <para>
/// The decision is made in the constructor because xUnit reads <see cref="FactAttribute.Skip"/> at
/// discovery time, which runs in-process after the environment is populated. Setting it later has no
/// effect: by then the runner has already classified the test.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Environment variable naming the scratch database to run against.</summary>
    public const string ConnectionVariable = "AGENTMESH_TEST_POSTGRES_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"requires a PostgreSQL instance via ${ConnectionVariable}; " +
                   $"e.g. {ConnectionVariable}=\"Host=127.0.0.1;Database=agentmesh_memorytest;Username=postgres;Password=postgres\" dotnet test";
        }
    }
}
