using gsm.Services;

namespace gsm.Tests;

public sealed class UssdResponseDecoderTests
{
    [Fact]
    public void Normalize_UserResponse_DecodesUcs2AndRemovesAtEnvelope()
    {
        const string raw = "OK\r\n+CUSD: 0,\"0053006F002000540042002000300039003400360031003800330032003400380020002800560049004E00410036003900300029002E00200054004B0020006300680069006E0068003D003100360030003100200056004E0044002C0020004800530044002000300039002F00310030002F0032003000320036002E0020004E0067006100790020004B0048003A002000310031002F00300037002F0032003000320036002E0020004B0068006F006100310043003A002000300039002F00310030002F0032003000320036002E0020004B0068006F006100320043003A002000310039002F00310030002F0032003000320036002E002000430053004B004800200031003800300030003100300039003100200028003000640029\",15\r\n";

        string result = UssdResponseDecoder.Normalize(raw);

        Assert.Equal("So TB 0946183248 (VINA690). TK chinh=1601 VND, HSD 09/10/2026. Ngay KH: 11/07/2026. Khoa1C: 09/10/2026. Khoa2C: 19/10/2026. CSKH 18001091 (0d)", result);
    }

    [Fact]
    public void Normalize_AsciiPayload_ReturnsPayloadOnly()
    {
        Assert.Equal("4321 VND", UssdResponseDecoder.Normalize("+CUSD: 0,\"4321 VND\",15\r\nOK"));
    }

    [Fact]
    public void Normalize_NumericPayload_DoesNotMisdecodeBalanceAsUnicode()
    {
        Assert.Equal("1601", UssdResponseDecoder.Normalize("+CUSD: 0,\"1601\",15"));
    }

    [Fact]
    public void Normalize_ErrorWithoutCusd_PreservesError()
    {
        Assert.Equal("ERROR: network unavailable", UssdResponseDecoder.Normalize(" ERROR: network unavailable \r\n"));
    }
}
