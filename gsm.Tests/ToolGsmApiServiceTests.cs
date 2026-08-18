using System.Collections.Concurrent;
using gsm.Services;

namespace gsm.Tests;

public sealed class ToolGsmApiServiceTests
{
    [Fact]
    public async Task SubmitSms_SelectsExactSourcePhoneAndSendsOnce()
    {
        var host = new FakeApiHost(
        [
            new ToolGsmApiPort("COM10", 10, "0900000000", true),
            new ToolGsmApiPort("COM11", 11, "0912345678", true)
        ]);
        var service = new ToolGsmApiService(host);

        ToolGsmSmsResponse response = await service.SubmitSmsAsync(Request());

        Assert.True(response.Ok);
        Assert.Equal("sent", response.Status);
        Assert.Equal("COM11", response.PortName);
        Assert.Single(host.Sends);
        Assert.Equal(
            ("COM11", "0362669166", "[Zalo] 3zJzYNy2N320f9WhqHn82M3B4EoxcKXa"),
            host.Sends.Single());
    }

    [Fact]
    public async Task SubmitSms_DuplicateRequestIdReturnsCachedResultWithoutResend()
    {
        var host = new FakeApiHost(
        [new ToolGsmApiPort("COM8", 8, "+84912345678", true)]);
        var service = new ToolGsmApiService(host);
        ToolGsmSmsRequest request = Request();

        ToolGsmSmsResponse first = await service.SubmitSmsAsync(request);
        ToolGsmSmsResponse second = await service.SubmitSmsAsync(request);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Single(host.Sends);
    }

    [Fact]
    public async Task SubmitSms_UncertainPayloadIsCachedWithoutResend()
    {
        const string uncertainResult =
            "ERROR: [SMS_PAYLOAD_SUBMITTED] [SMS_CHANNEL_RECOVERY_REQUIRED] Timeout sending SMS payload; SMS result uncertain";
        var host = new FakeApiHost(
            [new ToolGsmApiPort("COM8", 8, "+84912345678", true)],
            [uncertainResult]);
        var service = new ToolGsmApiService(host);
        ToolGsmSmsRequest request = Request();

        ToolGsmSmsResponse first = await service.SubmitSmsAsync(request);
        ToolGsmSmsResponse second = await service.SubmitSmsAsync(request);

        Assert.False(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal("maybe_sent", first.Status);
        Assert.Equal("maybe_sent", second.Status);
        Assert.Equal(202, first.HttpStatusCode);
        Assert.Equal(202, second.HttpStatusCode);
        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Single(host.Sends);
    }

    [Fact]
    public async Task SubmitSms_DefiniteFailureCanRetryWithSameRequestId()
    {
        var host = new FakeApiHost(
            [new ToolGsmApiPort("COM8", 8, "+84912345678", true)],
            ["ERROR: modem rejected payload", "+CMGS: 9\r\nOK"]);
        var service = new ToolGsmApiService(host);
        ToolGsmSmsRequest request = Request();

        ToolGsmSmsResponse first = await service.SubmitSmsAsync(request);
        ToolGsmSmsResponse second = await service.SubmitSmsAsync(request);

        Assert.False(first.Ok);
        Assert.Equal("failed", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("sent", second.Status);
        Assert.Equal(2, host.Sends.Count);
    }

    [Fact]
    public async Task SubmitSms_RejectsGenericSmsRelayPayload()
    {
        var host = new FakeApiHost(
        [new ToolGsmApiPort("COM8", 8, "0912345678", true)]);
        var service = new ToolGsmApiService(host);
        ToolGsmSmsRequest request = Request() with { Message = "generic text" };

        ToolGsmSmsResponse response = await service.SubmitSmsAsync(request);

        Assert.False(response.Ok);
        Assert.Equal("invalid_request", response.ErrorCode);
        Assert.Empty(host.Sends);
    }

    [Fact]
    public async Task SubmitSms_FailsWhenDuplicateActivePortsSharePhone()
    {
        var host = new FakeApiHost(
        [
            new ToolGsmApiPort("COM8", 8, "0912345678", true),
            new ToolGsmApiPort("COM9", 9, "+84912345678", true)
        ]);
        var service = new ToolGsmApiService(host);

        ToolGsmSmsResponse response = await service.SubmitSmsAsync(Request());

        Assert.False(response.Ok);
        Assert.Equal("source_phone_ambiguous", response.ErrorCode);
        Assert.Empty(host.Sends);
    }

    private static ToolGsmSmsRequest Request() => new()
    {
        SchemaVersion = 1,
        RequestId = "zalo-mo-0123456789abcdef",
        Purpose = "zalo-manual-mo",
        SourcePhone = "0912345678",
        Destination = "+84362669166",
        Message = "[Zalo] 3zJzYNy2N320f9WhqHn82M3B4EoxcKXa"
    };

    private sealed class FakeApiHost : IToolGsmApiHost
    {
        private readonly IReadOnlyList<ToolGsmApiPort> _ports;
        private readonly ConcurrentQueue<string> _sendResults = new();

        public FakeApiHost(
            IReadOnlyList<ToolGsmApiPort> ports,
            IEnumerable<string>? sendResults = null)
        {
            _ports = ports;
            if (sendResults != null)
            {
                foreach (string result in sendResults)
                    _sendResults.Enqueue(result);
            }
        }

        public ConcurrentQueue<(string Port, string Destination, string Message)> Sends
            { get; } = new();

        public IReadOnlyList<ToolGsmApiPort> GetPorts() => _ports;

        public IReadOnlyList<SmsInboxRecord> GetOtpInbox() =>
            Array.Empty<SmsInboxRecord>();

        public Task<string> SendSmsAsync(
            string portName,
            string destination,
            string message,
            CancellationToken cancellationToken)
        {
            Sends.Enqueue((portName, destination, message));
            return Task.FromResult(
                _sendResults.TryDequeue(out string? result)
                    ? result
                    : "+CMGS: 7\r\nOK");
        }

        public void UpsertQueue(
            string requestId,
            string portName,
            string destination,
            string message,
            string status,
            string? result,
            string? error)
        {
        }

        public void Log(string message, string level)
        {
        }
    }
}
