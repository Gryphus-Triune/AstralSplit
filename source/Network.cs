using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AstralSplit
{
    internal sealed class Adapter
    {
        internal string Id, Name, Description;
        internal NetworkInterfaceType Type;
        internal IPAddress V4, V6;
        internal bool Hardware, Pppoe;
        internal int Mtu4, Mtu6;
        internal IPAddress[] DnsServers = new IPAddress[0];
        internal Tuple<IPAddress, int>[] Attached = new Tuple<IPAddress, int>[0];
        internal uint Index, Index6;
        internal ulong Luid;

        internal static IEnumerable<Adapter> Enumerate(bool onlyUp = true)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (onlyUp && nic.OperationalStatus != OperationalStatus.Up) continue;
                var ip = nic.GetIPProperties();
                var ipv4 = nic.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties() : null;
                var ipv6 = nic.Supports(NetworkInterfaceComponent.IPv6) ? ip.GetIPv6Properties() : null;
                int indexNumber = ipv4?.Index ?? ipv6?.Index ?? 0;
                if (indexNumber == 0) continue;
                uint index = (uint)indexNumber;
                ulong luid;
                if (Native.ConvertInterfaceIndexToLuid(index, out luid) != 0) continue;
                yield return new Adapter {
                    Id = nic.Id, Name = nic.Name, Description = nic.Description, Type = nic.NetworkInterfaceType,
                    Index = index, Index6 = (uint)(ipv6?.Index ?? 0), Luid = luid, Hardware = Native.IsHardware(luid),
                    Mtu4 = ipv4?.Mtu ?? 0, Mtu6 = ipv6?.Mtu ?? 0,
                    DnsServers = ip.DnsAddresses.ToArray(),
                    Attached = ip.UnicastAddresses.Where(address => address.PrefixLength > 0)
                        .Select(address => Tuple.Create(address.Address, address.PrefixLength)).ToArray(),
                    V4 = ip.UnicastAddresses.Select(x => x.Address).FirstOrDefault(x =>
                        x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x)),
                    V6 = ip.UnicastAddresses.Select(x => x.Address).FirstOrDefault(x =>
                        x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal && !IPAddress.IsLoopback(x))
                };
            }
        }
    }

    internal sealed class NetworkState
    {
        internal Adapter Astral, Physical4, Physical6;
        internal Adapter[] Adapters = new Adapter[0];
        internal Native.RouteRow[] Routes = new Native.RouteRow[0];
        internal IPAddress AstralGateway, AstralDns4, VpnDns6;
        internal Prefix4[] Guest4 = new Prefix4[0];
        internal bool AstralHasDefault, GenericVpn, GenericVpn4, GenericVpn6;
        internal bool Vpn4 { get { return GenericVpn ? GenericVpn4 : Astral?.V4 != null; } }
        internal bool Vpn6 { get { return GenericVpn && GenericVpn6; } }
        internal bool Capture4 { get { return !GenericVpn || Physical4 != null ||
            Vpn4 || Routes.Any(r => RoutePlan.AffectsPublicV4(r) && !IsAttached(r)); } }
        internal bool Capture6 { get { return Physical6 != null || GenericVpn &&
            (Vpn6 || Routes.Any(r => RoutePlan.AffectsPublicV6(r) && !IsAttached(r))); } }
        internal bool Ready { get { return GenericVpn ?
            (Vpn4 || Vpn6) && (Physical4 != null || Physical6 != null) :
            Physical4 != null && Astral != null && (AstralGateway != null || AstralHasDefault); } }
        internal string Unavailable
        {
            get {
                if (!GenericVpn && Physical4 == null) return "等待 IPv4 直连网络。连接后自动继续。";
                if (Physical4 == null && Physical6 == null) return "等待可用的直连网络。连接后自动继续。";
                if (!GenericVpn) return "等待星云 TUN。节点连接后自动继续。";
                if (Astral == null) return "等待已选 VPN 网卡。连接同一隧道后自动继续。";
                return "已选 VPN 缺少公网出口路由。请检查 VPN 的全局路由或出口节点。";
            }
        }

        internal static NetworkState Read(string vpnAdapterId = null)
        {
            var adapters = Adapter.Enumerate().ToArray();
            var routes = Native.Routes();
            if (adapters.Any(a => a.Type == NetworkInterfaceType.Ppp))
            {
                var pppoe = UplinkDiscovery.ConnectedPppoe();
                foreach (var adapter in adapters) adapter.Pppoe = pppoe.Contains(adapter.Luid);
            }
            return FromRoutes(adapters, routes, vpnAdapterId, Native.ReadInterface,
                UplinkDiscovery.GuestSources(adapters, routes, UplinkDiscovery.HotspotGateway()));
        }

        internal static NetworkState FromRoutes(Adapter[] adapters, Native.RouteRow[] routes, string vpnAdapterId,
            Func<ulong, ushort, Native.MetricState> readInterface, Prefix4[] guestSources)
        {
            var tunnel = vpnAdapterId == null ? adapters.FirstOrDefault(n => n.V4 != null && IsAstral(n)) :
                adapters.FirstOrDefault(n => EligibleVpn(n) && VpnPreference.Matches(n.Id, vpnAdapterId));
            // An explicitly selected Astral adapter still uses its established compatibility rules.
            bool generic = vpnAdapterId != null && (tunnel == null || !IsAstral(tunnel) || tunnel.V4 == null);
            var uplinks = adapters.Where(n => n != tunnel && EligibleUplink(n)).ToArray();
            var tunnelRoutes = tunnel == null ? new Native.RouteRow[0] : routes.Where(r =>
                r.Luid == tunnel.Luid && !Recovery.IsOwnedFallback(r)).ToArray();
            var snapshot = new NetworkState {
                Astral = tunnel, Routes = routes, Adapters = adapters,
                GenericVpn = generic,
                GenericVpn4 = generic && tunnel?.V4 != null && RoutePlan.CoversPublic4(tunnelRoutes),
                GenericVpn6 = generic && tunnel?.V6 != null && RoutePlan.CoversPublic6(tunnelRoutes),
                Guest4 = guestSources,
                AstralHasDefault = tunnelRoutes.Any(r => r.V4 && r.PrefixLength == 0),
                AstralGateway = tunnelRoutes.Where(r => r.V4 && r.ThroughGateway)
                    .Select(r => r.NextHop.Address).GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
                Physical4 = DefaultUplink(uplinks, routes, Native.IPv4, readInterface),
                Physical6 = DefaultUplink(uplinks, routes, Native.IPv6, readInterface)
            };
            if (tunnel != null)
            {
                snapshot.AstralDns4 = tunnel.DnsServers.FirstOrDefault(dns => dns.AddressFamily == AddressFamily.InterNetwork &&
                    (generic ? ValidResolverAddress(tunnel, dns) : tunnel.Attached.Any(a =>
                        !Equals(a.Item1, dns) && RoutePlan.SamePrefix(a.Item1, dns, a.Item2))));
                if (snapshot.Vpn6)
                    snapshot.VpnDns6 = tunnel.DnsServers.FirstOrDefault(dns =>
                        dns.AddressFamily == AddressFamily.InterNetworkV6 && ValidResolverAddress(tunnel, dns));
            }
            return snapshot;
        }

        internal static bool ValidResolverAddress(Adapter adapter, IPAddress dns)
        {
            return !IPAddress.IsLoopback(dns) && !Equals(dns, adapter.V4) && !Equals(dns, adapter.V6) &&
                !dns.IsIPv6LinkLocal && !dns.IsIPv6Multicast &&
                !Equals(dns, IPAddress.Any) && !Equals(dns, IPAddress.IPv6Any);
        }

        internal static bool IsAstral(Adapter adapter)
        {
            return adapter?.Description?.StartsWith("astral-tun", StringComparison.OrdinalIgnoreCase) == true;
        }

        internal static bool EligibleVpn(Adapter adapter)
        {
            return !adapter.Hardware && !adapter.Pppoe && (adapter.V4 != null || adapter.V6 != null) &&
                adapter.Type != NetworkInterfaceType.Loopback && !adapter.Name.StartsWith("ASplit-", StringComparison.Ordinal) &&
                Guid.TryParse(adapter.Id, out _);
        }

        internal static bool EligibleUplink(Adapter adapter)
        {
            return (adapter.Type == NetworkInterfaceType.Ppp ? adapter.Pppoe : adapter.Hardware || UplinkDiscovery.HyperV(adapter)) &&
                adapter.Type != NetworkInterfaceType.Tunnel && adapter.Type != NetworkInterfaceType.Loopback &&
                !adapter.Name.StartsWith("ASplit-", StringComparison.Ordinal) &&
                !IsAstral(adapter);
        }

        internal bool IsAttached(Native.RouteRow route)
        {
            if (route.ThroughGateway || route.PrefixLength == 0) return false;
            var nic = Adapters.FirstOrDefault(candidate => candidate.Luid == route.Luid);
            return nic != null && nic.Attached.Any(address => address.Item2 <= route.PrefixLength &&
                RoutePlan.SamePrefix(address.Item1, route.Destination.Address, address.Item2));
        }

        internal static Adapter DefaultUplink(Adapter[] uplinks, Native.RouteRow[] routes, ushort family,
            Func<ulong, ushort, Native.MetricState> readInterface)
        {
            var candidates = from route in routes
                where route.Destination.Family == family && route.PrefixLength == 0
                join nic in uplinks on route.Luid equals nic.Luid
                where family == Native.IPv4 ? nic.V4 != null : nic.V6 != null
                where UplinkDiscovery.DefaultRoute(nic, route)
                let metric = readInterface(nic.Luid, family)
                orderby (ulong)route.Metric + metric.Metric, nic.Index
                select nic;
            return candidates.FirstOrDefault();
        }

        internal string Signature(ulong auxLuid)
        {
            return Key(Astral) + "|" + Key(Physical4) + "|" + Key(Physical6) + "|" + AstralDns4 + "|" +
                VpnDns6 + "|" + GenericVpn4 + "|" + GenericVpn6 + "|" +
                String.Join(",", Guest4) + "|" +
                String.Join(",", Routes.Where(r => r.Luid != auxLuid && RelevantRoute(r))
                    .Select(r => r.Luid + ":" + r.Destination.Address + "/" + r.PrefixLength + "@" + r.NextHop.Address + "#" + r.Metric +
                        (!r.ThroughGateway && IsAttached(r) ? ":local" : ""))
                    .OrderBy(s => s, StringComparer.Ordinal));
        }

        bool RelevantRoute(Native.RouteRow route)
        {
            if (Astral != null && route.Luid == Astral.Luid && Recovery.IsOwnedFallback(route)) return false;
            if (!route.V4) return RoutePlan.AffectsPublicV6(route);
            if (route.PrefixLength == 0)
                return (Astral != null && route.Luid == Astral.Luid) ||
                    (Physical4 != null && route.V4 && route.Luid == Physical4.Luid) ||
                    (Physical6 != null && !route.V4 && route.Luid == Physical6.Luid);
            return RoutePlan.AffectsPublicV4(route);
        }

        static string Key(Adapter a)
        {
            return a == null ? "-" : a.Id + ":" + a.Luid + ":" + a.Index + ":" + a.Index6 + ":" + a.Name + ":" + a.V4 + ":" +
                a.V6 + ":" + a.Mtu4 + ":" + a.Mtu6 + ":" + a.Pppoe;
        }

        internal int CaptureMtu()
        {
            int KnownOrDefault(int mtu) { return mtu > 0 ? mtu : 1400; }
            int mtu = 1500;
            if (Physical4 != null) mtu = Math.Min(mtu, KnownOrDefault(Physical4.Mtu4));
            if (Vpn4) mtu = Math.Min(mtu, KnownOrDefault(Astral.Mtu4));
            if (Vpn6) mtu = Math.Min(mtu, KnownOrDefault(Astral.Mtu6));
            if (Physical6 != null) mtu = Math.Min(mtu, KnownOrDefault(Physical6.Mtu6));
            if (mtu < (Capture6 ? 1280 : 576))
                throw new InvalidOperationException("当前出口的 MTU 为 " + mtu + "，低于分流网卡支持的最小值。");
            return mtu;
        }
    }

    internal struct Prefix4 : IEquatable<Prefix4>
    {
        internal readonly uint Network;
        internal readonly int Bits;

        internal Prefix4(uint address, int bits)
        {
            Bits = bits;
            Network = address & Mask(bits);
        }

        static uint Mask(int bits) { return bits == 0 ? 0 : UInt32.MaxValue << (32 - bits); }
        internal uint LastAddress { get { return Network | ~Mask(Bits); } }

        internal IPAddress Address { get { return new IPAddress(new[] {
            (byte)(Network >> 24), (byte)(Network >> 16), (byte)(Network >> 8), (byte)Network }); } }

        internal static Prefix4 From(IPAddress address, int bits)
        {
            byte[] b = address.GetAddressBytes();
            return new Prefix4(((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3], bits);
        }

        internal bool Contains(Prefix4 other) { return Bits <= other.Bits && (other.Network & Mask(Bits)) == Network; }
        internal bool Overlaps(Prefix4 other) { return Contains(other) || other.Contains(this); }

        internal static Prefix4 Parse(string cidr)
        {
            var parts = cidr.Split('/');
            return From(IPAddress.Parse(parts[0]), Int32.Parse(parts[1]));
        }

        internal IEnumerable<Prefix4> Without(Prefix4 blocked)
        {
            if (blocked.Contains(this)) yield break;
            if (!Contains(blocked)) { yield return this; yield break; }
            foreach (var child in new[] { new Prefix4(Network, Bits + 1),
                new Prefix4(Network | (1u << (31 - Bits)), Bits + 1) })
                foreach (var part in child.Without(blocked)) yield return part;
        }

        public bool Equals(Prefix4 other) { return Network == other.Network && Bits == other.Bits; }
        public override bool Equals(object other) { return other is Prefix4 && Equals((Prefix4)other); }
        public override int GetHashCode() { return unchecked((int)Network * 397 ^ Bits); }
        public override string ToString() { return Address + "/" + Bits; }
    }

    internal sealed class RoutePlan
    {
        internal static readonly IPAddress LegacyNext4 = IPAddress.Parse("198.19.242.2");
        internal static readonly IPAddress LegacyNext6 = IPAddress.Parse("fd4a:5350:4c49::2");
        static readonly IPAddress preferred6 = IPAddress.Parse("fd4a:5350:4c49::1");
        static readonly IPAddress ula6 = IPAddress.Parse("fd00::1");
        internal const string FakeIp4 = "198.18.0.0/15";
        static readonly Prefix4 fakeIp = Prefix4.Parse(FakeIp4);
        static readonly IPAddress commonIPv4 = IPAddress.Parse("1.1.1.1");
        static readonly IPAddress defaultIPv4 = IPAddress.Parse("203.0.113.1");
        static readonly IPAddress defaultIPv6 = IPAddress.Parse("2001:db8::1");
        static readonly Tuple<IPAddress, byte>[] public6 = {
            Tuple.Create(IPAddress.Parse("2000::"), (byte)3),
            Tuple.Create(IPAddress.Parse("64:ff9b::"), (byte)96),
            Tuple.Create(IPAddress.Parse("64:ff9b:1::"), (byte)48)
        };
        static readonly Prefix4[] rfc1918 = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Select(Prefix4.Parse).ToArray();
        static readonly Prefix4[] preferred4 = new[] { "10.254.253.0/24", "172.30.252.0/24", "192.168.253.0/24" }
            .Select(Prefix4.Parse).Concat(rfc1918).ToArray();
        static readonly Prefix4[] private4 = new[] { "0.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/3" }
            .Select(Prefix4.Parse).Concat(rfc1918).ToArray();

        internal static bool IsPrivateSubnet(Prefix4 subnet) { return rfc1918.Any(p => p.Contains(subnet)); }

        static Prefix4[] Except4(IEnumerable<Prefix4> excluded)
        {
            return excluded.Aggregate((IEnumerable<Prefix4>)new[] { new Prefix4(0, 0) },
                (ranges, blocked) => ranges.SelectMany(range => range.Without(blocked)).ToArray()).ToArray();
        }

        internal static bool CoversPublic4(Native.RouteRow[] routes)
        {
            if (routes.Any(r => r.V4 && r.PrefixLength == 0)) return true;
            return Except4(private4.Concat(routes.Where(r => r.V4).OrderBy(r => r.PrefixLength)
                .Select(r => Prefix4.From(r.Destination.Address, r.PrefixLength)))).Length == 0;
        }

        internal static bool CoversPublic6(Native.RouteRow[] routes)
        {
            return Covers6(routes.Where(r => !r.V4).ToArray(), public6[0].Item1, public6[0].Item2);
        }

        static bool Covers6(Native.RouteRow[] routes, IPAddress prefix, int bits)
        {
            if (routes.Any(r => r.PrefixLength <= bits && SamePrefix(r.Destination.Address, prefix, r.PrefixLength))) return true;
            if (bits == 128 || !routes.Any(r => SamePrefix(r.Destination.Address, prefix, bits))) return false;
            byte[] right = prefix.GetAddressBytes();
            right[bits / 8] |= (byte)(128 >> (bits % 8));
            return Covers6(routes, prefix, bits + 1) && Covers6(routes, new IPAddress(right), bits + 1);
        }

        static bool PreservedHostRoute(NetworkState state, Native.RouteRow route)
        {
            // A VPN can install an exact host route on the uplink for its transport.
            var uplink = route.V4 ? state.Physical4 : state.Physical6;
            return state.GenericVpn && uplink != null && route.Luid == uplink.Luid && route.PrefixLength == (route.V4 ? 32 : 128);
        }

        static IEnumerable<Prefix4> OccupiedPrivate4(NetworkState state, ulong auxiliary = 0)
        {
            // Ignore default/split-default routes, which don't assign a private subnet.
            var routed = state.Routes.Where(r => (auxiliary == 0 || r.Luid != auxiliary) && r.V4 && r.PrefixLength >= 8)
                .Select(r => Prefix4.From(r.Destination.Address, r.PrefixLength));
            var attached = state.Adapters.Where(a => auxiliary == 0 || a.Luid != auxiliary)
                .SelectMany(a => a.Attached.Where(p => p.Item1.AddressFamily == AddressFamily.InterNetwork)
                    .Select(p => Prefix4.From(p.Item1, p.Item2))
                    .Concat(a.DnsServers.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Select(ip => Prefix4.From(ip, 32))));
            return routed.Concat(attached).Where(p => rfc1918.Any(space => space.Overlaps(p))).Distinct();
        }

        internal static Prefix4 SelectAuxiliary4(NetworkState state)
        {
            var occupied = OccupiedPrivate4(state).OrderBy(p => p.Bits).ToArray();
            foreach (var pool in preferred4)
            {
                var remaining = occupied.Where(pool.Overlaps).Aggregate((IEnumerable<Prefix4>)new[] { pool },
                    (ranges, used) => ranges.SelectMany(range => range.Without(used)).Where(range => range.Bits <= 30).ToArray());
                if (remaining.Any()) return new Prefix4(remaining.First().Network, 30);
            }
            throw new InvalidOperationException("没有找到可供分流使用且不与现有网段重叠的私网 /30。");
        }

        static IEnumerable<Tuple<IPAddress,int>> OccupiedPrivate6(NetworkState state, ulong auxiliary = 0)
        {
            var routes = state.Routes.Where(r => !r.V4 && r.PrefixLength >= 7 && (auxiliary == 0 || r.Luid != auxiliary))
                .Select(r => Tuple.Create(r.Destination.Address, (int)r.PrefixLength));
            var addresses = state.Adapters.Where(a => auxiliary == 0 || a.Luid != auxiliary)
                .SelectMany(a => a.Attached.Concat(a.DnsServers.Select(ip => Tuple.Create(ip, 128))));
            return routes.Concat(addresses).Where(p => p.Item1.AddressFamily == AddressFamily.InterNetworkV6 &&
                p.Item2 > 0 && p.Item2 <= 128 && SamePrefix(p.Item1, ula6, Math.Min(8, p.Item2))).Distinct();
        }

        internal static IPAddress SelectAuxiliary6(NetworkState state)
        {
            var occupied = OccupiedPrivate6(state).OrderBy(p => p.Item2).ToArray();
            foreach (var start in new[] { preferred6, ula6 })
            {
                var candidate = start;
                while (candidate.GetAddressBytes()[0] == 0xfd)
                {
                    var block = occupied.FirstOrDefault(p => SamePrefix(candidate, p.Item1, Math.Min(126, p.Item2)));
                    if (block == null) return candidate;
                    // Jump to the next prefix after the whole conflicting block (at least /126).
                    int lastBit = Math.Min(126, block.Item2) - 1;
                    int octet = lastBit / 8, increment = 128 >> (lastBit % 8);
                    byte[] bytes = candidate.GetAddressBytes();
                    Array.Clear(bytes, octet + 1, 15 - octet);
                    bytes[octet] &= (byte)(256 - increment);
                    for (int i = octet; i >= 0 && increment != 0; i--) {
                        int sum = bytes[i] + increment;
                        bytes[i] = (byte)sum;
                        increment = sum >> 8;
                    }
                    bytes[15] |= 1;
                    candidate = new IPAddress(bytes);
                }
            }
            throw new InvalidOperationException("没有找到可供分流使用且不与现有网段重叠的 IPv6 私网 /126。");
        }

        internal readonly Prefix4[] V4;
        internal readonly Tuple<IPAddress, byte>[] V6;
        internal readonly Prefix4 AuxiliarySubnet;
        internal readonly IPAddress Auxiliary4, Next4, Auxiliary6, Next6;
        internal readonly int Mtu;
        readonly HashSet<Prefix4> v4Set;
        readonly HashSet<Tuple<IPAddress, byte>> v6Set;

        internal RoutePlan(NetworkState state)
        {
            if (state.Capture4) {
                AuxiliarySubnet = SelectAuxiliary4(state);
                Auxiliary4 = new Prefix4(AuxiliarySubnet.Network + 1, 32).Address;
                Next4 = new Prefix4(AuxiliarySubnet.Network + 2, 32).Address;
            }
            if (state.Capture6) {
                Auxiliary6 = SelectAuxiliary6(state);
                var next = Auxiliary6.GetAddressBytes();
                next[15]++;
                Next6 = new IPAddress(next);
            }
            Mtu = state.CaptureMtu();
            V4 = Auxiliary4 == null ? new Prefix4[0] : CaptureRoutes4(state);
            var remote6 = state.Routes.Where(route => !route.V4 && AffectsPublicV6(route) && !state.IsAttached(route) &&
                !PreservedHostRoute(state, route)).ToArray();
            if (remote6.Length != 0 && Auxiliary6 == null)
                throw new InvalidOperationException("检测到可通往公网的 IPv6 路由，但没有可确认的 IPv6 默认出口。为避免已选应用经 IPv6 绕过分流，本次未启用。");
            foreach (var route in remote6)
                if (!route.ThroughGateway && route.Luid != state.Astral.Luid &&
                    !(state.Physical6?.Pppoe == true && route.Luid == state.Physical6.Luid)) throw OnLinkConflict(route);
            V6 = Auxiliary6 == null ? new Tuple<IPAddress, byte>[0] : remote6
                .Where(route => public6.Any(prefix => prefix.Item2 < route.PrefixLength &&
                    SamePrefix(route.Destination.Address, prefix.Item1, prefix.Item2)))
                .Select(route => Tuple.Create(route.Destination.Address, route.PrefixLength))
                .Concat(public6).Distinct().ToArray();
            v4Set = new HashSet<Prefix4>(V4);
            v6Set = new HashSet<Tuple<IPAddress, byte>>(V6);
        }

        static Prefix4[] CaptureRoutes4(NetworkState state)
        {
            var localCandidates = state.Routes.Where(route => route.V4 && route.PrefixLength > 0 && !route.ThroughGateway &&
                (route.Luid != state.Astral.Luid || state.IsAttached(route)) &&
                (state.Physical4?.Pppoe != true || route.Luid != state.Physical4.Luid || state.IsAttached(route)) &&
                !PreservedHostRoute(state, route)).ToArray();
            var excluded = state.GenericVpn ?
                state.Routes.Where(r => r.V4 && PreservedHostRoute(state, r)).Select(r => Prefix4.From(r.Destination.Address, 32)) :
                new[] { state.AstralGateway, state.AstralDns4, state.Astral.V4 }.Where(ip => ip != null)
                    .Select(ip => Prefix4.From(ip, 32)).Where(fakeIp.Contains);
            var onLink = localCandidates.SelectMany(route => {
                var prefix = Prefix4.From(route.Destination.Address, route.PrefixLength);
                // An on-link Fake-IP pool on Astral is still routed per application.
                // Keep its actual TUN peers and any local host routes reachable.
                return !state.GenericVpn && route.Luid == state.Astral.Luid && prefix.Bits < 32 ?
                    prefix.Without(fakeIp) : new[] { prefix };
            }).Concat(excluded);
            var open = Except4(private4.Concat(onLink).Distinct());
            if (open.Length == 0)
                throw new InvalidOperationException("其他网卡的直连路由已覆盖所有公网 IPv4 地址，无法启用分流。请检查其他 VPN 或网卡的路由。");
            foreach (var half in new[] { new Prefix4(0, 1), new Prefix4(0x80000000u, 1) })
            {
                if (!open.Any(range => range.Contains(half) || half.Contains(range)))
                    throw new InvalidOperationException("其他网卡的直连路由已覆盖 " + half + " 范围内的所有公网地址，无法启用分流。请检查其他 VPN 或网卡的路由。");
            }
            foreach (var route in localCandidates)
                if (AffectsPublicV4(route) && !state.IsAttached(route)) throw OnLinkConflict(route);

            // Copy existing more-specific public routes so they cannot outrank the new TUN.
            var existing = state.Routes.Where(r => r.V4 && (r.ThroughGateway || r.Luid == state.Astral.Luid ||
                state.Physical4?.Pppoe == true && r.Luid == state.Physical4.Luid) && r.PrefixLength > 0 &&
                !PreservedHostRoute(state, r))
                .Select(r => Prefix4.From(r.Destination.Address, r.PrefixLength));
            return open.Concat(existing.Where(p => open.Any(range => range.Contains(p))))
                .Distinct().OrderBy(p => p.Bits).ThenBy(p => p.Network).ToArray();
        }

        internal bool AddressStillAvailable(NetworkState live, ulong auxiliary)
        {
            return (Auxiliary4 == null || !OccupiedPrivate4(live, auxiliary).Any(AuxiliarySubnet.Overlaps)) &&
                (Auxiliary6 == null || !OccupiedPrivate6(live, auxiliary).Any(p => SamePrefix(Auxiliary6, p.Item1, Math.Min(126, p.Item2))));
        }

        static InvalidOperationException OnLinkConflict(Native.RouteRow route)
        {
            return new InvalidOperationException("网卡 " + route.Index + " 有一条不属于本机直连子网的公网路由（" +
                route.Destination.Address + "/" + route.PrefixLength + "），无法保证按应用分流。请检查其他 VPN 或网卡的路由。");
        }

        internal static bool SamePrefix(IPAddress left, IPAddress right, int bits)
        {
            if (left.AddressFamily != right.AddressFamily) return false;
            byte[] first = left.GetAddressBytes(), second = right.GetAddressBytes();
            int whole = bits / 8, remaining = bits % 8;
            for (int index = 0; index < whole; index++) if (first[index] != second[index]) return false;
            return remaining == 0 || ((first[whole] ^ second[whole]) >> (8 - remaining)) == 0;
        }

        internal static bool AffectsPublicV6(Native.RouteRow route)
        {
            return !route.V4 && public6.Any(prefix => SamePrefix(route.Destination.Address, prefix.Item1,
                Math.Min(prefix.Item2, route.PrefixLength)));
        }

        internal static bool AffectsPublicV4(Native.RouteRow route)
        {
            if (!route.V4) return false;
            var prefix = Prefix4.From(route.Destination.Address, route.PrefixLength);
            return !private4.Any(local => local.Contains(prefix));
        }

        internal bool IsInstalled(Native.RouteRow[] routes, ulong auxiliary)
        {
            var installed = routes.Where(r => r.Luid == auxiliary && r.Metric == 0).ToArray();
            return v4Set.SetEquals(installed.Where(r => r.V4 && Equals(r.NextHop.Address, Next4))
                .Select(r => Prefix4.From(r.Destination.Address, r.PrefixLength))) &&
                v6Set.SetEquals(installed.Where(r => !r.V4 && Equals(r.NextHop.Address, Next6))
                .Select(r => Tuple.Create(r.Destination.Address, r.PrefixLength)));
        }

        internal void Install(Adapter auxiliary, Action checkStop)
        {
            if (V4.Length > 0) Native.WriteMetric(auxiliary.Luid, Native.IPv4, false, 0);
            if (V6.Length > 0) Native.WriteMetric(auxiliary.Luid, Native.IPv6, false, 0);
            foreach (var range in V4)
            {
                checkStop();
                Native.AddRoute(auxiliary.Luid, range.Address, (byte)range.Bits, Next4, 0);
            }
            foreach (var range in V6)
            {
                checkStop();
                Native.AddRoute(auxiliary.Luid, range.Item1, range.Item2, Next6, 0);
            }
        }

        internal void Verify(Adapter auxiliary, NetworkState state, Action checkStop)
        {
            foreach (var range in V4)
            {
                checkStop();
                // The last address in a prefix also exercises subnet boundaries.
                if (Native.Best(new Prefix4(range.LastAddress, 32).Address).Luid != auxiliary.Luid)
                    throw new InvalidOperationException("Windows 有优先级更高的 IPv4 路由，已恢复网络。");
            }
            foreach (var range in V6)
            {
                checkStop();
                if (Native.Best(range.Item1).Luid != auxiliary.Luid)
                    throw new InvalidOperationException("Windows 有优先级更高的 IPv6 路由，已恢复网络。");
            }

            VerifyOutlets(state, checkSocket: true, checkStop: checkStop);
        }

        internal static void VerifyOutlets(NetworkState state, bool checkSocket, Action checkStop = null)
        {
            if (state.GenericVpn)
            {
                VpnReachability.Verify(state, checkSocket, checkStop);
                return;
            }
            VerifyOutlet(state.Astral, "星云 TUN 出口", checkSocket);
            VerifyOutlet(state.Physical4, "IPv4 直连出口", checkSocket);
            if (state.Physical6 != null) VerifyBoundRoute(state.Physical6, "IPv6 直连出口", defaultIPv6);
        }

        static void VerifyBoundRoute(Adapter adapter, string label, IPAddress target)
        {
            var route = Native.Best(target, adapter.Luid);
            if (route.Luid != adapter.Luid)
                throw new InvalidOperationException(label + "（" + adapter.Name + "）被 Windows 改走了网卡 " + route.Index + "，无法确认出口。");
        }

        internal static void VerifyOutlet(Adapter adapter, string label, bool checkSocket = true)
        {
            const SocketOptionName IpUnicastIf = (SocketOptionName)31;
            VerifyBoundRoute(adapter, label, commonIPv4);
            VerifyBoundRoute(adapter, label, defaultIPv4);
            if (!checkSocket) return;

            string outlet = label + "（" + adapter.Name + "）";
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastIf, IPAddress.HostToNetworkOrder((int)adapter.Index));
                    // The UDP socket selects a source address without sending any data.
                    socket.Connect(new IPEndPoint(defaultIPv4, 443));
                    var chosen = (IPEndPoint)socket.LocalEndPoint;
                    if (!Equals(chosen.Address, adapter.V4))
                        throw new InvalidOperationException(outlet + "的本地地址不匹配。预期 " + adapter.V4 + "，Windows 选择了 " + chosen.Address + "。");
                }
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(outlet + "无法绑定到该网卡（Windows " + ex.NativeErrorCode + "）。", ex);
            }
        }
    }

    internal static class Native
    {
        internal const ushort IPv4 = 2, IPv6 = 23;
        internal static bool InterfaceGone(int error) { return error == 2 || error == 1167 || error == 1168; }

        const int IfRowStatusFlagsOffset = 8 + 4 + 16 + 2 * 257 * 2 + 4 + 2 * 32 + 7 * 4;
        [StructLayout(LayoutKind.Explicit, Size = 2048)]
        struct IfRow2
        {
            [FieldOffset(0)] internal ulong Luid;
            [FieldOffset(IfRowStatusFlagsOffset)] internal byte StatusFlags;
        }

        [DllImport("iphlpapi.dll")] static extern uint GetIfEntry2(ref IfRow2 row);
        internal static bool IsHardware(ulong luid)
        {
            var row = new IfRow2 { Luid = luid };
            return GetIfEntry2(ref row) == 0 && (row.StatusFlags & 1) != 0;
        }

        [StructLayout(LayoutKind.Explicit, Size = 28)]
        internal struct SockAddr
        {
            [FieldOffset(0)] internal ushort Family;
            [FieldOffset(4)] uint v4;
            [FieldOffset(8)] uint a;
            [FieldOffset(12)] uint b;
            [FieldOffset(16)] uint c;
            [FieldOffset(20)] uint d;
            [FieldOffset(24)] uint scope;

            internal IPAddress Address { get {
                if (Family != IPv6) return new IPAddress(v4);
                return new IPAddress(new[] { a, b, c, d }.SelectMany(x => BitConverter.GetBytes(x)).ToArray(), scope);
            } }
            internal bool IsGlobal6 { get { return Family == IPv6 && (a & 0xE0) == 0x20; } }

            internal static SockAddr From(IPAddress address)
            {
                byte[] bytes = address.GetAddressBytes();
                if (bytes.Length == 4) return new SockAddr { Family = IPv4, v4 = BitConverter.ToUInt32(bytes, 0) };
                return new SockAddr { Family = IPv6, a = BitConverter.ToUInt32(bytes, 0), b = BitConverter.ToUInt32(bytes, 4),
                    c = BitConverter.ToUInt32(bytes, 8), d = BitConverter.ToUInt32(bytes, 12), scope = (uint)address.ScopeId };
            }
        }

        // Layout of the SDK's MIB_IPFORWARD_ROW2 on Windows x64.
        [StructLayout(LayoutKind.Explicit, Size = 104)]
        internal struct RouteRow
        {
            [FieldOffset(0)] internal ulong Luid;
            [FieldOffset(8)] internal uint Index;
            [FieldOffset(12)] internal SockAddr Destination;
            [FieldOffset(40)] internal byte PrefixLength;
            [FieldOffset(44)] internal SockAddr NextHop;
            [FieldOffset(84)] internal uint Metric;
            [FieldOffset(88)] internal uint Protocol;
            internal bool V4 { get { return Destination.Family == IPv4; } }
            internal bool ThroughGateway { get { return !NextHop.Address.Equals(V4 ? IPAddress.Any : IPAddress.IPv6Any); } }
        }

        internal struct MetricState
        {
            internal bool Automatic;
            internal bool Forwarding;
            internal uint Metric, Mtu;
        }

        [DllImport("iphlpapi.dll")] internal static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);
        [DllImport("iphlpapi.dll")] static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
        [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr table);
        [DllImport("iphlpapi.dll")] static extern void InitializeIpForwardEntry(ref RouteRow row);
        [DllImport("iphlpapi.dll")] static extern uint CreateIpForwardEntry2(ref RouteRow row);
        [DllImport("iphlpapi.dll")] static extern uint DeleteIpForwardEntry2(ref RouteRow row);
        [DllImport("iphlpapi.dll")] static extern uint GetBestRoute2(IntPtr luid, uint index, IntPtr source,
            ref SockAddr destination, uint options, out RouteRow route, out SockAddr bestSource);
        [DllImport("iphlpapi.dll")] static extern uint GetBestRoute2(ref ulong luid, uint index, IntPtr source,
            ref SockAddr destination, uint options, out RouteRow route, out SockAddr bestSource);
        [DllImport("iphlpapi.dll")] static extern void InitializeIpInterfaceEntry(IntPtr row);
        [DllImport("iphlpapi.dll")] static extern uint GetIpInterfaceEntry(IntPtr row);
        [DllImport("iphlpapi.dll")] static extern uint SetIpInterfaceEntry(IntPtr row);

        internal static RouteRow[] Routes()
        {
            IntPtr table;
            Check(GetIpForwardTable2(0, out table), "读取 Windows 路由");
            try
            {
                int count = Marshal.ReadInt32(table);
                var routes = new RouteRow[count];
                for (int i = 0; i < count; i++)
                    routes[i] = Marshal.PtrToStructure<RouteRow>(IntPtr.Add(table, 8 + i * 104));
                return routes;
            }
            finally { FreeMibTable(table); }
        }

        internal static RouteRow Best(IPAddress address, ulong luid = 0)
        {
            var destination = SockAddr.From(address);
            RouteRow route;
            SockAddr source;
            uint result = luid == 0 ? GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref destination, 0, out route, out source) :
                GetBestRoute2(ref luid, 0, IntPtr.Zero, ref destination, 0, out route, out source);
            Check(result, "核对路由");
            return route;
        }

        internal static void AddRoute(ulong luid, IPAddress prefix, byte length, IPAddress nextHop, uint metric)
        {
            if (!TryAddRoute(luid, prefix, length, nextHop, metric)) Check(5010, "添加会话路由：已存在");
        }

        internal static bool TryAddRoute(ulong luid, IPAddress prefix, byte length, IPAddress nextHop, uint metric)
        {
            var row = new RouteRow();
            InitializeIpForwardEntry(ref row);
            row.Luid = luid;
            row.Destination = SockAddr.From(prefix);
            row.PrefixLength = length;
            row.NextHop = SockAddr.From(nextHop);
            row.Metric = metric;
            row.Protocol = 3; // NETMGMT, created through Windows IP Helper.
            uint result = CreateIpForwardEntry2(ref row);
            if (result == 5010 || result == 183) return false;
            Check(result, "添加会话路由");
            return true;
        }

        internal static void RemoveRoute(RouteRow row)
        {
            var code = DeleteIpForwardEntry2(ref row);
            if (!InterfaceGone((int)code)) Check(code, "移除会话路由");
        }

        // Offset constants are for the Windows x64 MIB_IPINTERFACE_ROW structure.
        const int ForwardingOffset = 41, AutomaticMetricOffset = 44, MetricOffset = 148, MtuOffset = 152;

        static IntPtr ReadInterfaceRow(ulong luid, ushort family)
        {
            var row = Marshal.AllocHGlobal(256);
            InitializeIpInterfaceEntry(row);
            Marshal.WriteInt16(row, 0, (short)family);
            Marshal.WriteInt64(row, 8, (long)luid);
            uint result = GetIpInterfaceEntry(row);
            if (result == 0) return row;
            Marshal.FreeHGlobal(row);
            Check(result, "读取网卡设置");
            return IntPtr.Zero;
        }

        internal static MetricState ReadInterface(ulong luid, ushort family)
        {
            var row = ReadInterfaceRow(luid, family);
            try { return new MetricState { Automatic = Marshal.ReadByte(row, AutomaticMetricOffset) != 0,
                Forwarding = Marshal.ReadByte(row, ForwardingOffset) != 0,
                Metric = (uint)Marshal.ReadInt32(row, MetricOffset), Mtu = (uint)Marshal.ReadInt32(row, MtuOffset) }; }
            finally { Marshal.FreeHGlobal(row); }
        }

        internal static void WriteMetric(ulong luid, ushort family, bool automatic, uint metric, bool? forwarding = null)
        {
            var row = ReadInterfaceRow(luid, family);
            try
            {
                Marshal.WriteByte(row, AutomaticMetricOffset, automatic ? (byte)1 : (byte)0);
                Marshal.WriteInt32(row, MetricOffset, (int)metric);
                if (forwarding.HasValue) Marshal.WriteByte(row, ForwardingOffset, forwarding.Value ? (byte)1 : (byte)0);
                Check(SetIpInterfaceEntry(row), "更新会话网卡优先级");
            }
            finally { Marshal.FreeHGlobal(row); }
        }

        internal static void Check(uint result, string operation)
        {
            if (result != 0) throw new Win32Exception((int)result, operation + "失败（Windows " + result + "）");
        }
    }
}
