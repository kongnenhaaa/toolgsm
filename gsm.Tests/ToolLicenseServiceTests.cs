using gsm.Services;

namespace gsm.Tests;

public sealed class ToolLicenseServiceTests
{
    [Fact]
    public void KeyAlgorithm_MatchesAdminGeneratorVector()
    {
        string key = ToolLicenseService.CreateKeyForHardwareId(
            "ABCD-1234-EFGH-5678",
            "12345678");

        Assert.Equal(
            "TGSM-12345678-6FB6BC31788B7D73CEB31442",
            key);
        Assert.True(ToolLicenseService.TryValidateKey(
            "ABCD-1234-EFGH-5678",
            key,
            out string error));
        Assert.Empty(error);
    }

    [Fact]
    public void KeyForAnotherMachine_IsRejected()
    {
        string key = ToolLicenseService.CreateKeyForHardwareId(
            "AAAA-BBBB-CCCC-DDDD",
            "12345678");

        Assert.False(ToolLicenseService.TryValidateKey(
            "1111-2222-3333-4444",
            key,
            out string error));
        Assert.Contains("HWID", error);
    }

    [Fact]
    public void LegacyAdminKey_WithUsageCount_IsAcceptedWithoutEnforcingCount()
    {
        string key = ToolLicenseService.CreateLegacyKeyForHardwareId(
            "ABCD-1234-EFGH-5678",
            "100",
            "123456");

        Assert.Equal("100-123456-447BCC67C12EF799F3AD", key);
        Assert.True(ToolLicenseService.TryValidateKey(
            "ABCD-1234-EFGH-5678",
            key,
            out string error));
        Assert.Empty(error);
    }

    [Fact]
    public void Activation_IsPersistedAndReloadedWithoutUsageCount()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"toolgsm-license-test-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "license.json");
        try
        {
            var first = new ToolLicenseService(
                "ABCD-1234-EFGH-5678-IJKL-9012",
                path);
            string key = ToolLicenseService.CreateLegacyKeyForHardwareId(
                first.HardwareId,
                "1",
                "876543");

            ToolLicenseActivationResult activation = first.Activate(key);

            Assert.True(activation.Success);
            Assert.True(first.IsActivated);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain("count", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var reloaded = new ToolLicenseService(
                "ABCD-1234-EFGH-5678-IJKL-9012",
                path);
            Assert.True(reloaded.IsActivated);
            Assert.Equal(key, reloaded.ActivatedKey);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
