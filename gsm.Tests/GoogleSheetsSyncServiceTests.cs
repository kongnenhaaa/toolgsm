using System.Net;
using System.Text;
using System.Text.Json;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class GoogleSheetsSyncServiceTests
{
    private const string SpreadsheetId =
        "1AbCdEfGhIjKlMnOpQrStUvWxYz123456789";

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz123456789/edit")]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz123456789/edit#gid=12")]
    public void ExtractSpreadsheetId_AcceptsGoogleSheetLinks(string url)
    {
        bool success = GoogleSheetsSyncService.TryExtractSpreadsheetId(
            url,
            out string id);

        Assert.True(success);
        Assert.Equal(SpreadsheetId, id);
    }

    [Theory]
    [InlineData("https://example.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz123456789/edit")]
    [InlineData("https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz123456789/edit")]
    [InlineData("")]
    public void ExtractSpreadsheetId_RejectsNonSheetLinks(string url)
    {
        Assert.False(GoogleSheetsSyncService.TryExtractSpreadsheetId(url, out _));
    }

    [Fact]
    public async Task SyncAsync_PostsRowsAndUsesGatewayResult()
    {
        var handler = new RecordingHandler(
            "{\"ok\":true,\"rows\":2,\"message\":\"Đã ghi 2 dòng.\"}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);
        var target = new GoogleSheetTargetSettings
        {
            Name = "Kho 1",
            SpreadsheetUrl =
                $"https://docs.google.com/spreadsheets/d/{SpreadsheetId}/edit",
            WorksheetName = "Du lieu"
        };
        GoogleSheetSyncColumn[] columns =
        [
            new("PortName", "Cổng"),
            new("Imei", "IMEI")
        ];
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            new[] { "COM3", "123456789012345" },
            new[] { "COM4", "987654321098765" }
        ];

        GoogleSheetSyncResult result = await service.SyncAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            target,
            columns,
            rows,
            "May-GSM-1");

        Assert.True(result.Success);
        Assert.Equal(2, result.RowCount);
        Assert.Equal("Đã ghi 2 dòng.", result.Message);
        Assert.Equal(
            "https://script.google.com/macros/s/test-deployment/exec",
            handler.RequestUri?.ToString());

        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement root = document.RootElement;
        Assert.Equal("shared-secret", root.GetProperty("secret").GetString());
        Assert.Equal(SpreadsheetId, root.GetProperty("spreadsheetId").GetString());
        Assert.Equal("Du lieu", root.GetProperty("worksheetName").GetString());
        Assert.Equal("May-GSM-1", root.GetProperty("machineId").GetString());
        Assert.Equal(2, root.GetProperty("rows").GetArrayLength());
        Assert.Equal(
            "987654321098765",
            root.GetProperty("rows")[1][1].GetString());
    }

    [Fact]
    public async Task SyncAsync_InvalidSheetDoesNotSendHttpRequest()
    {
        var handler = new RecordingHandler("{\"ok\":true}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.SyncAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            new GoogleSheetTargetSettings
            {
                Name = "Sai link",
                SpreadsheetUrl = "https://example.com/not-a-sheet"
            },
            [new GoogleSheetSyncColumn("Imei", "IMEI")],
            [new[] { "123456789012345" }],
            "May-GSM-1");

        Assert.False(result.Success);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SyncAsync_LoginHtmlIsNotReportedAsSuccess()
    {
        var handler = new RecordingHandler("<html>Google sign in</html>");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.SyncAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            new GoogleSheetTargetSettings
            {
                Name = "Kho 1",
                SpreadsheetUrl =
                    $"https://docs.google.com/spreadsheets/d/{SpreadsheetId}/edit"
            },
            [new GoogleSheetSyncColumn("Imei", "IMEI")],
            [new[] { "123456789012345" }],
            "May-GSM-1");

        Assert.False(result.Success);
        Assert.Contains("quyền triển khai", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_PingResponseIsSuccess()
    {
        var handler = new RecordingHandler(
            "{\"ok\":true,\"rows\":0,\"message\":\"Kết nối Apps Script thành công.\"}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.TestConnectionAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            "May-GSM-1");

        Assert.True(result.Success);
        Assert.Contains("URL và mã bảo mật hợp lệ", result.Message);
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        Assert.Equal("ping", document.RootElement.GetProperty("action").GetString());
        Assert.Equal("shared-secret", document.RootElement.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_OlderGatewayInvalidIdStillProvesConnection()
    {
        var handler = new RecordingHandler(
            "{\"ok\":false,\"message\":\"Spreadsheet ID không hợp lệ.\"}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.TestConnectionAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            "May-GSM-1");

        Assert.True(result.Success);
        Assert.Contains("URL và mã bảo mật hợp lệ", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_OlderGatewayMissingColumnsStillProvesConnection()
    {
        var handler = new RecordingHandler(
            "{\"ok\":false,\"message\":\"Không có cột dữ liệu.\"}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.TestConnectionAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "shared-secret",
            "May-GSM-1");

        Assert.True(result.Success);
        Assert.Contains("URL và mã bảo mật hợp lệ", result.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_WrongSecretIsFailure()
    {
        var handler = new RecordingHandler(
            "{\"ok\":false,\"message\":\"Mã bảo mật không đúng.\"}");
        using var client = new HttpClient(handler);
        using var service = new GoogleSheetsSyncService(client);

        GoogleSheetSyncResult result = await service.TestConnectionAsync(
            "https://script.google.com/macros/s/test-deployment/exec",
            "wrong-secret",
            "May-GSM-1");

        Assert.False(result.Success);
        Assert.Equal("Mã bảo mật không đúng.", result.Message);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
