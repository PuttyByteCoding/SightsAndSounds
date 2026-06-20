using VideoOrganizer.API.Auth;
using Xunit;

namespace VideoOrganizer.Tests;

public class KeycloakRolesTests
{
    [Fact]
    public void Extracts_realm_roles()
    {
        var roles = KeycloakRoles.Extract("{\"roles\":[\"admin\",\"viewer\"]}");
        Assert.Equal(new[] { "admin", "viewer" }, roles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"roles\":\"notarray\"}")]
    [InlineData("[\"admin\"]")]
    public void Returns_empty_on_missing_or_malformed(string? input)
        => Assert.Empty(KeycloakRoles.Extract(input));

    [Fact]
    public void Skips_non_string_and_empty_role_entries()
    {
        var roles = KeycloakRoles.Extract("{\"roles\":[\"admin\", \"\", 5, null]}");
        Assert.Equal(new[] { "admin" }, roles);
    }
}
