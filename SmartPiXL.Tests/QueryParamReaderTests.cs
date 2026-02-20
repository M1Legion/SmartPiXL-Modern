using FluentAssertions;
using SmartPiXL.Forge.Services.Enrichments;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for QueryParamReader — fast query string parameter extraction
/// used by Forge enrichment services.
/// </summary>
public sealed class QueryParamReaderTests
{
    // ========================================================================
    // GET — Basic retrieval
    // ========================================================================

    [Fact]
    public void Get_should_returnValue_when_paramExists()
    {
        var qs = "sw=1920&sh=1080&gpu=RTX+4090";
        QueryParamReader.Get(qs, "sw").Should().Be("1920");
        QueryParamReader.Get(qs, "sh").Should().Be("1080");
    }

    [Fact]
    public void Get_should_returnNull_when_paramMissing()
    {
        var qs = "sw=1920&sh=1080";
        QueryParamReader.Get(qs, "gpu").Should().BeNull();
    }

    [Fact]
    public void Get_should_returnNull_when_queryStringNull()
    {
        QueryParamReader.Get(null, "sw").Should().BeNull();
    }

    [Fact]
    public void Get_should_returnNull_when_queryStringEmpty()
    {
        QueryParamReader.Get("", "sw").Should().BeNull();
    }

    [Fact]
    public void Get_should_returnNull_when_paramNameNull()
    {
        QueryParamReader.Get("sw=1920", null!).Should().BeNull();
    }

    [Fact]
    public void Get_should_returnNull_when_paramNameEmpty()
    {
        QueryParamReader.Get("sw=1920", "").Should().BeNull();
    }

    // ========================================================================
    // GET — First param in query string
    // ========================================================================

    [Fact]
    public void Get_should_findFirstParam()
    {
        var qs = "gpu=RTX+4090&sw=1920&sh=1080";
        QueryParamReader.Get(qs, "gpu").Should().Be("RTX 4090"); // + decoded to space
    }

    // ========================================================================
    // GET — Last param in query string
    // ========================================================================

    [Fact]
    public void Get_should_findLastParam()
    {
        var qs = "sw=1920&sh=1080&gpu=RTX+4090";
        QueryParamReader.Get(qs, "gpu").Should().Be("RTX 4090");
    }

    // ========================================================================
    // GET — URL-encoded values
    // ========================================================================

    [Fact]
    public void Get_should_decodeUrlEncodedValues()
    {
        var qs = "gpu=NVIDIA%20GeForce%20RTX%204090&sw=1920";
        QueryParamReader.Get(qs, "gpu").Should().Be("NVIDIA GeForce RTX 4090");
    }

    // ========================================================================
    // GET — Case insensitive matching
    // ========================================================================

    [Fact]
    public void Get_should_beCaseInsensitive()
    {
        var qs = "SW=1920&SH=1080";
        QueryParamReader.Get(qs, "sw").Should().Be("1920");
        QueryParamReader.Get(qs, "sh").Should().Be("1080");
    }

    // ========================================================================
    // GET — Partial name match should NOT match
    // ========================================================================

    [Fact]
    public void Get_should_notMatch_partialNames()
    {
        var qs = "_srv_subnetHits=5&_srv_subnet=192.168";
        // Asking for "_srv_subnet" should not return the _srv_subnetHits value
        QueryParamReader.Get(qs, "_srv_subnet").Should().Be("192.168");
    }

    // ========================================================================
    // GET — Empty value
    // ========================================================================

    [Fact]
    public void Get_should_returnEmpty_when_emptyValue()
    {
        var qs = "gpu=&sw=1920";
        QueryParamReader.Get(qs, "gpu").Should().Be("");
    }

    // ========================================================================
    // GET — _srv_* params (Forge enrichment params)
    // ========================================================================

    [Fact]
    public void Get_should_find_srvParams()
    {
        var qs = "sw=1920&_srv_crossCustHits=5&_srv_crossCustAlert=1&_srv_leadScore=85";
        QueryParamReader.Get(qs, "_srv_crossCustHits").Should().Be("5");
        QueryParamReader.Get(qs, "_srv_crossCustAlert").Should().Be("1");
        QueryParamReader.Get(qs, "_srv_leadScore").Should().Be("85");
    }

    // ========================================================================
    // GETINT — Integer parsing
    // ========================================================================

    [Fact]
    public void GetInt_should_returnInt_when_valid()
    {
        QueryParamReader.GetInt("cores=16&mem=32", "cores").Should().Be(16);
        QueryParamReader.GetInt("cores=16&mem=32", "mem").Should().Be(32);
    }

    [Fact]
    public void GetInt_should_return0_when_missing()
    {
        QueryParamReader.GetInt("sw=1920", "cores").Should().Be(0);
    }

    [Fact]
    public void GetInt_should_return0_when_notParseable()
    {
        QueryParamReader.GetInt("cores=abc", "cores").Should().Be(0);
    }

    [Fact]
    public void GetInt_should_return0_when_nullQueryString()
    {
        QueryParamReader.GetInt(null, "cores").Should().Be(0);
    }

    // ========================================================================
    // GETDOUBLE — Double parsing with InvariantCulture
    // ========================================================================

    [Fact]
    public void GetDouble_should_returnDouble_when_valid()
    {
        QueryParamReader.GetDouble("mouseEntropy=3.75", "mouseEntropy").Should().BeApproximately(3.75, 0.001);
    }

    [Fact]
    public void GetDouble_should_return0_when_missing()
    {
        QueryParamReader.GetDouble("sw=1920", "mouseEntropy").Should().Be(0.0);
    }

    [Fact]
    public void GetDouble_should_return0_when_notParseable()
    {
        QueryParamReader.GetDouble("mouseEntropy=abc", "mouseEntropy").Should().Be(0.0);
    }

    // ========================================================================
    // GETBOOL — "1" = true, anything else = false
    // ========================================================================

    [Fact]
    public void GetBool_should_returnTrue_when_value1()
    {
        QueryParamReader.GetBool("fpUniq=1&canvasNoise=0", "fpUniq").Should().BeTrue();
    }

    [Fact]
    public void GetBool_should_returnFalse_when_value0()
    {
        QueryParamReader.GetBool("fpUniq=1&canvasNoise=0", "canvasNoise").Should().BeFalse();
    }

    [Fact]
    public void GetBool_should_returnFalse_when_missing()
    {
        QueryParamReader.GetBool("sw=1920", "fpUniq").Should().BeFalse();
    }

    [Fact]
    public void GetBool_should_returnFalse_when_valueTrue()
    {
        // Only "1" is truthy, not "true"
        QueryParamReader.GetBool("fpUniq=true", "fpUniq").Should().BeFalse();
    }
}
