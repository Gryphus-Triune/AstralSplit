using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AstralSplit
{
    // The router has both direct and TUN sockets. Count its IPv4 TCP/UDP payload
    // events only on the local address bound by the selected-app outbound.
    internal sealed class RoutedByteMeter : IDisposable
    {
        const string Logger="AstralSplit.RoutedTransfer.v1";
        static readonly Guid LoggerId=new Guid("fa24886c-7f75-4daa-a8b4-897d87b29332");
        static readonly Guid NetworkId=new Guid("7dd42a49-5329-4832-8dfd-43d979153a88");
        const ulong InvalidHandle=ulong.MaxValue;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void EventCallback(IntPtr record);

        // The release is x64. Named offsets are the Windows SDK's x64 ABI.
        [StructLayout(LayoutKind.Explicit,Size=120)] struct TraceProperties {
            [FieldOffset(0)] public int bytes; [FieldOffset(24)] public Guid guid;
            [FieldOffset(40)] public int clock; [FieldOffset(44)] public uint flags;
            [FieldOffset(48)] public int bufferKB; [FieldOffset(52)] public int minimum;
            [FieldOffset(56)] public int maximum; [FieldOffset(64)] public uint mode;
            [FieldOffset(68)] public int flushSeconds; [FieldOffset(116)] public int nameOffset;
        }
        [StructLayout(LayoutKind.Explicit,Size=448)] struct TraceLogFile {
            [FieldOffset(8)] public IntPtr loggerName; [FieldOffset(28)] public uint mode;
            [FieldOffset(424)] public IntPtr eventRecordCallback;
        }
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern uint StartTraceW(out ulong handle,string name,IntPtr properties);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern uint ControlTraceW(ulong handle,string name,IntPtr properties,uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong trace,ref Guid provider,uint code,byte level,ulong any,ulong all,uint timeout,IntPtr parameters);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern ulong OpenTraceW(ref TraceLogFile log);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace([In] ulong[] handles,uint count,IntPtr start,IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

        readonly int pid,ip;
        readonly string home,run;
        readonly Action<Action> asUser;
        readonly EventCallback callback;
        readonly Stopwatch checkpoint=new Stopwatch();
        long bytes,saved;
        ulong session,consumer=InvalidHandle;
        IntPtr properties,name;
        Thread reader;

        RoutedByteMeter(int processId,IPAddress address,string folder,string runId,Action<Action> user,long initial)
        {
            pid=processId;ip=BitConverter.ToInt32(address.GetAddressBytes(),0);home=folder;run=runId;asUser=user;
            bytes=saved=initial;callback=OnEvent;
        }
        internal static RoutedByteMeter Start(int pid,IPAddress address,string home,string run,Action<Action> asUser)
        {
            RoutedByteMeter meter=null;
            try {
                long initial=0;asUser(()=>initial=StellarMilestone.Read(home,run));
                if(initial==long.MaxValue)return null;
                meter=new RoutedByteMeter(pid,address,home,run,asUser,initial);
                if(meter.Open()){meter.checkpoint.Start();return meter;}
            }catch(Exception ex) when(OptionalFailure(ex)){ }
            meter?.Dispose();return null;
        }
        static bool OptionalFailure(Exception ex){
            return ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                ex is InvalidOperationException || ex is ArgumentException || ex is OverflowException ||
                ex is ExternalException || ex is EntryPointNotFoundException || ex is DllNotFoundException || ex is FormatException;
        }
        void WriteProperties()
        {
            const int allocation=2048;
            Marshal.Copy(new byte[allocation],0,properties,allocation);
            int size=Marshal.SizeOf(typeof(TraceProperties));
            var p=new TraceProperties{bytes=allocation,guid=LoggerId,clock=1,flags=0x20000,
                bufferKB=64,minimum=4,maximum=32,mode=0x100,flushSeconds=1,nameOffset=size};
            Marshal.StructureToPtr(p,properties,false);
            var label=Encoding.Unicode.GetBytes(Logger+"\0");Marshal.Copy(label,0,IntPtr.Add(properties,size),label.Length);
        }
        bool Open()
        {
            properties=Marshal.AllocHGlobal(2048);name=Marshal.StringToHGlobalUni(Logger);WriteProperties();
            ulong id;uint code=StartTraceW(out id,Logger,properties);
            if(code==183){
                // The routing worker already holds its machine-wide single-session lock. Recover its own orphaned logger only.
                WriteProperties();
                if(ControlTraceW(0,Logger,properties,0)!=0 || ((TraceProperties)Marshal.PtrToStructure(properties,typeof(TraceProperties))).guid!=LoggerId)return false;
                WriteProperties();if(ControlTraceW(0,Logger,properties,1)!=0)return false;
                WriteProperties();code=StartTraceW(out id,Logger,properties);
            }
            if(code!=0)return false;
            session=id;
            var log=new TraceLogFile{loggerName=name,mode=0x10000100,eventRecordCallback=Marshal.GetFunctionPointerForDelegate(callback)};
            consumer=OpenTraceW(ref log);
            if(consumer==InvalidHandle)return false;
            var provider=NetworkId;
            if(EnableTraceEx2(session,ref provider,1,4,0x10,0,0,IntPtr.Zero)!=0)return false;
            ulong stream=consumer;
            reader=new Thread(()=>{try{ProcessTrace(new[]{stream},1,IntPtr.Zero,IntPtr.Zero);}finally{GC.KeepAlive(this);}}){IsBackground=true,Name="AstralSplit transfer counter"};
            reader.Start();return true;
        }
        internal static bool Routed(int type,int process,int source,int destination,int router,int local){
            return process==router && (type==10 || type==11 || type==42 || type==43) && (source==local || destination==local);
        }
        void OnEvent(IntPtr record)
        {
            if(Marshal.ReadInt32(record,24)!=0x7dd42a49 || Marshal.ReadByte(record,42)!=0 ||
                (ushort)Marshal.ReadInt16(record,86)<16)return;
            int type=(ushort)Marshal.ReadInt16(record,40);
            IntPtr data=Marshal.ReadIntPtr(record,96);
            if(data==IntPtr.Zero || !Routed(type,Marshal.ReadInt32(data),Marshal.ReadInt32(data,12),Marshal.ReadInt32(data,8),pid,ip))return;
            // ProcessTrace calls this on the single consumer thread. Checkpoints may read concurrently.
            long total=Interlocked.Read(ref bytes),increment=(uint)Marshal.ReadInt32(data,4);
            Interlocked.Exchange(ref bytes,total+Math.Min(increment,long.MaxValue-total));
        }
        internal void Checkpoint(bool force=false)
        {
            long count=Interlocked.Read(ref bytes);
            if(count==saved || (!force && StellarMilestone.Stage(count)==StellarMilestone.Stage(saved) && checkpoint.ElapsedMilliseconds<4000))return;
            try{
                asUser(()=>StellarMilestone.Write(home,run,count));saved=count;
            }catch(Exception ex) when(OptionalFailure(ex)){ }
            checkpoint.Restart();
        }
        internal bool Complete {get{return saved==long.MaxValue;}}
        public void Dispose()
        {
            try{
                if(session!=0){WriteProperties();ControlTraceW(session,null,properties,1);session=0;}
                if(reader!=null && reader.IsAlive)reader.Join(1200);
                if(consumer!=InvalidHandle){CloseTrace(consumer);consumer=InvalidHandle;}
                Checkpoint(true);
            }catch(Exception ex) when(OptionalFailure(ex)){ }
            finally{
                if(properties!=IntPtr.Zero){Marshal.FreeHGlobal(properties);properties=IntPtr.Zero;}
                if(name!=IntPtr.Zero){Marshal.FreeHGlobal(name);name=IntPtr.Zero;}
                GC.KeepAlive(callback);
            }
        }
    }
}
