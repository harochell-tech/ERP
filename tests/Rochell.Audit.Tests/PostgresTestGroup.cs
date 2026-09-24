using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Audit.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
