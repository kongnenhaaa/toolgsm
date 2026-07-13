using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace gsm.Services
{
    public class ProxyManagerService
    {
        public class ProxyInfo
        {
            public string InterfaceName { get; set; } = string.Empty;
            public string IpAddress { get; set; } = string.Empty;
            public int Port { get; set; }
            public string ProxyString => $"127.0.0.1:{Port}";
        }

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningProxies = new();
        private readonly List<ProxyInfo> _proxyInfos = new();
        private int _startPort = 3001;
        private Timer? _refreshTimer;

        public void Start()
        {
            // Quét định kỳ 10 giây để phát hiện card mạng mới (khi cắm thêm SIM hoặc bật lại mạng)
            _refreshTimer = new Timer(RefreshInterfaces, null, 0, 10000);
        }

        public List<ProxyInfo> GetProxies()
        {
            lock (_proxyInfos)
            {
                return _proxyInfos.ToList();
            }
        }

        private void RefreshInterfaces(object? state)
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up && 
                                i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                var currentIps = new HashSet<string>();

                foreach (var iface in interfaces)
                {
                    // Lọc bỏ Wi-Fi, mạng ảo, VPN
                    string name = iface.Name.ToLower();
                    if (name.Contains("wi-fi") || name.Contains("tailscale") || name.Contains("vethernet") || name.Contains("vmware") || name.Contains("virtual"))
                        continue;

                    var ipProps = iface.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    
                    if (ipv4 != null)
                    {
                        string ip = ipv4.Address.ToString();
                        currentIps.Add(ip);

                        if (!_runningProxies.ContainsKey(ip))
                        {
                            int port = _startPort++;
                            var cts = new CancellationTokenSource();
                            _runningProxies.TryAdd(ip, cts);
                            
                            lock (_proxyInfos)
                            {
                                _proxyInfos.Add(new ProxyInfo { InterfaceName = iface.Name, IpAddress = ip, Port = port });
                            }

                            // Khởi động proxy server cục bộ cho IP này
                            _ = Task.Run(() => RunProxyServer(ip, port, cts.Token));
                        }
                    }
                }

                // Dọn dẹp các proxy của card mạng đã bị rút ra / mất kết nối
                var disconnected = _runningProxies.Keys.Except(currentIps).ToList();
                foreach (var ip in disconnected)
                {
                    if (_runningProxies.TryRemove(ip, out var cts))
                    {
                        cts.Cancel();
                        lock (_proxyInfos)
                        {
                            _proxyInfos.RemoveAll(p => p.IpAddress == ip);
                        }
                    }
                }
            }
            catch { }
        }

        private async Task RunProxyServer(string localIp, int port, CancellationToken ct)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();

                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client, localIp, ct), ct);
                }
            }
            catch { }
            finally
            {
                listener?.Stop();
            }
        }

        private async Task HandleClient(TcpClient client, string localIp, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) return;

                    string requestHeader = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var lines = requestHeader.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return;

                    var firstLine = lines[0].Split(' ');
                    if (firstLine.Length < 3) return;

                    string method = firstLine[0];
                    string url = firstLine[1];

                    string host = "";
                    int targetPort = 80;

                    if (method.ToUpper() == "CONNECT")
                    {
                        var parts = url.Split(':');
                        host = parts[0];
                        targetPort = parts.Length > 1 ? int.Parse(parts[1]) : 443;
                    }
                    else
                    {
                        try
                        {
                            var uri = new Uri(url);
                            host = uri.Host;
                            targetPort = uri.Port;
                        }
                        catch
                        {
                            // Fallback if not absolute URI
                            var hostLine = lines.FirstOrDefault(l => l.StartsWith("Host: ", StringComparison.OrdinalIgnoreCase));
                            if (hostLine != null)
                            {
                                host = hostLine.Substring(6).Trim();
                                if (host.Contains(":"))
                                {
                                    var parts = host.Split(':');
                                    host = parts[0];
                                    targetPort = int.Parse(parts[1]);
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(host)) return;

                    using (var targetClient = new TcpClient(AddressFamily.InterNetwork))
                    {
                        // Quan trọng nhất: Ràng buộc (bind) ra ngoài bằng IP nội bộ của SIM
                        targetClient.Client.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                        await targetClient.ConnectAsync(host, targetPort);

                        using (var targetStream = targetClient.GetStream())
                        {
                            if (method.ToUpper() == "CONNECT")
                            {
                                // Báo hiệu thiết lập Tunnel thành công cho client
                                byte[] okBytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                                await stream.WriteAsync(okBytes, 0, okBytes.Length, ct);
                            }
                            else
                            {
                                await targetStream.WriteAsync(buffer, 0, bytesRead, ct);
                            }

                            // Truyền tải dữ liệu 2 chiều
                            var t1 = stream.CopyToAsync(targetStream, 8192, ct);
                            var t2 = targetStream.CopyToAsync(stream, 8192, ct);
                            await Task.WhenAny(t1, t2);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
