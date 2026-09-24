using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Tax.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
