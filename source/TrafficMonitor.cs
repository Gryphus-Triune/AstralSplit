using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AstralSplit
{
    internal sealed class TrafficSample
    {
        internal double? TunBytesPerSecond, UplinkBytesPerSecond, TunUpload, TunDownload,UplinkUpload,UplinkDownload;
        // Only TCP connections with readable, already enabled EStats across both samples.
        // A missing path (or null map) is unknown. These values do not include UDP.
        internal IReadOnlyDictionary<string,double> AppBytesPerSecond;
        internal int ComparableTcp, ObservedTcp;
        internal bool HasTcpCoverage, IncompleteTcpTable;
        // A pulse means an additional, non-loopback TCP socket was observed established.
        // Connections that open and close between samples are not observable here.
        internal IReadOnlyCollection<string> NewTcpConnectionPaths;
    }

    internal sealed class TrafficMonitor
    {
        // Windows SDK x64 MIB_IF_ROW2, keyed by interface LUID; octets are unsigned 64-bit counters.
        [StructLayout(LayoutKind.Explicit, Size=1352)]
        struct IfRow
        {
            [FieldOffset(0)] internal ulong Luid;
            [FieldOffset(12)] internal Guid Id;
            [FieldOffset(1156)] internal uint OperStatus;
            [FieldOffset(1208)] internal ulong InOctets;
            [FieldOffset(1280)] internal ulong OutOctets;
        }
        [DllImport("iphlpapi.dll")] static extern uint GetIfEntry2(ref IfRow row);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable([Out] byte[] buffer,ref int size,int sorted,int family,int tableClass,int reserved);
        [StructLayout(LayoutKind.Explicit, Size=96)]
        struct TcpData
        {
            [FieldOffset(72)] internal ulong ThruBytesAcked;
            [FieldOffset(88)] internal ulong ThruBytesReceived;
        }
        [DllImport("iphlpapi.dll")] static extern uint GetPerTcpConnectionEStats(byte[] row,int type,out byte enabled,uint rwVersion,uint rwSize,
            IntPtr ros,uint rosVersion,uint rosSize,out TcpData data,uint rodVersion,uint rodSize);
        [DllImport("iphlpapi.dll")] static extern uint GetPerTcp6ConnectionEStats(byte[] row,int type,out byte enabled,uint rwVersion,uint rwSize,
            IntPtr ros,uint rosVersion,uint rosSize,out TcpData data,uint rodVersion,uint rodSize);

        struct Rates
        {
            internal double Upload, Download;
            internal double Total { get { return Upload+Download; } }
        }
        struct Counter
        {
            internal ulong Received,Sent;
            internal Counter(ulong received,ulong sent){Received=received;Sent=sent;}
            internal double? Rate(Counter old,double elapsed)
            {
                return Directional(old,elapsed)?.Total;
            }
            internal Rates? Directional(Counter old,double elapsed)
            {
                if(!(elapsed>0 && elapsed<=8) || Received<old.Received || Sent<old.Sent)return null;
                return new Rates{Upload=(Sent-old.Sent)/elapsed,Download=(Received-old.Received)/elapsed};
            }
        }
        sealed class InterfaceChannel
        {
            Dictionary<Guid,Counter> previous;
            static bool Read(Adapter adapter,Dictionary<Guid,Counter> current)
            {
                if(adapter==null)return true;
                Guid id;
                if(adapter.Luid==0 || !Guid.TryParse(adapter.Id,out id))return false;
                if(current.ContainsKey(id))return true;
                var row=new IfRow{Luid=adapter.Luid};
                if(GetIfEntry2(ref row)!=0 || row.Id!=id || row.OperStatus!=1)return false;
                current.Add(id,new Counter(row.InOctets,row.OutOctets));return true;
            }
            internal Rates? Update(Adapter first,Adapter second,double elapsed)
            {
                var current=new Dictionary<Guid,Counter>();
                var old=previous; previous=null;
                if(!Read(first,current) || !Read(second,current) || current.Count==0)return null;
                previous=current;
                if(old==null || current.Count!=old.Count)return null;
                var rates=new Rates();
                foreach(var pair in current){
                    Counter before;if(!old.TryGetValue(pair.Key,out before))return null;
                    var delta=pair.Value.Directional(before,elapsed);if(!delta.HasValue)return null;
                    rates.Upload+=delta.Value.Upload;rates.Download+=delta.Value.Download;
                }
                return rates;
            }
        }

        sealed class AppFlows
        {
            internal readonly HashSet<string> Sockets=new HashSet<string>();
            internal readonly Dictionary<string,Counter> Connections=new Dictionary<string,Counter>();
            internal int Unidentified;
            internal bool NewSocketSince(AppFlows previous){return !Sockets.IsSubsetOf(previous.Sockets);}
            internal double? Rate(AppFlows before,double seconds,out int comparable,out int observed)
            {
                comparable=0;
                observed=Sockets.Count + Math.Max(Unidentified,before.Unidentified);
                foreach(string socket in before.Sockets)if(!Sockets.Contains(socket))observed++;
                double total=0;
                foreach(var pair in Connections){
                    Counter old;if(!before.Connections.TryGetValue(pair.Key,out old))continue;
                    var rate=pair.Value.Rate(old,seconds);if(!rate.HasValue)continue;
                    total+=rate.Value;
                    comparable++;
                }
                return comparable==0?(double?)null:total;
            }
        }
        sealed class State
        {
            internal readonly InterfaceChannel Tun=new InterfaceChannel(), Uplink=new InterfaceChannel();
            internal long LastTime,LastTcpTime,NextStatsCheck;
            internal Dictionary<string,AppFlows> PreviousApps;
            internal byte[] Tcp4,Tcp6;
            internal readonly StringBuilder PathBuffer=new StringBuilder(32768);
            internal readonly HashSet<int> VerifiedOwners=new HashSet<int>();
            internal Dictionary<int,Apps.SocketOwner> Owners=new Dictionary<int,Apps.SocketOwner>(),
                PreviousOwners=new Dictionary<int,Apps.SocketOwner>();
            internal bool IncompleteTable;
        }
        State state=new State();
        internal void Reset(){Interlocked.Exchange(ref state,new State());}

        internal TrafficSample Sample(NetworkState network,IEnumerable<string> appPaths=null)
        {
            var working=Volatile.Read(ref state);
            lock(working){
                long now=Stopwatch.GetTimestamp();
                double elapsed=(now-working.LastTime)/(double)Stopwatch.Frequency;
                working.LastTime=now;
                var tun=working.Tun.Update(network?.Astral,null,elapsed);
                var uplink=working.Uplink.Update(network?.Physical4,network?.Physical6,elapsed);
                var result=new TrafficSample{
                    TunBytesPerSecond=tun?.Total,UplinkBytesPerSecond=uplink?.Total,
                    TunUpload=tun?.Upload,TunDownload=tun?.Download,UplinkUpload=uplink?.Upload,UplinkDownload=uplink?.Download
                };
                ReadApps(working,appPaths,now,result);
                return ReferenceEquals(working,Volatile.Read(ref state))?result:new TrafficSample();
            }
        }

        static void ReadApps(State state,IEnumerable<string> paths,long now,TrafficSample sample)
        {
            var current=new Dictionary<string,AppFlows>(StringComparer.OrdinalIgnoreCase);
            if(paths!=null)foreach(string path in paths)if(!String.IsNullOrEmpty(path))current[path]=new AppFlows();
            if(current.Count==0){
                state.PreviousApps=null;state.NextStatsCheck=0;
                state.Owners.Clear();state.PreviousOwners.Clear();
                state.Tcp4=null;state.Tcp6=null;
                return;
            }
            var oldOwners=state.PreviousOwners;
            state.PreviousOwners=state.Owners;
            state.Owners=oldOwners;
            state.Owners.Clear();
            bool readStats=now>=state.NextStatsCheck;
            bool four=CollectTcp(false,state,ref state.Tcp4,current,readStats);
            bool six=CollectTcp(true,state,ref state.Tcp6,current,readStats);
            bool priorIncomplete=state.IncompleteTable;
            state.IncompleteTable=!four || !six;
            if(!four && !six){
                state.PreviousApps=null;return;
            }
            if(readStats){
                bool available=false;
                foreach(var item in current.Values)if(item.Connections.Count!=0){available=true;break;}
                state.NextStatsCheck=available?0:now+10*Stopwatch.Frequency;
            }
            var previous=state.PreviousApps;
            state.PreviousApps=current;
            double elapsed=(now-state.LastTcpTime)/(double)Stopwatch.Frequency;
            state.LastTcpTime=now;
            if(previous==null || elapsed<=0 || elapsed>8)return;
            sample.HasTcpCoverage=true;
            sample.IncompleteTcpTable=priorIncomplete || state.IncompleteTable;
            var opened=new List<string>();
            var rates=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            foreach(var pair in current){
                AppFlows old;if(!previous.TryGetValue(pair.Key,out old))continue;
                if(!sample.IncompleteTcpTable && pair.Value.NewSocketSince(old))opened.Add(pair.Key);
                double? rate=pair.Value.Rate(old,elapsed,out int comparable,out int observed);
                if(rate.HasValue)rates[pair.Key]=rate.Value;
                sample.ComparableTcp+=comparable;
                sample.ObservedTcp+=observed;
            }
            sample.NewTcpConnectionPaths=opened;
            sample.AppBytesPerSecond=rates.Count==0?null:rates;
        }

        static bool IsLoopback(byte[] table,int remote,bool ipv6)
        {
            if(!ipv6)return table[remote]==127;
            for(int i=0;i<10;i++)if(table[remote+i]!=0)return false;
            if(table[remote+10]==255 && table[remote+11]==255)return table[remote+12]==127;
            for(int i=10;i<15;i++)if(table[remote+i]!=0)return false;
            return table[remote+15]==1;
        }
        static bool CollectTcp(bool ipv6,State state,ref byte[] buffer,Dictionary<string,AppFlows> current,bool readStats)
        {
            const int ownerModuleConnections=7, insufficientBuffer=122;
            int family=ipv6?23:2,size=buffer?.Length??0;
            byte[] table=buffer;
            uint error=GetExtendedTcpTable(table,ref size,0,family,ownerModuleConnections,0);
            // The x64 SDK OWNER_MODULE rows start at an 8-byte boundary; their timestamp
            // disambiguates a socket reopened on the same four-tuple by the same process.
            int stride=ipv6?192:160, stateOffset=ipv6?48:0,pidOffset=ipv6?52:20,identityBytes=ipv6?64:32;
            for(int attempt=0;error==insufficientBuffer && attempt<3;attempt++){
                if(size<4 || size>32*1024*1024)return false;
                size=Math.Min(32*1024*1024,size+Math.Max(256,size/4));
                table=new byte[size];
                if(size<=4*1024*1024)buffer=table;
                error=GetExtendedTcpTable(table,ref size,0,family,ownerModuleConnections,0);
            }
            if(error==50 && ipv6)return true; // IPv6 is not present on this host.
            if(error!=0 || table==null || size<4 || size>table.Length)return false;
            int count=BitConverter.ToInt32(table,0);
            if(count<0 || (count>0 && 8L+(long)count*stride>size))return false;
            var row=new byte[ipv6?52:20];
            state.VerifiedOwners.Clear();
            for(int i=0;i<count;i++){
                int entry=8+i*stride;
                if(BitConverter.ToInt32(table,entry+stateOffset)!=5 || IsLoopback(table,entry+(ipv6?24:12),ipv6))continue;
                int pid=BitConverter.ToInt32(table,entry+pidOffset);
                if(state.VerifiedOwners.Add(pid)){
                    if(!state.Owners.TryGetValue(pid,out var prior))state.PreviousOwners.TryGetValue(pid,out prior);
                    var owner=Apps.SocketOwnerAt(pid,prior,state.PathBuffer);
                    state.Owners[pid]=owner;
                }
                var socketOwner=state.Owners[pid];
                if(socketOwner.Path==null || !current.TryGetValue(socketOwner.Path,out var app))continue;
                long socketCreated=BitConverter.ToInt64(table,entry+identityBytes-8);
                if(socketCreated<=0){app.Unidentified++;continue;}
                if(socketCreated<socketOwner.Created)continue; // PID reused after the table snapshot.
                string key=Convert.ToBase64String(table,entry,identityBytes);
                app.Sockets.Add(key);
                if(!readStats)continue;
                if(ipv6){
                    Buffer.BlockCopy(table,entry+48,row,0,4);
                    Buffer.BlockCopy(table,entry,row,4,48);
                }else Buffer.BlockCopy(table,entry,row,0,20);
                TcpData data;byte enabled;
                uint status=ipv6?GetPerTcp6ConnectionEStats(row,1,out enabled,0,1,IntPtr.Zero,0,0,out data,0,96):
                    GetPerTcpConnectionEStats(row,1,out enabled,0,1,IntPtr.Zero,0,0,out data,0,96);
                if(status!=0 || enabled!=1)continue;
                app.Connections[key]=new Counter(data.ThruBytesReceived,data.ThruBytesAcked);
            }
            return true;
        }
    }
}
