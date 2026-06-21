using System;
using System.Threading.Tasks;
using Moq;
using Xunit;
using gsm.Services;
using gsm.Models;

namespace gsm.Tests
{
    public class ImeiManagementTests
    {
        private readonly Mock<IGsmModemService> _mockModem;
        private readonly ImeiManagementService _service;

        public ImeiManagementTests()
        {
            _mockModem = new Mock<IGsmModemService>();
            _service = new ImeiManagementService(_mockModem.Object, (msg, level) => { });
        }

        [Fact]
        public void IsValidImei_TestCases_ReturnsExpected()
        {
            // Testing static logic exposed via DeviceSpoofingService
            Assert.True(DeviceSpoofingService.IsValidImei("490154203237518"));
            Assert.False(DeviceSpoofingService.IsValidImei("490154203237519"));
        }

        [Fact]
        public async Task ProcessImeiAsync_CFUN0_Fails_ReturnsSecurityBlocked()
        {
            // Arrange
            var port = new SimPort { PortName = "COM1", PhoneNumber = "123" };
            var settings = new AppSettings { EnableDeviceSpoofing = true };
            
            // CFUN=0 fails
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CFUN=0", 10000, true))
                      .ReturnsAsync("ERROR");

            // Act
            var result = await _service.ProcessImeiAsync(port, "123456789012345678", "123456789012345", settings, 
                ccid => null, 
                entry => {}, 
                action => action());

            // Assert
            Assert.Equal(ImeiProcessStatus.SecurityBlocked, result.Status);
            Assert.Equal(SecurityErrors.RadioOffFailed, result.ErrorMessage);
        }

        [Fact]
        public async Task ProcessImeiAsync_Unsupported_BreaksEarly()
        {
            // Arrange
            var port = new SimPort { PortName = "COM1", PhoneNumber = "123" };
            var settings = new AppSettings { EnableDeviceSpoofing = true };
            
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CFUN=0", 10000, true)).ReturnsAsync("OK");
            
            // Hard error for both EGMR and SIMEI
            _mockModem.Setup(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false)).ReturnsAsync("ERROR");
            _mockModem.Setup(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+SIMEI")), 30000, false)).ReturnsAsync("ERROR");
            
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CGSN", 10000, true)).ReturnsAsync("123456789012345");

            // Act
            var result = await _service.ProcessImeiAsync(port, "123456789012345678", "123456789012345", settings, 
                ccid => null, entry => {}, action => action());

            // Assert
            // It should break early after first attempt because of unsupported firmware
            _mockModem.Verify(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false), Times.Once);
            Assert.Equal(ImeiProcessStatus.SecurityBlocked, result.Status);
        }

        [Fact]
        public async Task ProcessImeiAsync_Timeout_Retries()
        {
            // Arrange
            var port = new SimPort { PortName = "COM1", PhoneNumber = "123" };
            var settings = new AppSettings { EnableDeviceSpoofing = true };
            
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CFUN=0", 10000, true)).ReturnsAsync("OK");
            
            // Temporary timeout error
            _mockModem.Setup(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false))
                      .ReturnsAsync("ERROR: Timeout waiting for lock");
            
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CGSN", 10000, true)).ReturnsAsync("123456789012345");

            // Act
            var result = await _service.ProcessImeiAsync(port, "123456789012345678", "123456789012345", settings, 
                ccid => null, entry => {}, action => action());

            // Assert
            // It should retry 3 times since it's a temporary timeout
            _mockModem.Verify(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false), Times.Exactly(3));
            Assert.Equal(ImeiProcessStatus.SecurityBlocked, result.Status);
        }

        [Fact]
        public async Task ProcessImeiAsync_Fake_Success()
        {
            // Arrange
            var port = new SimPort { PortName = "COM1", PhoneNumber = "123" };
            var settings = new AppSettings { EnableDeviceSpoofing = true };
            
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CFUN=0", 10000, true)).ReturnsAsync("OK");
            _mockModem.Setup(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false)).ReturnsAsync("OK");
            
            // To simulate success, the final CGSN must match the target IMEI.
            // Since we don't know the exact randomly generated target IMEI here without extracting it,
            // we can capture it when AT+EGMR is called, and then return it for AT+CGSN.
            string targetImei = "";
            _mockModem.Setup(m => m.SendCommandAsync("COM1", It.Is<string>(s => s.StartsWith("AT+EGMR")), 30000, false))
                      .Callback<string, string, int, bool>((p, cmd, t, s) => {
                          var match = System.Text.RegularExpressions.Regex.Match(cmd, @"\""(\d{15})\""");
                          if (match.Success) targetImei = match.Groups[1].Value;
                      })
                      .ReturnsAsync("OK");
                      
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CGSN", 10000, true))
                      .ReturnsAsync(() => targetImei); // Return whatever was written
                      
            _mockModem.Setup(m => m.SendCommandAsync("COM1", "AT+CFUN=1", 30000, false)).ReturnsAsync("OK");

            // Act
            var result = await _service.ProcessImeiAsync(port, "123456789012345678", "123456789012345", settings, 
                ccid => null, entry => {}, action => action());

            // Assert
            Assert.Equal(ImeiProcessStatus.Applied, result.Status);
            Assert.Equal(targetImei, result.FinalImei);
        }
    }
}
