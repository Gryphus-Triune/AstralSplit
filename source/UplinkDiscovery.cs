using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AstralSplit
{
    internal static class UplinkDiscovery
    {
        internal static bool HyperV(Adapter adapter)
        {
            const string driver = "Hyper-V Virtual Ethernet Adapter";
            return adapter.Type == NetworkInterfaceType.Ethernet &&
                (String.Equals(adapter.Description, driver, StringComparison.OrdinalIgnoreCase) ||
                (adapter.Description?.StartsWith(driver + " #", StringComparison.OrdinalIgnoreCase) == true &&
                    Int32.TryParse(adapter.Description.Substring(driver.Length + 2), out _)));
        }

        internal static bool DefaultRoute(Adapter adapter, Native.RouteRow route)
        {
            if (route.PrefixLength != 0 || !NetworkState.EligibleUplink(adapter)) return false;
            if (IsWsl(adapter)) return false;
            if (!route.ThroughGateway) return adapter.Pppoe;
            if (!HyperV(adapter)) return true;

            // A management vNIC is an uplink only when it has a real next hop.
            var gateway = route.NextHop.Address;
            if (gateway.IsIPv6LinkLocal) return gateway.ScopeId == 0 || gateway.ScopeId == adapter.Index;
            return adapter.Attached.Any(address => !Equals(address.Item1, gateway) &&
                RoutePlan.SamePrefix(address.Item1, gateway, address.Item2));
        }

        static bool IsWsl(Adapter adapter)
        {
            return HyperV(adapter) &&
                (String.Equals(adapter.Name, "vEthernet (WSL)", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(adapter.Name, "vEthernet (WSL (Hyper-V firewall))", StringComparison.OrdinalIgnoreCase));
        }

        internal static Prefix4[] GuestSources(Adapter[] adapters, Native.RouteRow[] routes, IPAddress hotspotGateway)
        {
            var uplinks = new HashSet<ulong>(routes.Where(r => r.V4 && r.PrefixLength == 0).Select(r => r.Luid));
            var hostAddresses = adapters.SelectMany(a => a.Attached.Select(p => p.Item1).Concat(new[] { a.V4 }))
                .Where(a => a != null && a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => Prefix4.From(a, 32)).Distinct().ToArray();
            var sources = new List<Prefix4>();
            foreach (var adapter in adapters)
            {
                if (adapter.V4 == null || uplinks.Contains(adapter.Luid)) continue;
                bool hotspot = Equals(adapter.V4, hotspotGateway) &&
                    adapter.Description?.StartsWith("Microsoft Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) == true;
                if (!IsWsl(adapter) && !hotspot) continue;

                var assigned = adapter.Attached.FirstOrDefault(p => Equals(p.Item1, adapter.V4) && p.Item2 >= 16 && p.Item2 <= 30);
                if (assigned == null) continue;
                var subnet = Prefix4.From(assigned.Item1, assigned.Item2);
                if (!RoutePlan.IsPrivateSubnet(subnet) ||
                    !routes.Any(r => r.V4 && r.Luid == adapter.Luid && !r.ThroughGateway &&
                        r.PrefixLength == subnet.Bits && Prefix4.From(r.Destination.Address, r.PrefixLength).Equals(subnet))) continue;
                if (adapters.Any(other => other.Luid != adapter.Luid &&
                    other.Attached.Any(p => p.Item1.AddressFamily == AddressFamily.InterNetwork &&
                        Prefix4.From(p.Item1, p.Item2).Overlaps(subnet)))) continue;

                var excluded = hostAddresses.Concat(new[] {
                    new Prefix4(subnet.Network, 32), new Prefix4(subnet.LastAddress, 32)
                }).Where(subnet.Contains);
                sources.AddRange(excluded.Aggregate((IEnumerable<Prefix4>)new[] { subnet },
                    (ranges, address) => ranges.SelectMany(range => range.Without(address)).ToArray()));
            }
            return sources.Distinct().OrderBy(p => p.Network).ThenBy(p => p.Bits).ToArray();
        }

        internal static IPAddress HotspotGateway()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters"))
                {
                    return IPAddress.TryParse(key?.GetValue("ScopeAddress") as string, out var address) ? address : null;
                }
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (System.IO.IOException) { return null; }
        }

        // RASCONNW uses four-byte packing, including the connection handle and NET_LUID.
        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
        struct RasConnection
        {
            internal uint Size;
            internal IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string EntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] internal string DeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Phonebook;
            internal uint SubEntry;
            internal Guid EntryGuid;
            internal uint Flags;
            internal ulong Luid;
        }

        [DllImport("rasapi32.dll", ExactSpelling = true)]
        static extern uint RasEnumConnectionsW(IntPtr connections, ref uint bytes, out uint count);

        internal static HashSet<ulong> ConnectedPppoe()
        {
            const uint bufferTooSmall = 603;
            var adapters = new HashSet<ulong>();
            int size = Marshal.SizeOf<RasConnection>();
            uint capacity = (uint)size;
            for (int attempt = 0; attempt < 3 && capacity <= size * 256; attempt++)
            {
                IntPtr rows = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    Marshal.WriteInt32(rows, size);
                    uint bytes = capacity;
                    uint result = RasEnumConnectionsW(rows, ref bytes, out uint count);
                    if (result == bufferTooSmall && bytes > capacity) { capacity = bytes; continue; }
                    if (result != 0 || count > capacity / size) break;
                    for (int i = 0; i < count; i++)
                    {
                        var connection = Marshal.PtrToStructure<RasConnection>(IntPtr.Add(rows, i * size));
                        if (connection.Luid != 0 && String.Equals(connection.DeviceType, "PPPoE", StringComparison.OrdinalIgnoreCase))
                            adapters.Add(connection.Luid);
                    }
                    break;
                }
                finally { Marshal.FreeHGlobal(rows); }
            }
            return adapters;
        }
    }
}
