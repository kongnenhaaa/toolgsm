using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using gsm.Services;

namespace gsm.Tests;

public sealed class FirebaseMachineIdentityTests
{
    [Fact]
    public async Task TwoInstallationsRequestingSameName_ReceiveDifferentStableNames()
    {
        var handler = new InMemoryFirebaseHandler();
        using var firstClient = new HttpClient(handler, disposeHandler: false);
        using var secondClient = new HttpClient(handler, disposeHandler: false);

        Task<string> first = FirebaseMachineIdentity.ClaimAsync(
            firstClient, "https://firebase.test", "GSM", Guid.NewGuid().ToString("N"));
        Task<string> second = FirebaseMachineIdentity.ClaimAsync(
            secondClient, "https://firebase.test", "GSM", Guid.NewGuid().ToString("N"));

        string[] names = await Task.WhenAll(first, second);

        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("GSM", names);
        Assert.Contains("GSM-2", names);
    }

    [Fact]
    public async Task SameInstallationAfterRestart_KeepsItsClaimedName()
    {
        var handler = new InMemoryFirebaseHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        string installationId = Guid.NewGuid().ToString("N");

        string first = await FirebaseMachineIdentity.ClaimAsync(
            client, "https://firebase.test", "May chinh", installationId);
        string afterRestart = await FirebaseMachineIdentity.ClaimAsync(
            client, "https://firebase.test", "May chinh", installationId);

        Assert.Equal("May chinh", first);
        Assert.Equal(first, afterRestart);
    }

    [Fact]
    public async Task LegacyMachineWithoutInstallationId_IsNeverOverwritten()
    {
        var handler = new InMemoryFirebaseHandler();
        handler.SetServerStatus("GSM", "{\"machineId\":\"GSM\",\"lastSync\":123}");
        using var client = new HttpClient(handler, disposeHandler: false);

        string resolved = await FirebaseMachineIdentity.ClaimAsync(
            client, "https://firebase.test", "GSM", Guid.NewGuid().ToString("N"));

        Assert.Equal("GSM-2", resolved);
    }

    [Fact]
    public void FirebaseKey_IsSanitizedBeforeClaiming()
    {
        Assert.Equal("May_1_", FirebaseService.SanitizeFirebaseKey(" May.1/#[]$ "));
    }

    [Fact]
    public void DeviceScopedInstallationId_IsStableAndDoesNotExposeRawId()
    {
        string raw = Guid.NewGuid().ToString("N");

        string first = FirebaseMachineIdentity.GetDeviceScopedInstallationId(raw);
        string second = FirebaseMachineIdentity.GetDeviceScopedInstallationId(raw);

        Assert.Equal(first, second);
        Assert.NotEqual(raw, first);
        Assert.Equal(64, first.Length);
    }

    private sealed class InMemoryFirebaseHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _claims =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _versions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _statuses =
            new(StringComparer.OrdinalIgnoreCase);

        internal void SetServerStatus(string machineId, string json)
        {
            lock (_gate) _statuses[machineId] = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath.Trim('/');
            if (path.StartsWith("machines/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/identity_claim.json", StringComparison.OrdinalIgnoreCase))
            {
                string machineId = path["machines/".Length..^"/identity_claim.json".Length];
                if (request.Method == HttpMethod.Get)
                    return GetClaim(machineId);
                if (request.Method == HttpMethod.Put)
                {
                    string json = await request.Content!.ReadAsStringAsync(cancellationToken);
                    request.Headers.TryGetValues("if-match", out var ifMatchValues);
                    return PutClaim(machineId, json, ifMatchValues?.FirstOrDefault());
                }
            }

            if (request.Method == HttpMethod.Get
                && path.StartsWith("machines/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/server_status.json", StringComparison.OrdinalIgnoreCase))
            {
                string machineId = path["machines/".Length..
                    ^"/server_status.json".Length];
                lock (_gate)
                {
                    return JsonResponse(
                        _statuses.TryGetValue(machineId, out string? value)
                            ? value
                            : "null");
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage GetClaim(string machineId)
        {
            lock (_gate)
            {
                int version = _versions.GetValueOrDefault(machineId);
                bool exists = _claims.TryGetValue(machineId, out string? value);
                var response = JsonResponse(
                    exists ? value! : "null");
                if (exists)
                    response.Headers.ETag = new EntityTagHeaderValue($"\"{version}\"");
                else
                    response.Headers.TryAddWithoutValidation("ETag", "null_etag");
                return response;
            }
        }

        private HttpResponseMessage PutClaim(
            string machineId,
            string json,
            string? ifMatch)
        {
            lock (_gate)
            {
                int version = _versions.GetValueOrDefault(machineId);
                string expectedEtag = _claims.ContainsKey(machineId)
                    ? $"\"{version}\""
                    : "null_etag";
                if (ifMatch != null && ifMatch != expectedEtag)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

                using JsonDocument document = JsonDocument.Parse(json);
                Assert.True(document.RootElement.TryGetProperty("installationId", out _));
                _claims[machineId] = json;
                _versions[machineId] = version + 1;
                return JsonResponse(json);
            }
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
