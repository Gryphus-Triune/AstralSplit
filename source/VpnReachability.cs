using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AstralSplit
{
    internal static class VpnReachability
    {
        internal enum ProbeKind { Tcp, Dns, Stun }

        static readonly IPAddress[] Public4 = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("223.5.5.5") };
        static readonly IPAddress[] Public6 = { IPAddress.Parse("2606:4700:4700::1111"), IPAddress.Parse("2400:3200::1") };
        static readonly IPAddress[] Stun4 = { IPAddress.Parse("162.159.207.1"), IPAddress.Parse("141.101.90.1") };
        static readonly IPAddress[] Stun6 = { IPAddress.Parse("2606:4700:48::1"), IPAddress.Parse("2a06:98c1:3200::1") };

        internal static void Verify(NetworkState network, bool exchangePackets, Action checkStop)
        {
            if (network.Vpn4) VerifyFamily(network.Astral, network.Astral.V4, network.AstralDns4, true, exchangePackets, checkStop);
            if (network.Vpn6) VerifyFamily(network.Astral, network.Astral.V6, network.VpnDns6, true, exchangePackets, checkStop);
            if (network.Physical4 != null)
                VerifyFamily(network.Physical4, network.Physical4.V4, null, false, exchangePackets, checkStop);
            if (network.Physical6 != null)
                VerifyFamily(network.Physical6, network.Physical6.V6, null, false, exchangePackets, checkStop);
        }

        static void VerifyFamily(Adapter adapter, IPAddress local, IPAddress resolver, bool vpn, bool packets, Action checkStop)
        {
            bool ipv4 = local.AddressFamily == AddressFamily.InterNetwork;
            var endpoints = ipv4 ? Public4 : Public6;
            var stun = ipv4 ? Stun4 : Stun6;
            var dns = resolver != null ? new[] { resolver } : vpn ? endpoints :
                adapter.DnsServers.Where(address => address.AddressFamily == local.AddressFamily &&
                    NetworkState.ValidResolverAddress(adapter, address)).Take(2).Concat(endpoints).Distinct().ToArray();
            string label = (vpn ? "VPN " : "直连 ") + (ipv4 ? "IPv4" : "IPv6") + "（" + adapter.Name + "）";
            if (!endpoints.Any(address => HasRoute(adapter, address)) ||
                resolver != null && !HasRoute(adapter, resolver))
                throw new InvalidOperationException(label + "缺少可绑定的出口路由。请检查 VPN 的全局路由、DNS 或阻止直连的设置。");
            if (!packets) return;
            bool Exchange(IPAddress[] targets, ProbeKind kind) =>
                targets.Any(address => Probe(adapter, local, address, kind, checkStop));
            if (!Exchange(endpoints, ProbeKind.Tcp))
                throw new InvalidOperationException(label + "的 TCP 探测没有收到响应。请检查网络和 VPN 的断线保护设置。");
            if (resolver != null && !Exchange(dns, ProbeKind.Dns))
                throw new InvalidOperationException(label + "配置的 DNS 没有回应 UDP 探测。请检查 VPN 的 DNS 设置。");
            if (resolver == null && !Exchange(stun, ProbeKind.Stun) && !Exchange(dns, ProbeKind.Dns))
                throw new InvalidOperationException(label + "的 UDP 探测没有收到响应。请检查网络和 VPN 的 UDP 支持。");
        }

        static bool HasRoute(Adapter adapter, IPAddress remote)
        {
            try { return Native.Best(remote, adapter.Luid).Luid == adapter.Luid; }
            catch (Win32Exception ex) when (Native.InterfaceGone(ex.NativeErrorCode) || ex.NativeErrorCode == 1231 || ex.NativeErrorCode == 1232)
            {
                return false;
            }
        }

        internal static bool Probe(Adapter adapter, IPAddress local, IPAddress remote, ProbeKind kind, Action checkStop,
            int? port = null)
        {
            checkStop?.Invoke();
            if (!HasRoute(adapter, remote)) return false;
            bool ipv4 = local.AddressFamily == AddressFamily.InterNetwork;
            bool tcp = kind == ProbeKind.Tcp;
            const SocketOptionName UnicastInterface = (SocketOptionName)31;
            try
            {
                using (var socket = new Socket(local.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram,
                    tcp ? ProtocolType.Tcp : ProtocolType.Udp))
                {
                    socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, UnicastInterface,
                        ipv4 ? IPAddress.HostToNetworkOrder((int)adapter.Index) : (int)adapter.Index6);
                    socket.Bind(new IPEndPoint(local, 0));
                    var endpoint = new IPEndPoint(remote, port ?? (tcp ? 443 : kind == ProbeKind.Stun ? 3478 : 53));
                    if (tcp)
                    {
                        var connecting = socket.BeginConnect(endpoint, null, null);
                        using (var signal = connecting.AsyncWaitHandle)
                        {
                            if (!signal.WaitOne(1200)) return false;
                            socket.EndConnect(connecting);
                            return socket.Connected;
                        }
                    }
                    socket.ReceiveTimeout = 1000;
                    socket.Connect(endpoint);
                    byte[] query = Datagram(kind);
                    socket.Send(query);
                    byte[] response = new byte[512];
                    int length = socket.Receive(response);
                    if (kind == ProbeKind.Stun)
                        return length >= 20 && response[0] == 1 && response[1] == 1 &&
                            response.Skip(4).Take(16).SequenceEqual(query.Skip(4));
                    // A valid DNS refusal still demonstrates bidirectional UDP.
                    return length >= 12 && response[0] == query[0] && response[1] == query[1] &&
                        (response[2] & 0xF8) == 0x80;
                }
            }
            catch (SocketException) { return false; }
        }

        static byte[] Datagram(ProbeKind kind)
        {
            var nonce = Guid.NewGuid().ToByteArray();
            if (kind == ProbeKind.Stun)
            {
                // STUN binding request with no attributes.
                byte[] request = { 0, 1, 0, 0, 0x21, 0x12, 0xA4, 0x42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                Array.Copy(nonce, 0, request, 8, 12);
                return request;
            }
            // A root NS lookup avoids sending any user's DNS names.
            return new byte[] { nonce[0], nonce[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1 };
        }
    }
}
