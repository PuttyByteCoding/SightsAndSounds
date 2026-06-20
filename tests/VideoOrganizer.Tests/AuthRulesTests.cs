using VideoOrganizer.API.Auth;
using Xunit;

namespace VideoOrganizer.Tests;

public class AuthRulesTests
{
    [Theory]
    [InlineData("GET", "/api/videos/count")]
    [InlineData("HEAD", "/api/videos/count")]
    [InlineData("OPTIONS", "/api/videos/count")]
    public void Reads_are_not_writes(string method, string path)
        => Assert.False(AuthRules.IsWriteRequest(method, path));

    [Theory]
    [InlineData("/api/videos/filter")]
    [InlineData("/api/videos/filter-page")]
    [InlineData("/api/playlists/random")]
    [InlineData("/api/playlists/even")]
    public void Allowlisted_query_posts_are_reads(string path)
        => Assert.False(AuthRules.IsWriteRequest("POST", path));

    [Theory]
    [InlineData("POST", "/api/videos/abc/mark-favorite")]
    [InlineData("POST", "/api/tags")]
    [InlineData("PUT", "/api/videos/abc/tags")]
    [InlineData("DELETE", "/api/video-sets/abc")]
    [InlineData("PATCH", "/api/anything")]
    public void Mutations_are_writes(string method, string path)
        => Assert.True(AuthRules.IsWriteRequest(method, path));

    [Fact]
    public void Unknown_post_fails_closed_as_a_write()
        => Assert.True(AuthRules.IsWriteRequest("POST", "/api/some/new/endpoint"));

    [Fact]
    public void Allowlist_match_is_case_insensitive()
        => Assert.False(AuthRules.IsWriteRequest("post", "/API/Videos/Filter"));
}
