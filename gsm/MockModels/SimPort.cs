namespace gsm.MockModels
{
    public enum SimStatus
    {
        Unknown,
        Connecting,
        Active,
        SecurityBlocked,
        NoResponse
    }

    public class SimPort
    {
        public string PortName { get; set; } = "";
        public string Imei { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public string Balance { get; set; } = "";
        public string NetworkProvider { get; set; } = "";
        public SimStatus Status { get; set; } = SimStatus.Unknown;
        public string DeviceName { get; set; } = "";
        public string Serial { get; set; } = ""; // CCID
        public bool IsRebooting { get; set; }
    }
}
