using gsm.Services;

namespace gsm.Tests;

public sealed class ImeiWriteGuardTests
{
    [Theory]
    [InlineData("AT+CFUN=0")]
    [InlineData("AT+CFUN=4")]
    [InlineData("AT+CFUN=1")]
    [InlineData("AT+CFUN=1,1")]
    [InlineData("AT+COPS=0")]
    [InlineData("AT+QCFG=\"nwscanmode\",0,1")]
    [InlineData("AT+QCFG=\"nwscanseq\",020301")]
    [InlineData("AT+QCFG=\"band\",0,0,0")]
    [InlineData("AT+QCFG=\"ims\",1")]
    [InlineData("AT+QCFG=\"ims\",2")]
    [InlineData("AT+QCFG=\"ims/ut\",0")]
    [InlineData("AT+QPOWD=1")]
    [InlineData("AT+QRESET")]
    [InlineData("AT+QRST=1")]
    [InlineData("AT+QPRTPARA=1")]
    [InlineData("AT+QPRTPARA=2")]
    [InlineData("AT+QPRTPARA=3")]
    [InlineData("ATZ")]
    [InlineData("AT&F")]
    public void RadioDisruptiveCommands_AreBlocked(string command) =>
        Assert.True(GsmModemService.IsRadioDisruptiveCommand(command));

    [Theory]
    [InlineData("AT+CFUN?")]
    [InlineData("AT+COPS?")]
    [InlineData("AT+QCFG=\"nwscanmode\"")]
    [InlineData("AT+QCFG=\"ims/ut\"")]
    [InlineData("AT+QPRTPARA?")]
    [InlineData("AT+QCFG=\"urcport\",\"uart1\"")]
    [InlineData("AT+CPIN?")]
    [InlineData("AT+ICCID")]
    public void RadioSafeCommands_AreAllowed(string command) =>
        Assert.False(GsmModemService.IsRadioDisruptiveCommand(command));

    [Theory]
    [InlineData("AT+EGMR=1,7,\"490154203237518\"")]
    [InlineData("AT+EGMR=01,7,\"490154203237518\"")]
    [InlineData("AT+EGMR=+1,7,\"490154203237518\"")]
    [InlineData("ATE0+EGMR=001,7,\"490154203237518\"")]
    [InlineData("AT+EGMR=0X1,7,\"490154203237518\"")]
    [InlineData(" at + egmr = 1 , 10 , \"490154203237518\" ")]
    [InlineData("AT+EGMR=1,99,\"490154203237518\"")]
    [InlineData("AT\r\nAT+EGMR=1,7,\"490154203237518\"")]
    [InlineData("AT;AT+EGMR=1,7,\"490154203237518\"")]
    [InlineData("AT+CSQ;+EGMR=1,7,\"490154203237518\"")]
    [InlineData("AT+QIMEI=490154203237518")]
    [InlineData("at+simei=490154203237518")]
    [InlineData("AT^CIMEI=490154203237518")]
    public void ImeiWriteCommands_AreBlocked(string command)
    {
        Assert.True(GsmModemService.IsImeiWriteCommand(command));
    }

    [Theory]
    [InlineData("AT+EGMR=0,7;")]
    [InlineData("AT+EGMR=00,7;")]
    [InlineData("AT+CGSN")]
    [InlineData("AT+QIMEI?")]
    [InlineData("AT")]
    [InlineData("")]
    public void ReadOnlyOrUnrelatedCommands_AreAllowed(string command)
    {
        Assert.False(GsmModemService.IsImeiWriteCommand(command));
    }

    [Theory]
    [InlineData("AT+EGMR=0\b1,7,\"490154203237518\"")]
    [InlineData("AT\r\nAT+CSQ")]
    [InlineData("AT\u007F+CSQ")]
    public void AtControlCharacters_AreRejectedBeforeUartWrite(string command)
    {
        Assert.True(GsmModemService.ContainsUnsafeAtControlCharacter(command));
    }

    [Theory]
    [InlineData("ATS3=13")]
    [InlineData("ATS04=10")]
    [InlineData("AT+CSQ;S5=88")]
    [InlineData("ATE0S5=88")]
    public void AtLineDisciplineSetters_AreRejected(string command)
    {
        Assert.True(
            GsmModemService.IsUnsafeAtLineDisciplineCommand(command));
    }

    [Theory]
    [InlineData("ATS3?")]
    [InlineData("ATS5?")]
    [InlineData("AT+CSQ")]
    public void ReadOnlyLineDisciplineCommands_AreAllowed(string command)
    {
        Assert.False(
            GsmModemService.IsUnsafeAtLineDisciplineCommand(command));
    }

    [Theory]
    [InlineData("hello\u001Aworld", true)]
    [InlineData("hello\u001Bworld", true)]
    [InlineData("hello\0world", true)]
    [InlineData("hello\bworld", true)]
    [InlineData("hello\r\nworld", false)]
    [InlineData("hello\tworld", false)]
    public void SmsPayloads_CannotTerminateTheTransportEarly(
        string message,
        bool expected)
    {
        Assert.Equal(
            expected,
            GsmModemService
                .ContainsUnsafeSmsTransportControlCharacter(message));
    }

    [Theory]
    [InlineData("call-play.wav", true)]
    [InlineData("ufs:incoming-COM83.wav", true)]
    [InlineData("x\"\rAT+EGMR=1,7,\"490154203237518\"", false)]
    [InlineData("", false)]
    public void ModemFileNames_CannotInjectAtCommands(
        string remoteFile,
        bool expected)
    {
        Assert.Equal(expected, GsmModemService.IsSafeModemFileName(remoteFile));
    }
}
