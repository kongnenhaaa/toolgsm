using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;

namespace gsm.Services;

public interface IGsmModemService
{
    Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 1000);
    List<string> GetAvailablePorts();
}

public class GsmModemService : IGsmModemService
{
    public List<string> GetAvailablePorts()
    {
        return new List<string>(SerialPort.GetPortNames());
    }

    public async Task<string> SendCommandAsync(string portName, string command, int timeoutMs = 1000)
    {
        // Mock implementation for UI building phase
        await Task.Delay(200); // Simulate network delay
        
        if (command == "AT+CUSD=1,\"*101#\",15")
        {
            return "+CUSD: 0,\"TK Chinh: 50000d. HSD: 10/10/2026. TK KM: 0d.\", 15\r\nOK";
        }
        else if (command == "AT+CIMI")
        {
            return "452012345678901\r\nOK";
        }
        
        return "OK";
    }
}
