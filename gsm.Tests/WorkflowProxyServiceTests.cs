using System.Diagnostics;
using System.Net;
using gsm.Models;
using gsm.Services;

namespace gsm.Tests;

public sealed class WorkflowProxyServiceTests
{
    [Fact]
    public void ProxyFlags_DefaultToDisabled()
    {
        var settings = new AppSettings();

        Assert.False(settings.UseProxyForMyVnpt);
        Assert.False(settings.UseProxyForDeviceUnlock);
        Assert.False(settings.UseProxyForDmx);
    }

    [Fact]
    public void TryParse_AuthenticatedProxy_PreservesPasswordColons()
    {
        bool parsed = WorkflowProxyService.TryParse(
            "192.0.2.10:31112:proxy-user:password:with:colons",
            out WorkflowProxyConfiguration? configuration,
            out string error);

        Assert.True(parsed, error);
        Assert.NotNull(configuration);
        Assert.Equal("192.0.2.10", configuration.Host);
        Assert.Equal(31112, configuration.Port);
        Assert.Equal("proxy-user", configuration.Username);
        Assert.Equal("password:with:colons", configuration.Password);
    }

    [Theory]
    [InlineData("127.0.0.1:not-a-port")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("missing-port")]
    public void TryParse_InvalidProxy_ReturnsError(string value)
    {
        bool parsed = WorkflowProxyService.TryParse(
            value,
            out WorkflowProxyConfiguration? configuration,
            out string error);

        Assert.False(parsed);
        Assert.Null(configuration);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void BuildProxyUri_EncodesCredentials()
    {
        var configuration = new WorkflowProxyConfiguration(
            "127.0.0.1",
            8080,
            "user@example.com",
            "p@ss word");

        string uri = WorkflowProxyService.BuildProxyUri(
            configuration,
            includeCredentials: true);

        Assert.Equal(
            "http://user%40example.com:p%40ss%20word@127.0.0.1:8080/",
            uri);
    }

    [Fact]
    public void ApplyToEnvironment_Disabled_RemovesInheritedProxyVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["HTTP_PROXY"] = "http://old-proxy:8080";
        startInfo.Environment["https_proxy"] = "http://old-proxy:8080";

        WorkflowProxyService.ApplyToEnvironment(
            startInfo,
            useConfiguredProxy: false);

        Assert.False(startInfo.Environment.ContainsKey("HTTP_PROXY"));
        Assert.False(startInfo.Environment.ContainsKey("https_proxy"));
    }

    [Fact]
    public void CreateHandler_EnablesCompressionAndKeepsConnectionsWarm()
    {
        using SocketsHttpHandler handler = WorkflowProxyService.CreateHandler(null);

        DecompressionMethods expected = DecompressionMethods.GZip
                                      | DecompressionMethods.Deflate
                                      | DecompressionMethods.Brotli;
        Assert.Equal(expected, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(10), handler.PooledConnectionIdleTimeout);
        Assert.False(handler.UseProxy);
    }
}
