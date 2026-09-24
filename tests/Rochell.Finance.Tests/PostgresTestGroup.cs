using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Finance.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
