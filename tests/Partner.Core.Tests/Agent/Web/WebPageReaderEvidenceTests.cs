using System.Security.Cryptography;
using System.Text;
using Partner.Core.Agent.Web;

namespace Partner.Core.Tests.Agent.Web;

public sealed class WebPageReaderEvidenceTests
{
    [Fact]
    public void ConvertResponse_RecordsOnlyEligibleHttpStatusObservations()
    {
        var uri = new Uri("https://example.com:8443/health#section");
        var final = new Uri("https://example.com:8443/health");
        var response = new WebResponse(503, "failure", final, "text/plain", null);
        var failure = WebPageReader.ConvertResponse(uri, response, 0, 100);
        var success = WebPageReader.ConvertResponse(uri, response with { Status = 204, Body = "" }, 0, 100);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("https://example.com:8443/health"))).ToLowerInvariant();

        Assert.False(failure.Ok);
        Assert.Equal(expected, failure.CheckEvidence?.Target);
        Assert.Equal(503, failure.CheckEvidence?.Status);
        Assert.False(success.Ok); // An empty body still proves the HTTP status, not page correctness.
        Assert.Equal(expected, success.CheckEvidence?.Target);
        Assert.Equal(204, success.CheckEvidence?.Status);

        var redirected = WebPageReader.ConvertResponse(uri, response with { RedirectCount = 1 }, 0, 100);
        Assert.Null(redirected.CheckEvidence);
        Assert.Equal(failure.Content, redirected.Content);
        Assert.Null(WebPageReader.ConvertResponse(uri, response with { Error = "unreachable: read failed" }, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(uri, response with { Status = 302 }, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(uri, response with { Status = 404 }, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(uri, response with { FinalUri = new Uri("https://example.com:8443/other") }, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(uri, response with { FinalUri = null }, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(new Uri("https://example.com/health?token=secret"), response, 0, 100).CheckEvidence);
        Assert.Null(WebPageReader.ConvertResponse(new Uri("https://u:p@example.com/health"), response, 0, 100).CheckEvidence);

        var other = new Uri("https://example.com/health");
        var otherResult = WebPageReader.ConvertResponse(other, response with { FinalUri = other }, 0, 100);
        Assert.NotEqual(expected, otherResult.CheckEvidence?.Target);
    }
}
