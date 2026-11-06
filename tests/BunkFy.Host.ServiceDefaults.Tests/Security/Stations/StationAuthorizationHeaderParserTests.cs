namespace BunkFy.Host.ServiceDefaults.Tests.Security.Stations;

using BunkFy.Host.ServiceDefaults.Security.Stations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StationAuthorizationHeaderParserTests
{
    [Fact]
    public void Missing_header_has_no_result()
    {
        StationAuthorizationHeader result = StationAuthorizationHeaderParser.Parse(new HeaderDictionary());

        Assert.Equal(StationAuthorizationHeaderKind.Missing, result.Kind);
        Assert.Equal(0, result.FieldValueCount);
        Assert.Empty(result.Lengths);
        Assert.Null(result.Token);
    }

    [Fact]
    public void One_well_formed_non_station_scheme_has_no_station_result()
    {
        HeaderDictionary headers = new()
        {
            [HeaderNames.Authorization] = "Bearer ordinary.jwt.value"
        };

        StationAuthorizationHeader result = StationAuthorizationHeaderParser.Parse(headers);

        Assert.Equal(StationAuthorizationHeaderKind.NonStation, result.Kind);
        Assert.Null(result.Token);
    }

    [Theory]
    [InlineData("Station abc.def_ghi-jkl")]
    [InlineData("station abc.def_ghi-jkl")]
    [InlineData("STATION abc.def_ghi-jkl")]
    public void Station_scheme_is_case_insensitive_but_token_is_bounded(string value)
    {
        HeaderDictionary headers = new()
        {
            [HeaderNames.Authorization] = value
        };

        StationAuthorizationHeader result = StationAuthorizationHeaderParser.Parse(headers);

        Assert.Equal(StationAuthorizationHeaderKind.Station, result.Kind);
        Assert.Equal("abc.def_ghi-jkl", result.Token);
    }

    [Fact]
    public void Multiple_field_values_are_rejected_without_first_or_last_wins()
    {
        HeaderDictionary headers = new()
        {
            [HeaderNames.Authorization] = new StringValues(
                ["Station abc.def.ghi", "Bearer ordinary.jwt.value"])
        };

        StationAuthorizationHeader result = StationAuthorizationHeaderParser.Parse(headers);

        Assert.Equal(StationAuthorizationHeaderKind.Malformed, result.Kind);
        Assert.Equal("station_header_multiple", result.Category);
        Assert.Equal(2, result.FieldValueCount);
        Assert.Equal([19, 25], result.Lengths);
        Assert.Null(result.Token);
    }

    [Theory]
    [InlineData("Station abc.def.ghi,Bearer other")]
    [InlineData("Station  abc.def.ghi")]
    [InlineData("Station abc def.ghi")]
    [InlineData("Station abc.def.+ghi")]
    [InlineData("Station\tabc.def.ghi")]
    public void Folded_whitespace_control_and_non_jws_material_is_rejected(string value)
    {
        HeaderDictionary headers = new()
        {
            [HeaderNames.Authorization] = value
        };

        StationAuthorizationHeader result = StationAuthorizationHeaderParser.Parse(headers);

        Assert.Equal(StationAuthorizationHeaderKind.Malformed, result.Kind);
        Assert.Null(result.Token);
    }

    [Fact]
    public void Token_length_is_rejected_before_cryptographic_validation()
    {
        HeaderDictionary maximumHeaders = new()
        {
            [HeaderNames.Authorization] = $"Station {new string('a', 4096)}"
        };
        HeaderDictionary oversizedHeaders = new()
        {
            [HeaderNames.Authorization] = $"Station {new string('a', 4097)}"
        };

        StationAuthorizationHeader maximum = StationAuthorizationHeaderParser.Parse(maximumHeaders);
        StationAuthorizationHeader oversized = StationAuthorizationHeaderParser.Parse(oversizedHeaders);

        Assert.Equal(StationAuthorizationHeaderKind.Station, maximum.Kind);
        Assert.Equal(StationAuthorizationHeaderKind.Malformed, oversized.Kind);
        Assert.Equal("station_token_length_invalid", oversized.Category);
        Assert.Null(oversized.Token);
    }
}
