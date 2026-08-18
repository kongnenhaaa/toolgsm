using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace gsm.Services;

/// <summary>
/// Atomically assigns a unique Firebase machine key to one ToolGSM install.
/// The claim is permanent for the installation so a restart cannot silently
/// switch back to a key currently used by another tool.
/// </summary>
internal static class FirebaseMachineIdentity
{
    private const int MaximumSuffix = 10_000;

    internal static string GetDeviceScopedInstallationId(string installationId)
    {
        string machineFingerprint;
        try
        {
            machineFingerprint = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                    "MachineGuid",
                    null)?.ToString() ?? "";
        }
        catch
        {
            machineFingerprint = "";
        }

        if (string.IsNullOrWhiteSpace(machineFingerprint))
        {
            machineFingerprint = string.Join(
                "|", Environment.MachineName, Environment.UserDomainName,
                Environment.SystemDirectory);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{installationId.Trim()}|{machineFingerprint.Trim()}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static async Task<string> ClaimAsync(
        HttpClient client,
        string databaseUrl,
        string requestedMachineId,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        string baseUrl = databaseUrl.TrimEnd('/');
        string baseName = FirebaseService.SanitizeFirebaseKey(requestedMachineId);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "machine";

        for (int suffix = 1; suffix <= MaximumSuffix; suffix++)
        {
            string candidate = suffix == 1 ? baseName : $"{baseName}-{suffix}";
            // Keep the claim below /machines because the deployed Firebase
            // rules only allow the existing application roots.
            string claimUrl = $"{baseUrl}/machines/{candidate}/identity_claim.json";

            using var getClaim = new HttpRequestMessage(HttpMethod.Get, claimUrl);
            getClaim.Headers.TryAddWithoutValidation("X-Firebase-ETag", "true");
            using HttpResponseMessage claimResponse = await client.SendAsync(
                getClaim, HttpCompletionOption.ResponseContentRead, cancellationToken);
            claimResponse.EnsureSuccessStatusCode();

            string claimJson = await claimResponse.Content.ReadAsStringAsync(cancellationToken);
            string? claimedBy = ReadInstallationId(claimJson);
            if (string.Equals(claimedBy, installationId, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshClaimAsync(
                    client, claimUrl, candidate, requestedMachineId, installationId,
                    cancellationToken);
                return candidate;
            }

            // A malformed/non-null claim is still occupied. Never risk two
            // machines sharing a writer path just because old data is partial.
            if (!IsFirebaseNull(claimJson)) continue;

            string statusUrl = $"{baseUrl}/machines/{candidate}/server_status.json";
            using HttpResponseMessage statusResponse = await client.GetAsync(
                statusUrl, cancellationToken);
            statusResponse.EnsureSuccessStatusCode();
            string statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!IsFirebaseNull(statusJson))
            {
                string? statusInstallationId = ReadInstallationId(statusJson);
                if (!string.Equals(
                        statusInstallationId, installationId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // Includes legacy ToolGSM nodes without installationId.
                    // Reserve their name so an updated tool cannot overwrite it.
                    continue;
                }
            }

            string? etag = claimResponse.Headers.ETag?.Tag;
            if (string.IsNullOrWhiteSpace(etag)
                && claimResponse.Headers.TryGetValues("ETag", out var values))
            {
                etag = System.Linq.Enumerable.FirstOrDefault(values);
            }
            if (string.IsNullOrWhiteSpace(etag))
                throw new InvalidOperationException("Firebase did not return an ETag for machine claim.");

            using var putClaim = BuildClaimRequest(
                claimUrl, candidate, requestedMachineId, installationId);
            // Firebase uses the special unquoted value "null_etag" for an
            // absent node, which EntityTagHeaderValue intentionally rejects.
            putClaim.Headers.TryAddWithoutValidation("if-match", etag);
            using HttpResponseMessage putResponse = await client.SendAsync(
                putClaim, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (putResponse.IsSuccessStatusCode) return candidate;
            if (putResponse.StatusCode == HttpStatusCode.PreconditionFailed) continue;
            putResponse.EnsureSuccessStatusCode();
        }

        throw new InvalidOperationException("Không thể cấp tên máy Firebase duy nhất.");
    }

    private static async Task RefreshClaimAsync(
        HttpClient client,
        string claimUrl,
        string candidate,
        string requestedMachineId,
        string installationId,
        CancellationToken cancellationToken)
    {
        using var request = BuildClaimRequest(
            claimUrl, candidate, requestedMachineId, installationId);
        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage BuildClaimRequest(
        string claimUrl,
        string candidate,
        string requestedMachineId,
        string installationId)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machineId"] = candidate,
            ["requestedMachineId"] = requestedMachineId,
            ["installationId"] = installationId,
            ["deviceName"] = Environment.MachineName,
            ["lastSeen"] = new Dictionary<string, string> { [".sv"] = "timestamp" }
        });
        return new HttpRequestMessage(HttpMethod.Put, claimUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static bool IsFirebaseNull(string? json) =>
        string.IsNullOrWhiteSpace(json)
        || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    private static string? ReadInstallationId(string? json)
    {
        if (IsFirebaseNull(json)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json!);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("installationId", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }
}
