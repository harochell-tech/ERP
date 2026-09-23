using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Migrations.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
