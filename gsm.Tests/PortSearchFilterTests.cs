using gsm.Models;

namespace gsm.Tests;

public sealed class PortSearchFilterTests
{
    private static readonly SimPort[] Ports =
    [
        new() { PortName = "COM12", PhoneNumber = "0830000012", Serial = "CCID-12", Imei = "IMEI-12" },
        new() { PortName = "COM3", PhoneNumber = "0830000003", Serial = "CCID-03", Imei = "IMEI-03" },
        new() { PortName = "COM91", PhoneNumber = "0839583279", Serial = "CCID-91", Imei = "IMEI-91" },
        new() { PortName = "COM7", PhoneNumber = "0810000007", Serial = "CCID-07", Imei = "IMEI-07" }
    ];

    [Fact]
    public void ListSearch_PreservesOriginalGsmOrder_NotInputOrder()
    {
        SimPort[] result = PortSearchFilter.Filter(
            Ports,
            "0839583279\r\n0830000012, 0830000003")
            .ToArray();

        Assert.Equal(["COM12", "COM3", "COM91"], result.Select(port => port.PortName));
    }

    [Theory]
    [InlineData("0839583279")]
    [InlineData("84839583279")]
    [InlineData("+84 839583279")]
    public void PhoneSearch_AcceptsCommonVietnamNumberFormats(string query)
    {
        SimPort port = Assert.Single(PortSearchFilter.Filter(Ports, query));

        Assert.Equal("COM91", port.PortName);
    }

    [Fact]
    public void ListSearch_AcceptsComCcidAndImeiTerms()
    {
        SimPort[] result = PortSearchFilter.Filter(
            Ports,
            "IMEI-12; COM91 | CCID-07")
            .ToArray();

        Assert.Equal(["COM12", "COM91", "COM7"], result.Select(port => port.PortName));
    }

    [Fact]
    public void EmptySearch_ReturnsEveryPortInOriginalOrder()
    {
        SimPort[] result = PortSearchFilter.Filter(Ports, "  \r\n ").ToArray();

        Assert.Equal(Ports, result);
    }

    [Fact]
    public void Parser_RemovesDuplicateAndEmptyTerms()
    {
        IReadOnlyList<string> terms = PortSearchFilter.ParseTerms(
            "0839583279, 0839583279\r\n; COM91");

        Assert.Equal(["0839583279", "COM91"], terms);
    }
}
