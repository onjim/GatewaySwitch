using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace YuanxinGateway
{
    internal sealed class RouteEntry
    {
        public uint InterfaceIndex;
        public ulong InterfaceLuid;
        public AddressFamily Family;
        public string DestinationPrefix;
        public byte PrefixLength;
        public string NextHop;
        public uint NextHopScopeId;
        public uint Metric;
        public int Protocol;
        public NativeNetwork.MIB_IPFORWARD_ROW2 RawRow;

        public string PolicyStore
        {
            get { return "ActiveStore"; }
        }
    }

    internal static class NativeNetwork
    {
        private const ushort AF_UNSPEC = 0;
        private const ushort AF_INET = 2;
        private const ushort AF_INET6 = 23;
        private const uint ERROR_NOT_FOUND = 1168;
        private const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SOCKADDR_INET
        {
            public ushort si_family;
            public ushort port;
            public uint flowinfo_or_addr4;
            public uint addr6_0;
            public uint addr6_1;
            public uint addr6_2;
            public uint addr6_3;
            public uint scope_id;

            public static SOCKADDR_INET FromIPAddress(IPAddress address, uint scopeId = 0)
            {
                if (address == null) throw new ArgumentNullException(nameof(address));
                SOCKADDR_INET inet = new SOCKADDR_INET();
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] bytes = address.GetAddressBytes();
                    inet.si_family = AF_INET;
                    inet.flowinfo_or_addr4 = BitConverter.ToUInt32(bytes, 0);
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    byte[] bytes = address.GetAddressBytes();
                    uint effectiveScope = scopeId;
                    if (effectiveScope == 0 && address.ScopeId > 0)
                    {
                        effectiveScope = (uint)address.ScopeId;
                    }
                    inet.si_family = AF_INET6;
                    inet.addr6_0 = BitConverter.ToUInt32(bytes, 0);
                    inet.addr6_1 = BitConverter.ToUInt32(bytes, 4);
                    inet.addr6_2 = BitConverter.ToUInt32(bytes, 8);
                    inet.addr6_3 = BitConverter.ToUInt32(bytes, 12);
                    inet.scope_id = effectiveScope;
                }
                else
                {
                    throw new NotSupportedException("Unsupported address family: " + address.AddressFamily);
                }
                return inet;
            }

            public IPAddress ToIPAddress()
            {
                if (si_family == AF_INET)
                {
                    return new IPAddress(BitConverter.GetBytes(flowinfo_or_addr4));
                }
                if (si_family == AF_INET6)
                {
                    byte[] addr = new byte[16];
                    Buffer.BlockCopy(BitConverter.GetBytes(addr6_0), 0, addr, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(addr6_1), 0, addr, 4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(addr6_2), 0, addr, 8, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(addr6_3), 0, addr, 12, 4);
                    return new IPAddress(addr, scope_id);
                }
                return IPAddress.None;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IP_ADDRESS_PREFIX
        {
            public SOCKADDR_INET Prefix;
            public byte PrefixLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_IPFORWARD_ROW2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public IP_ADDRESS_PREFIX DestinationPrefix;
            public SOCKADDR_INET NextHop;
            public byte SitePrefixLength;
            public uint ValidLifetime;
            public uint PreferredLifetime;
            public uint Metric;
            public int Protocol;
            public byte Loopback;
            public byte AutoconfigureAddress;
            public byte Publish;
            public byte Immortal;
            public uint Age;
            public int Origin;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIpForwardTable2(ushort Family, out IntPtr Table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr Memory);

        [DllImport("iphlpapi.dll")]
        private static extern void InitializeIpForwardEntry(ref MIB_IPFORWARD_ROW2 Row);

        [DllImport("iphlpapi.dll")]
        private static extern uint CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 Row);

        [DllImport("iphlpapi.dll")]
        private static extern uint SetIpForwardEntry2(ref MIB_IPFORWARD_ROW2 Row);

        [DllImport("iphlpapi.dll")]
        private static extern uint DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 Row);

        [DllImport("iphlpapi.dll")]
        private static extern uint ConvertInterfaceIndexToLuid(uint InterfaceIndex, out ulong InterfaceLuid);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCacheNative();

        public static void FlushDnsCache()
        {
            try
            {
                DnsFlushResolverCacheNative();
            }
            catch
            {
                // Best-effort cache flush.
            }
        }

        public static List<RouteEntry> GetDefaultRoutes(int interfaceIndex, AddressFamily? filterFamily = null)
        {
            ushort family = AF_UNSPEC;
            if (filterFamily == AddressFamily.InterNetwork) family = AF_INET;
            else if (filterFamily == AddressFamily.InterNetworkV6) family = AF_INET6;

            IntPtr tablePtr;
            uint result = GetIpForwardTable2(family, out tablePtr);
            if (result != 0)
            {
                throw new Win32Exception((int)result, "读取系统路由表失败 (GetIpForwardTable2 error " + result + ")");
            }

            List<RouteEntry> entries = new List<RouteEntry>();
            try
            {
                int count = Marshal.ReadInt32(tablePtr);
                IntPtr rowPtr = IntPtr.Add(tablePtr, 8);
                int rowSize = Marshal.SizeOf(typeof(MIB_IPFORWARD_ROW2));
                for (int i = 0; i < count; i++)
                {
                    MIB_IPFORWARD_ROW2 row = (MIB_IPFORWARD_ROW2)Marshal.PtrToStructure(rowPtr, typeof(MIB_IPFORWARD_ROW2));
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    if (interfaceIndex > 0 && row.InterfaceIndex != (uint)interfaceIndex) continue;
                    if (row.DestinationPrefix.PrefixLength != 0) continue;

                    ushort rowFamily = row.DestinationPrefix.Prefix.si_family;
                    if (rowFamily != AF_INET && rowFamily != AF_INET6) continue;

                    IPAddress destIp = row.DestinationPrefix.Prefix.ToIPAddress();
                    if (!IPAddress.Any.Equals(destIp) && !IPAddress.IPv6Any.Equals(destIp)) continue;

                    IPAddress nextHopIp = row.NextHop.ToIPAddress();
                    string prefixText = rowFamily == AF_INET ? "0.0.0.0/0" : "::/0";
                    entries.Add(new RouteEntry
                    {
                        InterfaceIndex = row.InterfaceIndex,
                        InterfaceLuid = row.InterfaceLuid,
                        Family = rowFamily == AF_INET ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6,
                        DestinationPrefix = prefixText,
                        PrefixLength = row.DestinationPrefix.PrefixLength,
                        NextHop = StripScopeId(nextHopIp),
                        NextHopScopeId = rowFamily == AF_INET6 ? row.NextHop.scope_id : 0,
                        Metric = row.Metric,
                        Protocol = row.Protocol,
                        RawRow = row
                    });
                }
            }
            finally
            {
                FreeMibTable(tablePtr);
            }
            return entries;
        }

        public static void CreateOrSetDefaultRoute(int interfaceIndex, string destinationPrefix, string nextHop, uint metric)
        {
            bool isIpv6 = destinationPrefix == "::/0";
            IPAddress hopIp = ParseAddressWithoutScope(nextHop);
            if (isIpv6 && hopIp.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("无效的 IPv6 网关地址: " + nextHop);
            }
            if (!isIpv6 && hopIp.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("无效的 IPv4 网关地址: " + nextHop);
            }

            ulong luid;
            uint luidErr = ConvertInterfaceIndexToLuid((uint)interfaceIndex, out luid);
            if (luidErr != 0)
            {
                throw new Win32Exception((int)luidErr, "无法获取网卡 LUID (index=" + interfaceIndex + ")");
            }

            // Check if exact route already exists; update its metric if needed.
            List<RouteEntry> existing = GetDefaultRoutes(interfaceIndex, hopIp.AddressFamily);
            string normalizedHop = hopIp.ToString().ToLowerInvariant();
            foreach (RouteEntry entry in existing)
            {
                if (string.Equals(entry.NextHop, normalizedHop, StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Metric != metric)
                    {
                        MIB_IPFORWARD_ROW2 updated = entry.RawRow;
                        updated.Metric = metric;
                        uint setErr = SetIpForwardEntry2(ref updated);
                        if (setErr != 0)
                        {
                            throw new Win32Exception((int)setErr, "更新默认路由 Metric 失败 (error " + setErr + ")");
                        }
                    }
                    return;
                }
            }

            MIB_IPFORWARD_ROW2 row = new MIB_IPFORWARD_ROW2();
            InitializeIpForwardEntry(ref row);
            row.InterfaceLuid = luid;
            row.InterfaceIndex = (uint)interfaceIndex;
            row.DestinationPrefix = new IP_ADDRESS_PREFIX
            {
                Prefix = SOCKADDR_INET.FromIPAddress(isIpv6 ? IPAddress.IPv6Any : IPAddress.Any),
                PrefixLength = 0
            };

            uint scopeId = (isIpv6 && hopIp.IsIPv6LinkLocal) ? (uint)interfaceIndex : 0;
            row.NextHop = SOCKADDR_INET.FromIPAddress(hopIp, scopeId);
            row.Metric = metric;

            uint createErr = CreateIpForwardEntry2(ref row);
            if (createErr != 0 && createErr != ERROR_OBJECT_ALREADY_EXISTS)
            {
                if (isIpv6 && hopIp.IsIPv6LinkLocal && scopeId != 0)
                {
                    // Fallback without explicit scope ID if driver prefers unscoped link-local.
                    row.NextHop = SOCKADDR_INET.FromIPAddress(hopIp, 0);
                    uint retryErr = CreateIpForwardEntry2(ref row);
                    if (retryErr == 0 || retryErr == ERROR_OBJECT_ALREADY_EXISTS) return;
                }
                throw new Win32Exception((int)createErr, "创建默认路由失败 (" + destinationPrefix + " -> " + nextHop + ", error " + createErr + ")");
            }
        }

        public static void DeleteRoute(RouteEntry entry)
        {
            if (entry == null) return;
            MIB_IPFORWARD_ROW2 row = entry.RawRow;
            uint err = DeleteIpForwardEntry2(ref row);
            if (err != 0 && err != ERROR_NOT_FOUND)
            {
                throw new Win32Exception((int)err, "删除路由失败 (" + entry.DestinationPrefix + " -> " + entry.NextHop + ", error " + err + ")");
            }
        }

        private static string StripScopeId(IPAddress address)
        {
            if (address == null) return string.Empty;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] bytes = address.GetAddressBytes();
                return new IPAddress(bytes).ToString().ToLowerInvariant();
            }
            return address.ToString().ToLowerInvariant();
        }

        private static IPAddress ParseAddressWithoutScope(string value)
        {
            string candidate = (value ?? string.Empty).Trim();
            int slash = candidate.IndexOf('/');
            if (slash >= 0) candidate = candidate.Substring(0, slash).Trim();
            int zone = candidate.IndexOf('%');
            if (zone >= 0) candidate = candidate.Substring(0, zone).Trim();
            return IPAddress.Parse(candidate);
        }
    }
}
