using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EpicErp.PrintService;
using EpicErp.PrintService.Printing;

namespace PrintService.Tests;

public class SampleLabelApiTests
{
    private const string Url = "/api/v1/print-sample-label";

    // Two 38 x 25 mm labels in one job, CRLF line endings, as the ERP backend generates them.
    private static readonly byte[] Ezpl = Encoding.ASCII.GetBytes(
        "^Q25,3\r\n^W38\r\n^H10\r\n^P1\r\n^S3\r\n^AD\r\n^C1\r\n^R0\r\n~Q+0\r\n^O0\r\n^D0\r\n^E12\r\n~R255\r\n^L\r\n" +
        "Dy2-me-dd\r\nTh:m:s\r\nBQ,20,20,2,5,60,0,1,S2610310001\r\nAB,20,100,1,1,0,0E,S2610310001\r\nE\r\n" +
        "^L\r\nBQ,20,20,2,5,60,0,1,S2610310002\r\nAB,20,100,1,1,0,0E,S2610310002\r\nE\r\n");

    private static ByteArrayContent Raw(byte[] bytes, string? contentType = "text/plain; charset=utf-8")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    [Fact]
    public async Task Label_data_is_passed_to_label_print_byte_for_byte()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient()
            .PostAsync($"{Url}?jobName=Sample%20labels%20S2610310001", Raw(Ezpl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("label_print", json.RootElement.GetProperty("printer").GetString());
        Assert.Equal("Sample labels S2610310001", json.RootElement.GetProperty("jobName").GetString());
        Assert.Equal(Ezpl.Length, json.RootElement.GetProperty("bytes").GetInt32());

        var sent = Assert.Single(factory.RawPrinter.Requests);
        Assert.Equal(ServiceConstants.SampleLabelPrinter, sent.PrinterName);
        Assert.Equal(Ezpl, sent.Data); // same bytes, CRLF kept, nothing appended
    }

    [Fact]
    public async Task Surrounding_whitespace_bom_and_line_endings_are_not_touched()
    {
        // Leading/trailing whitespace, a UTF-8 BOM, mixed LF/CRLF and a non-ASCII byte must all survive.
        var body = new byte[] { 0xEF, 0xBB, 0xBF, (byte)' ', (byte)'^', (byte)'L', (byte)'\n', (byte)'E', (byte)'\r', (byte)'\n', 0xE9, (byte)'\r', (byte)'\n', (byte)' ' };
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, Assert.Single(factory.RawPrinter.Requests).Data);
    }

    [Fact]
    public async Task Octet_stream_is_accepted_and_job_name_defaults()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Ezpl, "application/octet-stream"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(factory.RawPrinter.Requests);
        Assert.Equal("Sample Labels", sent.JobName);
        Assert.Equal(Ezpl, sent.Data);
    }

    [Fact]
    public async Task Job_name_is_trimmed_and_capped_at_128_chars()
    {
        using var factory = new PrintServiceFactory();
        var longName = new string('x', 200);
        var client = factory.CreateAuthorizedClient();

        await client.PostAsync($"{Url}?jobName=%20%20Batch%207%20%20", Raw(Ezpl));
        await client.PostAsync($"{Url}?jobName={longName}", Raw(Ezpl));

        Assert.Equal("Batch 7", factory.RawPrinter.Requests[0].JobName);
        Assert.Equal(128, factory.RawPrinter.Requests[1].JobName.Length);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("TEXT/PLAIN; charset=us-ascii")]
    [InlineData("application/octet-stream")]
    public async Task Accepted_content_types(string contentType)
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Ezpl, contentType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/json")]
    [InlineData("text/html")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData(null)]
    public async Task Other_content_types_return_415(string? contentType)
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Ezpl, contentType));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(factory.RawPrinter.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData(" \t\r\n ")]
    public async Task Empty_or_whitespace_body_returns_400(string body)
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Encoding.ASCII.GetBytes(body)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Empty body", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.RawPrinter.Requests);
    }

    [Fact]
    public async Task Body_of_exactly_1_mb_is_accepted()
    {
        using var factory = new PrintServiceFactory();
        var data = new byte[ServiceConstants.MaxRawSizeBytes];
        Array.Fill(data, (byte)'A');

        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(data));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(data, Assert.Single(factory.RawPrinter.Requests).Data);
    }

    [Fact]
    public async Task Body_over_1_mb_returns_413_from_content_length()
    {
        using var factory = new PrintServiceFactory();
        var data = new byte[ServiceConstants.MaxRawSizeBytes + 1];
        Array.Fill(data, (byte)'A');

        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(data));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Label data too large", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.RawPrinter.Requests);
    }

    [Fact]
    public async Task Body_over_1_mb_without_content_length_returns_413_while_reading()
    {
        using var factory = new PrintServiceFactory();
        var data = new byte[ServiceConstants.MaxRawSizeBytes + 1];
        Array.Fill(data, (byte)'A');
        var content = new UnknownLengthContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var response = await factory.CreateAuthorizedClient().PostAsync(Url, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.RawPrinter.Requests);
    }

    [Fact]
    public async Task Missing_label_printer_returns_503()
    {
        using var factory = new PrintServiceFactory();
        factory.RawPrinter.ThrowOnPrint = new PrinterUnavailableException("Printer 'label_print' is not installed on this PC.");

        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Ezpl));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Printer unavailable", body);
        Assert.Contains("label_print", body);
    }

    [Fact]
    public async Task Unexpected_error_returns_500()
    {
        using var factory = new PrintServiceFactory();
        factory.RawPrinter.ThrowOnPrint = new InvalidOperationException("boom");

        var response = await factory.CreateAuthorizedClient().PostAsync(Url, Raw(Ezpl));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Print failed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Copies_parameter_is_ignored()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateAuthorizedClient().PostAsync($"{Url}?copies=5", Raw(Ezpl));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Ezpl, Assert.Single(factory.RawPrinter.Requests).Data);
    }

    [Fact]
    public async Task Requires_api_key()
    {
        using var factory = new PrintServiceFactory();
        var response = await factory.CreateClient().PostAsync(Url, Raw(Ezpl));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.RawPrinter.Requests);
    }

    [Fact]
    public async Task Foreign_origin_is_forbidden()
    {
        using var factory = new PrintServiceFactory();
        var client = factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example.com");

        var response = await client.PostAsync(Url, Raw(Ezpl));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.RawPrinter.Requests);
    }

    /// <summary>Sends the body without a Content-Length header (chunked), like a streamed upload.</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
