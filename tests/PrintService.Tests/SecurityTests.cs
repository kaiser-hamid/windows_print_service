using System.Net;

namespace PrintService.Tests;

public class SecurityTests
{
    [Fact]
    public async Task Request_without_api_key_is_rejected()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateClient().GetAsync("/api/v1/printers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_with_wrong_api_key_is_rejected()
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        var response = await client.GetAsync("/api/v1/printers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_with_static_api_key_is_accepted()
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "epichealthcare@2026printserver");
        var response = await client.GetAsync("/api/v1/printers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://192.168.1.10:3000")]
    [InlineData("null")]
    public async Task Foreign_origin_is_forbidden_even_with_valid_key(string origin)
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add("Origin", origin);
        var response = await client.GetAsync("/api/v1/printers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:8080")]
    public async Task Localhost_origin_gets_cors_headers(string origin)
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add("Origin", origin);
        var response = await client.GetAsync("/api/v1/printers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Unauthorized_response_still_carries_cors_headers_for_localhost()
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");
        var response = await client.GetAsync("/api/v1/printers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_from_localhost_is_answered_without_key()
    {
        using var factory = new PrintServiceFactory();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/print/pdf");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-api-key,x-printer-name,content-type");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:3000", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        Assert.Contains("x-api-key", allowedHeaders);
        Assert.Contains("x-printer-name", allowedHeaders);
        Assert.Contains("content-type", allowedHeaders);
    }

    [Fact]
    public async Task Preflight_from_foreign_origin_is_forbidden()
    {
        using var factory = new PrintServiceFactory();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/print/pdf");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
