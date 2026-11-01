using Xunit;

namespace AgentMesh.Tests;

/// <summary>
/// Marker for the collection that owns the single PostgreSQL scratch database.
/// </summary>
/// <remarks>
/// Every class that connects through <see cref="PostgresFactAttribute"/> points at the same database
/// named by <c>AGENTMESH_TEST_POSTGRES_CONNECTION</c>, and each one resets it to a known state with
/// <c>EnsureDeletedAsync()</c> in its own fixture setup. xUnit runs distinct test <em>collections</em>
/// in parallel, and a class with no <c>[Collection]</c> attribute is its own collection — so two such
/// classes would drop each other's database mid-test. The failure would be attributed to whichever
/// class lost, would depend on core count and ordering, and would only appear on a machine that has a
/// PostgreSQL to point at, which is precisely the configuration nobody runs locally.
/// </remarks>
public static class PostgresScratchCollection
{
    /// <summary>The collection name every scratch-database class shares.</summary>
    public const string Name = "PostgreSQL scratch database";
}

/// <summary>
/// Serializes every class that touches the scratch database into one non-parallel collection.
/// </summary>
/// <remarks>
/// Applying this to a class is a constraint on the class, not a suggestion: within a collection xUnit
/// still parallelizes nothing, so the classes below run one after another. That is the cost of sharing
/// one mutable database, and it is cheaper than the alternative of one database per class, which the
/// attribute's contract does not support — the connection string names a single database that the
/// operator has to have created and granted.
/// </remarks>
[CollectionDefinition(PostgresScratchCollection.Name)]
public class PostgresScratchCollectionDefinition
{
    // No ICollectionFixture is declared on purpose: each class in the collection builds and disposes
    // its own DbContext, because they need different migration heads and one shared context would
    // carry the first class's schema state into the next. The CollectionDefinition exists only to put
    // them in the same non-parallel collection.
}
