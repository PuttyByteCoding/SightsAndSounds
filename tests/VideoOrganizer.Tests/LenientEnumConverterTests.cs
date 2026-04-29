using System.Text.Json;
using VideoOrganizer.API;
using Xunit;

namespace VideoOrganizer.Tests;

public class LenientEnumConverterTests
{
    // --- ToWireCamelCase ---------------------------------------------------
    //
    // These cases are the ones the converter's own doc-comment calls out as
    // tricky. The function decides where the leading all-caps "prefix" ends
    // and the next word begins, so each case is a distinct branch.

    [Theory]
    [InlineData("H264", "h264")]                  // 1 upper then digit
    [InlineData("HEVC", "hevc")]                  // entirely upper
    [InlineData("UHD8k", "uhd8k")]                // upper run, then digit, then lowercase
    [InlineData("HD1080p", "hd1080p")]            // upper run + digits + lowercase
    [InlineData("XMLParser", "xmlParser")]        // upper run, last upper starts next word
    [InlineData("CellPhone", "cellPhone")]        // single leading upper
    [InlineData("Other", "other")]                // single-letter upper run
    [InlineData("", "")]                          // empty stays empty
    [InlineData("alreadyCamel", "alreadyCamel")]  // first char lower → unchanged
    [InlineData("a", "a")]
    public void ToWireCamelCase_HandlesEdgeCases(string input, string expected)
    {
        Assert.Equal(expected, LenientEnumConverter<TestEnum>.ToWireCamelCase(input));
    }

    // --- JSON read --------------------------------------------------------

    [Theory]
    [InlineData("\"Alpha\"", TestEnum.Alpha)]
    [InlineData("\"alpha\"", TestEnum.Alpha)]      // case-insensitive
    [InlineData("\"ALPHA\"", TestEnum.Alpha)]
    [InlineData("\"Beta\"", TestEnum.Beta)]
    public void Read_ValidString_ParsesCaseInsensitively(string json, TestEnum expected)
    {
        Assert.Equal(expected, Deserialize(json));
    }

    [Theory]
    [InlineData("0", TestEnum.Alpha)]
    [InlineData("1", TestEnum.Beta)]
    [InlineData("2", TestEnum.Gamma)]
    public void Read_NumericInRange_ReturnsEnum(string json, TestEnum expected)
    {
        Assert.Equal(expected, Deserialize(json));
    }

    [Fact]
    public void Read_UnknownString_FallsBackToDefault()
    {
        // Lenient by design: bad input should not throw and crash the
        // request — it returns default(TEnum) so the caller can still
        // process the rest of the payload.
        Assert.Equal(default(TestEnum), Deserialize("\"NotARealMember\""));
    }

    [Fact]
    public void Read_EmptyString_ReturnsDefault()
    {
        Assert.Equal(default(TestEnum), Deserialize("\"\""));
    }

    [Fact]
    public void Read_NumericOutOfRange_StillCastsToEnum()
    {
        // The implementation deliberately casts unknown numeric values
        // through, so the consumer sees what was sent. This is brittle
        // by design — pin it so a future "validate first" refactor
        // doesn't silently change the API contract.
        var result = Deserialize("999");
        Assert.Equal((TestEnum)999, result);
    }

    // --- JSON write -------------------------------------------------------

    [Theory]
    [InlineData(TestEnum.Alpha, "\"alpha\"")]
    [InlineData(TestEnum.Beta, "\"beta\"")]
    [InlineData(TestEnum.Gamma, "\"gamma\"")]
    public void Write_DefinedEnum_ProducesCamelCaseString(TestEnum value, string expected)
    {
        Assert.Equal(expected, Serialize(value));
    }

    [Fact]
    public void Write_UndefinedEnum_FallsBackToNumericLiteral()
    {
        // Round-trip safety: if Read accepted 999 and stored (TestEnum)999,
        // Write must emit it as a number rather than throwing or losing
        // data. Without this, a server that received unknown numeric input
        // would crash on the way out.
        Assert.Equal("999", Serialize((TestEnum)999));
    }

    // --- helpers ----------------------------------------------------------

    public enum TestEnum
    {
        Alpha,
        Beta,
        Gamma
    }

    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new LenientEnumConverterFactory() }
    };

    private static TestEnum Deserialize(string json)
        => JsonSerializer.Deserialize<TestEnum>(json, _options);

    private static string Serialize(TestEnum value)
        => JsonSerializer.Serialize(value, _options);
}
