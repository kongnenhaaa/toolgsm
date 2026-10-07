using gsm.Services;
using System.Net;

namespace gsm.Tests;

public sealed class MyVnptServiceTests
{
    [Fact]
    public void StableDeviceInfo_SamePortNameProducesSameFingerprint()
    {
        string upper = MyVnptService.CreateStableDeviceInfo("COM35");
        string normalized = MyVnptService.CreateStableDeviceInfo(" com35 ");

        Assert.Equal(upper, normalized);
    }

    [Fact]
    public void StableDeviceInfo_DifferentPortsProduceDifferentFingerprints()
    {
        string first = MyVnptService.CreateStableDeviceInfo("COM35");
        string second = MyVnptService.CreateStableDeviceInfo("COM36");

        Assert.NotEqual(first, second);
        string[] fields = first.Split('|');
        Assert.Equal(fields[0], fields[1]);
        Assert.Matches(
            "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            fields[0]);
    }

    [Fact]
    public void OtpSendPacing_IsLongEnoughToAvoidApiBursts()
    {
        Assert.InRange(
            MyVnptService.MinimumOtpSendSpacing,
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(3.5));
    }

    [Theory]
    [InlineData("0942 152 795", "84942152795")]
    [InlineData("84942152795", "84942152795")]
    [InlineData("+84 942 152 795", "84942152795")]
    public void NormalizePhone_AcceptsSupportedVietnameseFormats(string input, string expected)
    {
        Assert.Equal(expected, MyVnptService.NormalizePhone(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("900")]
    [InlineData("1234567890")]
    [InlineData("8494215279500")]
    public void NormalizePhone_RejectsInvalidDestinations(string input)
    {
        Assert.Empty(MyVnptService.NormalizePhone(input));
    }

    [Theory]
    [InlineData("Ma OTP MyVNPT cua ban la 123456")]
    [InlineData("ma otp myvnpt cua ban la 123456")]
    [InlineData("MY VNPT: ma xac thuc 123456")]
    [InlineData("607718 la ma xac thuc OTP tren MyVNPT cua Quy Khach, hieu luc trong 2 phut. De dam bao an toan, vui long khong chia se ma nay voi bat ky ai.")]
    [InlineData("[VNPT] Ma OTP xac nhan ky hop dong dien tu cua ban la: 463827. Co hieu luc trong 5 phut. VNPT eContract.")]
    [InlineData("[VNPT] Mã OTP xác nhận ký hợp đồng điện tử của bạn là 463827. VNPT eContract.")]
    public void IsMyVnptOtpMessage_IsCaseInsensitive(string content)
    {
        Assert.True(MyVnptService.IsMyVnptOtpMessage(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OTP Zalo cua ban la 123456")]
    [InlineData("VNPT eContract: hop dong cua ban da duoc cap nhat")]
    public void IsMyVnptOtpMessage_RejectsUnrelatedSms(string? content)
    {
        Assert.False(MyVnptService.IsMyVnptOtpMessage(content));
    }

    [Theory]
    [InlineData("Bạn đang gửi OTP")]
    [InlineData("Ban dang gui OTP, vui long cho")]
    public void IsOtpAlreadyPendingMessage_TreatsExistingRequestAsPending(string content)
    {
        Assert.True(MyVnptService.IsOtpAlreadyPendingMessage(content));
    }

    [Theory]
    [InlineData("reg_nok", "Đăng ký không thành công")]
    [InlineData("1", "Thuê bao đã có tài khoản trên hệ thống")]
    [InlineData("1", "Tai khoan da ton tai")]
    [InlineData("1", "Account already exists")]
    public void IsAccountAlreadyExistsResponse_RecognizesRegisterConflicts(string code, string message)
    {
        Assert.True(MyVnptService.IsAccountAlreadyExistsResponse(code, message));
    }

    [Fact]
    public void IsAccountAlreadyExistsResponse_RejectsMissingAccountMessage()
    {
        Assert.False(MyVnptService.IsAccountAlreadyExistsResponse("1", "Chưa có tài khoản VNPortal"));
    }

    [Fact]
    public void GetFriendlyExceptionMessage_ExplainsServiceUnavailable()
    {
        var exception = new HttpRequestException(
            "VNPT HTTP 503: Service Temporarily Unavailable",
            null,
            HttpStatusCode.ServiceUnavailable);

        string message = MyVnptService.GetFriendlyExceptionMessage(exception);

        Assert.Contains("VNPT", message);
        Assert.DoesNotContain("HTTP 503", message);
    }

    [Fact]
    public async Task ApiFastPath_AllowsIndependentRequestsInParallel()
    {
        const int expectedParallelRequests = 3;
        Assert.True(MyVnptService.ApiConcurrencyLimit >= expectedParallelRequests);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var handler = new ConcurrentRequestHandler(expectedParallelRequests);
        using var client = new HttpClient(handler);

        Task<string>[] requests = Enumerable.Range(0, expectedParallelRequests)
            .Select(index => MyVnptService.PostAsyncCore(
                client,
                $"parallel-test-{index}",
                new { msisdn = $"8490000000{index}" },
                "test-device",
                "test-agent",
                cancellation.Token))
            .ToArray();

        await Task.WhenAll(requests);

        Assert.True(handler.MaximumConcurrency >= expectedParallelRequests);
    }

    private sealed class ConcurrentRequestHandler(int expectedConcurrency)
        : HttpMessageHandler
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _allRequestsEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;
        private int _maximumConcurrency;

        public int MaximumConcurrency
        {
            get
            {
                lock (_sync) return _maximumConcurrency;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _activeRequests++;
                _maximumConcurrency = Math.Max(
                    _maximumConcurrency,
                    _activeRequests);
                if (_activeRequests >= expectedConcurrency)
                    _allRequestsEntered.TrySetResult();
            }

            try
            {
                await _allRequestsEntered.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"error_code\":\"0\"}")
                };
            }
            finally
            {
                lock (_sync) _activeRequests--;
            }
        }
    }

}
