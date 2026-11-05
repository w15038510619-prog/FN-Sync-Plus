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

namespace FNSyncPlus.Core.Services
{
    public class DiscoveredDevice
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int SmbPort { get; set; } = 445;
        public int WebPort { get; set; } = 5667;
        public bool IsFeiniuNas { get; set; }
        public string Status { get; set; } = "在线";
        public string DiscoveryMethod { get; set; } = "mDNS";

        public override string ToString() => $"{Name} ({IpAddress})";
    }

    public class NasDiscoveryService
    {
        private const int MdnsPort = 5353;
        private static readonly IPAddress MdnsAddress = IPAddress.Parse("224.0.0.251");

        /// <summary>
        /// 综合执行局域网 NAS 发现：结合 mDNS 组播发现与快速关键子网 TCP 端口探测
        /// </summary>
        public async Task<List<DiscoveredDevice>> DiscoverNasDevicesAsync(string? knownIp = null, CancellationToken cancellationToken = default)
        {
            var discoveredMap = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);

            // 1. 如果传入了已知 IP（例如历史记录中的 192.168.8.120），优先快速验证
            if (!string.IsNullOrWhiteSpace(knownIp))
            {
                var known = await ProbeSingleIpAsync(knownIp.Trim(), cancellationToken).ConfigureAwait(false);
                if (known != null)
                {
                    discoveredMap[known.IpAddress] = known;
                }
            }

            // 2. 并行执行 mDNS 组播发现与子网快速探测
            var mdnsTask = DiscoverViaMdnsAsync(discoveredMap, cancellationToken);
            var probeTask = ProbeLocalSubnetsAsync(discoveredMap, cancellationToken);

            await Task.WhenAll(mdnsTask, probeTask).ConfigureAwait(false);

            return discoveredMap.Values.OrderByDescending(d => d.IsFeiniuNas).ThenBy(d => d.IpAddress).ToList();
        }

        private async Task DiscoverViaMdnsAsync(ConcurrentDictionary<string, DiscoveredDevice> resultMap, CancellationToken cancellationToken)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.JoinMulticastGroup(MdnsAddress);

                byte[] query = BuildDnsPtrQuery("_services._dns-sd._udp.local");
                var endpoint = new IPEndPoint(MdnsAddress, MdnsPort);
                await udp.SendAsync(query, query.Length, endpoint).ConfigureAwait(false);

                byte[] smbQuery = BuildDnsPtrQuery("_smb._tcp.local");
                await udp.SendAsync(smbQuery, smbQuery.Length, endpoint).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2.5));

                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var receiveTask = udp.ReceiveAsync(cts.Token).AsTask();
                        var res = await receiveTask.ConfigureAwait(false);
                        string ip = res.RemoteEndPoint.Address.ToString();

                        string rawStr = Encoding.UTF8.GetString(res.Buffer);
                        string name = ParseDeviceNameFromMdns(rawStr, ip);
                        bool isFn = rawStr.IndexOf("fnos", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    rawStr.IndexOf("feiniu", StringComparison.OrdinalIgnoreCase) >= 0;

                        resultMap.TryAdd(ip, new DiscoveredDevice
                        {
                            Name = name,
                            IpAddress = ip,
                            IsFeiniuNas = isFn,
                            DiscoveryMethod = "mDNS 组播"
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // 忽略格式错误包
                    }
                }
            }
            catch
            {
                // mDNS 在某些受限网卡上可能抛异常，静默降级为 TCP 探测
            }
        }

        private async Task ProbeLocalSubnetsAsync(ConcurrentDictionary<string, DiscoveredDevice> resultMap, CancellationToken cancellationToken)
        {
            var ipsToProbe = new HashSet<string>();

            // 获取本机所有活动网卡 IP 与网段
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        string prefix = ip.Substring(0, ip.LastIndexOf('.') + 1);
                        // 添加常用默认 NAS IP (如 .1, .120, .200, .250, .254)
                        ipsToProbe.Add(prefix + "1");
                        ipsToProbe.Add(prefix + "120");
                        ipsToProbe.Add(prefix + "200");
                        ipsToProbe.Add(prefix + "254");
                    }
                }
            }

            // 补充常见家庭/办公网段探测目标
            ipsToProbe.Add("192.168.8.120");
            ipsToProbe.Add("192.168.1.120");
            ipsToProbe.Add("192.168.0.120");
            ipsToProbe.Add("192.168.31.120");

            var probeTasks = ipsToProbe.Select(async ip =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                var dev = await ProbeSingleIpAsync(ip, cancellationToken).ConfigureAwait(false);
                if (dev != null)
                {
                    resultMap.TryAdd(dev.IpAddress, dev);
                }
            });

            await Task.WhenAll(probeTasks).ConfigureAwait(false);
        }

        public async Task<DiscoveredDevice?> ProbeSingleIpAsync(string ip, CancellationToken cancellationToken = default)
        {
            bool hasSmb = await TestTcpPortAsync(ip, 445, 600, cancellationToken).ConfigureAwait(false);
            bool hasFnWeb = await TestTcpPortAsync(ip, 5667, 600, cancellationToken).ConfigureAwait(false);

            if (!hasSmb && !hasFnWeb) return null;

            string name = hasFnWeb ? $"飞牛 NAS ({ip})" : $"网络设备 ({ip})";
            return new DiscoveredDevice
            {
                Name = name,
                IpAddress = ip,
                SmbPort = 445,
                WebPort = 5667,
                IsFeiniuNas = hasFnWeb,
                DiscoveryMethod = "IP 端口扫描"
            };
        }

        private static async Task<bool> TestTcpPortAsync(string ip, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port, cancellationToken).AsTask();
                var timeoutTask = Task.Delay(timeoutMs, cancellationToken);

                var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (completed == connectTask && client.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // 连接超时或拒绝
            }
            return false;
        }

        private static byte[] BuildDnsPtrQuery(string domainName)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write((ushort)0); // ID
            bw.Write((ushort)0); // Flags
            bw.Write((ushort)IPAddress.HostToNetworkOrder((short)1)); // QDCOUNT = 1
            bw.Write((ushort)0); // ANCOUNT
            bw.Write((ushort)0); // NSCOUNT
            bw.Write((ushort)0); // ARCOUNT

            foreach (var part in domainName.Split('.'))
            {
                byte[] partBytes = Encoding.UTF8.GetBytes(part);
                bw.Write((byte)partBytes.Length);
                bw.Write(partBytes);
            }
            bw.Write((byte)0);

            bw.Write((ushort)IPAddress.HostToNetworkOrder((short)12)); // QTYPE = PTR
            bw.Write((ushort)IPAddress.HostToNetworkOrder((short)1));  // QCLASS = IN

            return ms.ToArray();
        }

        private static string ParseDeviceNameFromMdns(string raw, string fallbackIp)
        {
            // 简单从包中提取可读设备名称
            foreach (var line in raw.Split('\0', '\n', '\r'))
            {
                if (line.Length > 2 && line.Length < 30 && !line.Contains('.') && !line.Contains('/'))
                {
                    return line.Trim();
                }
            }
            return $"NAS ({fallbackIp})";
        }
    }
}
