using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMesh.Tests.Data;

public class AgentMeshDbContextTests
{
    [Fact]
    public void AgentMeshDbContext_WhenPooled_DoesNotThrowOnResolveOrIsRelational()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<AgentMeshDbContext>(options =>
        {
            options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        });

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        // Under EF Core with pooling, any OnConfiguring call modifying optionsBuilder
        // when optionsBuilder.IsConfigured is true throws InvalidOperationException.
        var act = () => db.Database.IsRelational();
        act.Should().NotThrow();
    }
}
