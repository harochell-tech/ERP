using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Platform.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
