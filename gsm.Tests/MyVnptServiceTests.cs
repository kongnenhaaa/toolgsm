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
    public void DeviceUnlockOtpPacing_IsLongEnoughToAvoidApiBursts()
    {
        Assert.InRange(
            MyVnptService.MinimumOtpSendSpacing,
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(3.5));
    }

    [Fact]
    public void PasswordOtpPacing_IsShortButPreventsSameMillisecondBurst()
    {
        Assert.InRange(
            MyVnptService.MinimumPasswordOtpSendSpacing,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500));
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
    [InlineData("OTP đã được gửi. Vui lòng đợi 1 phút để tiếp tục thao tác")]
    [InlineData("OTP da duoc gui. Vui long doi 1 phut")]
    [InlineData("OTP has been sent. Please wait one minute")]
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

    [Theory]
    [InlineData("reg_nok", "Thuê bao đã có tài khoản trên hệ thống")]
    [InlineData("1", "Thuê bao đã có tài khoản trên hệ thống")]
    public void RegisterConflict_RequestsNewPasswordResetOtp(
        string code,
        string message)
    {
        Assert.True(MyVnptService.ShouldRetryWithMissPassword(
            accountExists: false,
            code,
            message));
        Assert.False(MyVnptService.ShouldRetryWithMissPassword(
            accountExists: true,
            code,
            message));
    }

    [Theory]
    [InlineData("otp_nok", "")]
    [InlineData("1", "Mã OTP không hợp lệ")]
    [InlineData("1", "OTP đã hết hạn")]
    [InlineData("1", "Chưa yêu cầu otp/pin cho dịch vụ này")]
    [InlineData("1", "Invalid PIN")]
    public void RecoverableOtpFailure_RequestsOneFreshOtp(
        string code,
        string message)
    {
        Assert.True(MyVnptService.IsRecoverableOtpFailure(code, message));
    }

    [Theory]
    [InlineData("1", "Thuê bao đã có tài khoản trên hệ thống")]
    [InlineData("1", "Mật khẩu không đủ mạnh")]
    [InlineData("1", "Dịch vụ tạm thời không khả dụng")]
    public void NonOtpFailure_DoesNotRequestFreshOtp(
        string code,
        string message)
    {
        Assert.False(MyVnptService.IsRecoverableOtpFailure(code, message));
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
    public async Task ApiFastPath_AllowsTwentyIndependentRequestsInParallel()
    {
        const int expectedParallelRequests = 20;
        Assert.Equal(expectedParallelRequests, MyVnptService.ApiConcurrencyLimit);

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

    [Fact]
    public async Task TransientTransportFailure_IsRetriedInsteadOfDroppingCom()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var handler = new TransientThenSuccessHandler();
        using var client = new HttpClient(handler);

        string response = await MyVnptService.PostAsyncCore(
            client,
            "transport-retry-test",
            new { msisdn = "84900000000" },
            "test-device",
            "test-agent",
            cancellation.Token);

        Assert.Equal("{\"error_code\":\"0\"}", response);
        Assert.Equal(2, handler.Attempts);
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

    private sealed class TransientThenSuccessHandler : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                throw new HttpRequestException("Temporary connection reset");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"error_code\":\"0\"}")
            });
        }
    }

}
