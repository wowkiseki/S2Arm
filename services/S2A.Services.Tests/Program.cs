using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Memory;
using Path = System.IO.Path;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace S2A.Services.Tests
{
    // APFS 克隆号（getattrlist ATTR_CMNEXT_CLONEID）：clonefile 出来的两份相同，逐字节复制的不同；取不到回 0
    static class S2ACloneId
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct AttrList { public ushort BitmapCount, Reserved; public uint Common, Vol, Dir, File, Fork; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        private struct Buf { public uint Length; public ulong CloneId; }
        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern int getattrlist(string path, ref AttrList list, out Buf buf, nuint size, uint options);
        public static ulong Of(string path)
        {
            AttrList l = new() { BitmapCount = 5, Fork = 0x100 };   // ATTR_BIT_MAP_COUNT / ATTR_CMNEXT_CLONEID
            return getattrlist(path, ref l, out Buf b, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Buf>(), 0x20) == 0 ? b.CloneId : 0;   // FSOPT_ATTR_CMN_EXTENDED
        }
    }

    // 服务层单测：假原生层 + 桩渲染器，经与生产相同的 C# 单一入口（CsApi.Svc）发手工拼的 IPC 消息。
    // 期望值全部来自 Horizon 协议 / 服务语义，不来自被测实现。输出「期望 → 真结果」对位表，任一不符退出码 1。
    public static unsafe partial class Program
    {
        private static readonly List<(string Name, string Expect, string Actual, bool Ok)> _rows = new();
        private static byte* _tlr;
        private static byte* _tlr2;   // 第二条线程的 TLS（并发请求用例）
        private static readonly List<(string Cmd, double Bytes)> _alloc = new();
        // 普查项（H05 #150）：只记录、不进零分配硬判据——取句柄类命令修完还剩子会话注册的 256–673 B，归零要改子会话生命周期，另开任务
        private static readonly List<(string Cmd, double Bytes)> _census = new();

        // 逐命令稳态分配：预热 100 次后量 1000 次，每请求字节（方案 §3-4 判据：高频命令 0 字节）
        private static void MeasureAlloc(string name, uint session, Func<Msg> make)
        {
            for (int i = 0; i < 100; i++)
            {
                Call(session, make());
            }

            Msg m = make();
            long total = 0;

            // 计量路径不经 Svc() 帮手（它为返回寄存器建数组，会算进夹具自己的分配）
            ulong cookie = FakeNative.CookieOf(session);
            ulong* regs = stackalloc ulong[31];
            SvcCtx ctx = new() { Num = 0x21, Thread = 0x1234, Regs = regs, Tlr = _tlr, Obj = cookie, ObjKind = (uint)ObjKind.Session };

            for (int i = 0; i < 1000; i++)
            {
                m.Write(_tlr);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                FakeNative.Api->Svc(&ctx);
                total += GC.GetAllocatedBytesForCurrentThread() - a0;
            }

            _alloc.Add((name, total / 1000.0));

            if (Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1")
            {
                using AllocProfiler prof = new();

                for (int i = 0; i < 200_000; i++)
                {
                    m.Write(_tlr);
                    FakeNative.Api->Svc(&ctx);
                }

                Console.Error.WriteLine($"分配来源 {name}：{prof.Top(8)}");
            }
        }
        // 成对命令的稳态分配（如显存 MapBufferEx 后 UnmapBuffer：游戏每帧成对调）：first 之后用 between 改第二条的参数，每对计一次
        private static void MeasureAllocPair(string name, uint session, Msg first, Msg second, Action between)
        {
            ulong cookie = FakeNative.CookieOf(session);
            ulong* regs = stackalloc ulong[31];
            SvcCtx ctx = new() { Num = 0x21, Thread = 0x1234, Regs = regs, Tlr = _tlr, Obj = cookie, ObjKind = (uint)ObjKind.Session };

            for (int i = 0; i < 100; i++)
            {
                Pair(&ctx, first, second, between);
            }

            long total = 0;

            for (int i = 0; i < 1000; i++)
            {
                total += Pair(&ctx, first, second, between);
            }

            _alloc.Add((name, total / 1000.0));

            if (Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1")
            {
                using AllocProfiler prof = new();

                for (int i = 0; i < 200_000; i++)
                {
                    Pair(&ctx, first, second, between);
                }

                Console.Error.WriteLine($"分配来源 {name}：{prof.Top(12)}");
            }
        }

        // 应答带复制句柄的命令（如取事件句柄，游戏每次取完即关）：每次请求后在计量区间外关掉应答里的句柄（同游戏 CloseHandle，不计夹具的句柄表）
        private static void MeasureAllocCloseCopy(string name, uint session, Msg m)
        {
            ulong cookie = FakeNative.CookieOf(session);
            ulong* regs = stackalloc ulong[31];
            SvcCtx ctx = new() { Num = 0x21, Thread = 0x1234, Regs = regs, Tlr = _tlr, Obj = cookie, ObjKind = (uint)ObjKind.Session };
            long total = 0;

            for (int i = 0; i < 1100; i++)
            {
                m.Write(_tlr);
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                FakeNative.Api->Svc(&ctx);
                long a = GC.GetAllocatedBytesForCurrentThread() - a0;
                total += i >= 100 ? a : 0;

                foreach (uint h in Reply.Parse(_tlr).Copy)
                {
                    FakeNative.HandleClose(h);
                }
            }

            _alloc.Add((name, total / 1000.0));

            if (Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1")
            {
                using AllocProfiler prof = new();

                for (int i = 0; i < 100_000; i++)
                {
                    m.Write(_tlr);
                    FakeNative.Api->Svc(&ctx);
                    foreach (uint h in Reply.Parse(_tlr).Copy) FakeNative.HandleClose(h);
                }

                Console.Error.WriteLine($"分配来源 {name}：{prof.Top(12)}");
            }
        }

        // 只计两条服务请求本身的分配（between 是夹具自己的活：改参数、模拟 GPU 线程清延迟队列，不计）
        private static long Pair(SvcCtx* ctx, Msg first, Msg second, Action between)
        {
            first.Write(_tlr);
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            FakeNative.Api->Svc(ctx);
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            between();
            second.Write(_tlr);
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            FakeNative.Api->Svc(ctx);
            return a1 - a0 + GC.GetAllocatedBytesForCurrentThread() - a2;
        }

        private static byte* _game;

        public static int Main(string[] args)
        {
            if (args is ["--offline-socket-shutdown"]) return OfflineSocketShutdown();
            // 自动发现（TestDiscovery.cs）：test.sh 按 --list-modes 逐个起独立进程；标了 [SoloTest] 的启动前模式在这里直接跑
            if (args is ["--list-modes"]) { foreach (string m in TestDiscovery.Modes()) Console.WriteLine(m); return 0; }
            if (args.Length == 1 && TestDiscovery.TryRunPreBoot(args[0], out int soloRc)) return soloRc;
            if (args is ["--ifile-write-length"]) return IFileWriteLengthTest();
            if (args.Length == 1 && args[0] == "--exports-zone-cleanup-fail")
            {
                int result = ExportsStartupRetryTest(true, true);
                Environment.Exit(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--exports-zone-race")
            {
                int result = ExportsStartupRetryTest(true);
                Environment.Exit(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--exports-startup-retry")
            {
                int result = ExportsStartupRetryTest();
                Environment.Exit(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--host-startup-retry")
            {
                int result = HostStartupRetryTest();
                Environment.Exit(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--host-configuration")
            {
                int result = HostConfigurationBootTest();
                Environment.Exit(result);
                return result;
            }
            if (args.Length == 1 && args[0] == "--configuration") return ConfigurationTest();
            if (args.Length == 1 && args[0] == "--legacy-environment") return LegacyEnvironmentTest();
            if (args.Length == 1 && args[0] == "--romfs-symlink") return RomFsSymlinkTest();
            if (args.Length == 1 && args[0] == "--screen-aspect") return ScreenAspectTest();
            if (args.Length == 1 && args[0] == "--native-provider-legacy") return NativeLegacyProviderTest();
            if (args.Length == 5 && args[0] == "--native-provider")
                return NativeProviderTest(args[1], args[2], args[3], args[4] == "empty");
            // #1422：同句柄「先兜底、再显式」的升级路径必须在独立进程里跑（绑定是进程级 latch）。
            if (args.Length == 2 && args[0] == "--native-provider-upgrade")
                return NativeProviderUpgradeTest(args[1]);
            // #1422：平台装配登记兜底 owner 的路径（同样独立进程）。
            if (args.Length == 2 && args[0] == "--native-provider-registered")
                return NativeProviderRegisteredFallbackTest(args[1]);
            if (args.Length == 1 && (args[0] == "--audio-lifetime" || args[0] == "--ldn-room-count" || args[0] == "--ldn-ip-wrap" || args[0] == "--nvas-initex" || args[0] == "--ldn-proxy-wake"))
            {
                if (args[0] == "--ldn-ip-wrap") LdnIpWrap();
                else if (args[0] == "--nvas-initex") NvAsInitializeExTest();
                else if (args[0] == "--ldn-proxy-wake") LdnProxyWakeTest();
                else if (args[0] == "--ldn-room-count") LdnRoomCountOrder();
                else AudioLifetimeTest();
                int failures = 0;
                foreach ((string n, string e, string a, bool ok) in _rows)
                {
                    Console.WriteLine($"{n}: {e} → {a} {(ok ? "✓" : "✗")}");
                    if (!ok) failures++;
                }
                return failures == 0 ? 0 : 1;
            }
            if (args is ["--horizon-merge-lifecycle"]) return HorizonMergeLifecycleTest();
            if (args is ["--filesystem-restart"]) return FileSystemRestart();
            if (args is ["--apple-audio-dispose"]) return AppleAudioDispose();
            if (IFileWriteLengthTest() != 0) return 1;
            FakeNative.Install();
            // 平台装配（同 services/aot/Compose.cs）：存档原子提交的宿主目录原语（#1072），单测宿主是 Mac
            Ryujinx.HLE.FileSystem.S2AHostFileSystem.Transaction = new S2A.Apple.SaveCommit.AppleHostDirectoryTransaction();
            // 相册截图 JPEG 编码器（ImageIO）：截图服务用例走生产同一个编码器
            S2A.Apple.Capture.AppleScreenshotEncoder.Register();
            // VP8 解码（libvpx + 垫片，宿主里静态链）：单测 JIT 跑，经 test.sh 给出的动态库调同一套代码
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Ryujinx.Graphics.Nvdec.Vpx.VpxVp8Decoder).Assembly, (name, _, _) =>
                name == "s2a_vpx" && Environment.GetEnvironmentVariable("S2A_VPX_DYLIB") is string vpx ? System.Runtime.InteropServices.NativeLibrary.Load(vpx) : 0);
            // Opus 解码（libopus + 垫片）：服务（Ryujinx.Horizon）与单测出测试包用的编码器（本程序集）经同一个动态库
            System.Runtime.InteropServices.DllImportResolver opusLib = (name, _, _) =>
                name == "s2a_opus" && Environment.GetEnvironmentVariable("S2A_OPUS_DYLIB") is string opus ? System.Runtime.InteropServices.NativeLibrary.Load(opus) : 0;
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Ryujinx.Horizon.HorizonStatic).Assembly, opusLib);
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, opusLib);
            Red.Strict = false;
            List<string> redLines = new();
            Red.Log = line => { redLines.Add(line); Console.Error.WriteLine(line); };

            // 联机中继 / RyuLdn 客户端改用临时端口（10-28 单测在 30456 托管中继，接走了同时段 Mac 双进程局的建房请求）
            _ldnPortDefault = Ryujinx.HLE.S2AHooks.LdnPort;
            Ryujinx.HLE.S2AHooks.LdnPort = FreeTcpPort(System.Net.IPAddress.Any);

            string data = Path.Combine(Path.GetTempPath(), $"s2a_svc_test_{Environment.ProcessId}");
            Host.Boot(new StubRenderer(), new DummyHardwareDeviceDriver(), data);
            Exports.Register();
            if (args is ["--apple-shutdown-thread"])
            {
                try { return AppleShutdownThreadTest(); }
                finally { try { Directory.Delete(data, true); } catch { } }
            }

            _tlr = FakeNative.Alloc(0x4000);
            _tlr2 = FakeNative.Alloc(0x4000);
            _game = FakeNative.Alloc(0x10000);

            try
            {
                bool accountOnly = args.Length == 1 && args[0] == "--account-lifetime";
                bool arpCapacity = args.Length == 1 && args[0] == "--arp-capacity";
                bool arpOnly = args.Length == 1 && (args[0] == "--arp-application" || arpCapacity);
                bool ldnFixture = args.Length == 1 && args[0] == "--ldn-synthetic-application";
                bool inflightOnly = args.Length == 1 && args[0] == "--inflight-close";
                bool ipcAlloc = args.Length == 1 && args[0] == "--ipc-allocation";
                bool domainErrors = args.Length == 1 && args[0] == "--domain-errors";
                bool bsdOnly = args.Length == 1 && args[0] == "--bsd-poll";
                bool inputSelf = args.Length == 1 && args[0] == "--input-selfstop";
                bool inputOnly = args.Length == 1 && (args[0] == "--input-lifetime" || inputSelf);
                bool discovered = args.Length == 1 && TestDiscovery.IsBootedMode(args[0]);   // [SoloTest(Booted = true)] 的独立模式
                if (discovered) TestDiscovery.TryRunBooted(args[0]);
                else if (ipcAlloc) IpcAllocationTest();
                else if (inflightOnly) InflightCloseTest();
                else if (bsdOnly) BsdPollRegression();
                else if (domainErrors) DomainRegressionTest();
                else if (inputOnly) InputLifetimeTest(inputSelf);
                else if (accountOnly) AccountLifetime();
                else if (arpOnly || ldnFixture) ArpApplicationTest(ldnFixture, arpCapacity);
                else
                {
                    Run();
                    LdnRoomCountOrder();
                    LdnIpWrap();
                    LdnRelayRoundTrip();   // 客户端会登记全进程 LDN 代理（用例结束注销）
                    TestDiscovery.RunCases();   // 标了 [SvcCase] 的用例（各自文件里，按方法名排序）
                }
                // NativeAOT 发布版跳过这条用例：它用 DispatchProxy.Create 造渲染器代理，要在运行时生成 IL，
                //   AOT 平台不支持（抛 PlatformNotSupportedException）。丢的只是「渲染线程与游戏线程互等」这一个死锁场景；
                //   H05 要的高频命令分配字节全在 Run() 里量。开关是编译期常量（AOT 下没有运行时特性可查），
                //   由 S2A.Services.Tests.csproj 的 S2AAotTests 定义。
#if !S2A_AOT_TESTS
                if (!accountOnly && !arpOnly && !ldnFixture && !inputOnly && !domainErrors && !bsdOnly && !inflightOnly && !ipcAlloc && !discovered) ThreadedBackendLock();
#endif
                // 收局：HorizonHost.Stop 在收局线程上调（不是 Horizon 服务线程，没登记 HorizonStatic 的线程局部 Syscall）。
                //   期望按「收局不崩、服务线程限时全退」定：不抛异常（10-29 IML 档八方2 Mac 局收局崩在 RequestStopProcessing → SignalEvent →
                //   NotifyAndWakeUpThread 取 HorizonStatic.Syscall 空引用）。放在所有用例之后：停了服务就不能再发请求
                {
                    string stopErr = "无";
                    // 新线程上调（同真机：收局在自动玩家线程 / dispatch 队列 / iOS 终止回调上）：测试主线程前面的用例登记过 HorizonStatic，复现不了
                    System.Threading.Thread stopper = new(() =>
                    {
                        try { HorizonHost.Stop(); }
                        catch (Exception e) { stopErr = $"{e.GetType().Name}：{e.Message}"; }
                    });
                    stopper.Start();
                    if (!stopper.Join(20_000)) stopErr = "20 秒没返回";
                    Check("收局：新线程上 HorizonHost.Stop（停端口、限时等服务线程退出）抛的异常", "无", stopErr);
                }
            }
            catch (Exception e)
            {
                // 用例抛出没接住的异常（11-05：opus 动态库被并行重编删掉，DllNotFoundException 一路冒到 Main，dotnet SIGABRT、rc=134、没有汇总行）：
                //   记一行不符、后面的用例不再跑，照常打对位表与汇总行、退出码 1；完整栈进标准错误（test.sh 红了打末 20 行）
                Console.Error.WriteLine($"服务层单测主流程未处理异常：{e}");
                Check("主流程：用例抛出未处理异常（之后的用例没跑）", "无", $"{e.GetType().Name}：{e.Message.ReplaceLineEndings(" ")}");   // 对位表一行一项：载库失败的消息自带换行
            }
            finally
            {
                try { Directory.Delete(data, true); } catch { }
            }

            // 零分配是硬判据（方案 §3-4）：高频命令每请求、声音线程每轮、空闲底噪，均须 0 B
            foreach ((string c, double b) in _alloc)
            {
                Check($"零分配：{c}", "0 B", $"{b:F0} B");
            }

            foreach ((string c, double b) in _census)
            {
                Console.WriteLine($"普查（只记录不判）：{c} {b:F0} B");
            }

            int bad = 0;
            Console.WriteLine("| 用例 | 期望 | 真结果 | |");
            Console.WriteLine("|---|---|---|---|");

            foreach ((string n, string e, string a, bool ok) in _rows)
            {
                Console.WriteLine($"| {n} | {e} | {a} | {(ok ? "✓" : "✗")} |");
                bad += ok ? 0 : 1;
            }

            Console.WriteLine($"共 {_rows.Count} 项，不符 {bad} 项；报红 {Red.Total} 次（预期内的报红用例计入）");
            // Ryujinx 的服务线程是前台线程，Main 返回不会退进程
            Environment.Exit(bad == 0 ? 0 : 1);
            return 0;
        }

        // Ryujinx DRAM（补丁 dram-service-pool-by-need）：只保留内核区 + 服务池（按需）+ nv 池，期望值按 Horizon 常量与 ServerBase 协议手算
        private static void DramLayout()
        {
            Ryujinx.HLE.HOS.Kernel.KernelContext k = Dispatcher.Instance.Kernel;
            Ryujinx.HLE.HOS.Kernel.Memory.KMemoryRegionManager service = k.MemoryManager.MemoryRegions[(int)Ryujinx.HLE.HOS.Kernel.Memory.MemoryRegion.Service];
            Ryujinx.HLE.HOS.Kernel.Memory.KMemoryRegionManager nv = k.MemoryManager.MemoryRegions[(int)Ryujinx.HLE.HOS.Kernel.Memory.MemoryRegion.NvServices];
            // nv 池 = Horizon 非安全系统池下限：vi 0x2238 + nvservices 0x710 + 其他 0x80 页（4 KB）；服务池止于 DRAM 起点 0x80000000 + 40 MB
            const ulong nvPool = (0x2238 + 0x710 + 0x80) * 0x1000UL;
            const ulong serviceEnd = 0x80000000UL + 40 * 0x100000UL;
            Check("DRAM 块大小（内核区 + 服务池 + nv 池）", 40 * 0x100000UL + nvPool, k.Memory.Size);
            Check("nv 池起点 = 服务池止点（16 KB 对齐）", $"0x{serviceEnd:X}、对齐", $"0x{nv.Address:X}、{(nv.Address % 0x4000 == 0 ? "对齐" : "不对齐")}");
            Check("nv 池止点 = DRAM 块尾", 0x80000000UL + k.Memory.Size, nv.EndAddr);
            Check("服务池止点", serviceEnd, service.EndAddr);
            // 用量：旧框架 10 个服务进程（Sm Bsd Fs Hid Nv Time Vi:u Vi:m Vi:s Ldn）× (ServerLoop 堆 0x200000 + 代码 1 页 + 主线程栈 0x1000)
            ulong used = service.Size - service.GetFreePages() * 0x1000;
            Check("服务池用量（10 个服务进程 × (2 MB 堆 + 2 页)）", 10 * (0x200000UL + 2 * 0x1000), used);
            // 耗尽：要比剩余多 1 页 → 失败且报红（不静默）
            long red0 = Red.Total;
            Ryujinx.Horizon.Common.Result r = service.AllocatePages(out _, service.GetFreePages() + 1);
            Check("服务池耗尽", "失败、报红 +1", $"{(r == Ryujinx.Horizon.Common.Result.Success ? "成功" : "失败")}、报红 +{Red.Total - red0}");
        }

        internal static void Check(string name, object expect, object actual)
        {
            string e = Fmt(expect), a = Fmt(actual);
            _rows.Add((name, e, a, e == a));
        }

        // 不随仓库分发的玩家数据（游戏 romfs、系统数据）：缺了打「未覆盖」一行、不计不符；判据不符仍走 Check
        internal static void CheckUncovered(string name, string why) => _rows.Add((name, "未覆盖", why, true));

        private static string Fmt(object o) => o switch
        {
            uint u => $"0x{u:X}",
            ulong u => $"0x{u:X}",
            null => "null",
            _ => o.ToString(),
        };

        // 一次系统调用：与原生层门帧同形（x0..x30），走 CsApi.Svc
        private static (int Rc, ulong[] Regs) Svc(uint num, ulong obj, params ulong[] x)
        {
            ulong* regs = stackalloc ulong[31];

            for (int i = 0; i < x.Length; i++)
            {
                regs[i] = x[i];
            }

            SvcCtx ctx = new() { Num = num, Thread = 0x1234, Regs = regs, Tlr = _tlr, Obj = obj, ObjKind = obj != 0 ? (uint)ObjKind.Session : 0 };
            int rc = FakeNative.Api->Svc(&ctx);
            ulong[] outRegs = new ulong[8];

            for (int i = 0; i < 8; i++)
            {
                outRegs[i] = regs[i];
            }

            return (rc, outRegs);
        }

        // 对一个会话句柄发请求（原生层会把句柄解成 cookie 放进 ctx.Obj）
        // 接口 v2.4 之前的 s2a_handle_close 约定（0 = 成功、-1 = 无此句柄），自检反向用例用
        private static int TestScreenWidth() => 1920;
        private static int TestScreenHeight() => 1440;
        private static int TestScreenMissing() => 0;

        private static int CloseOldConvention(uint h) => FakeNative.HandleClose(h) == 1 ? 0 : -1;

        // 经 LibHac 挂动森设备存档读一个文件；不存在回 null
        private static byte[] ReadDeviceSave(string path, LibHac.Fs.UserId? account = null)
        {
            LibHac.HorizonClient hc = Host.Device.System.LibHacHorizonManager.RyujinxClient;
            (account is LibHac.Fs.UserId u
                ? LibHac.Fs.Shim.SaveData.MountSaveData(hc.Fs, "s2achk"u8, new LibHac.Ncm.ApplicationId(Application.ProgramId), u)
                : hc.Fs.MountDeviceSaveData("s2achk"u8, new LibHac.ApplicationId(Application.ProgramId))).ThrowIfFailure();

            try
            {
                byte[] p = Encoding.UTF8.GetBytes("s2achk:" + path + "\0");

                if (hc.Fs.OpenFile(out LibHac.Fs.FileHandle h, p, LibHac.Fs.OpenMode.Read).IsFailure())
                {
                    return null;
                }

                hc.Fs.GetFileSize(out long size, h).ThrowIfFailure();
                byte[] buf = new byte[size];
                hc.Fs.ReadFile(h, 0, buf).ThrowIfFailure();
                hc.Fs.CloseFile(h);
                return buf;
            }
            finally
            {
                hc.Fs.Unmount("s2achk"u8);
            }
        }

        // 用指定的 TLS 发请求（别的线程并发发请求时用，不碰共用的 _tlr）
        private static Reply CallTlr(byte* tlr, uint session, Msg m)
        {
            m.Write(tlr);
            ulong* regs = stackalloc ulong[31];
            SvcCtx ctx = new() { Num = 0x21, Thread = 0x1235, Regs = regs, Tlr = tlr, Obj = FakeNative.CookieOf(session), ObjKind = (uint)ObjKind.Session };
            int rc = FakeNative.Api->Svc(&ctx);

            if (rc != SvcResult.Handled || regs[0] != 0)
            {
                throw new InvalidOperationException($"SendSyncRequest 失败 rc={rc} x0=0x{regs[0]:X}");
            }

            return Reply.Parse(tlr);
        }

        // 像游戏那样读时间共享内存算当前 POSIX 秒：稳态时钟（InternalOffset + 游戏自己的计数器）+ 本地系统时钟偏移（双缓冲按序号取）
        private static long GameNowFromTimeShm(byte* tm)
        {
            uint sIdx = *(uint*)tm;
            ulong internalOffset = *(ulong*)(tm + 8 + (sIdx & 1) * 24);
            uint lIdx = *(uint*)(tm + 0x38);
            long localOffset = *(long*)(tm + 0x38 + 8 + (lIdx & 1) * 32);
            ulong gameTick = FakeNative.GuestTickValue();
            long steadySec = (long)((internalOffset + (ulong)((Int128)gameTick * 1_000_000_000 / 19_200_000)) / 1_000_000_000);
            return localOffset + steadySec;
        }

        private static Reply Call(uint session, Msg m)
        {
            m.Write(_tlr);
            (int rc, ulong[] r) = Svc(0x21, FakeNative.CookieOf(session));

            if (rc != SvcResult.Handled || r[0] != 0)
            {
                throw new InvalidOperationException($"SendSyncRequest 失败 rc={rc} x0=0x{r[0]:X}");
            }

            return Reply.Parse(_tlr);
        }

        // 列出本进程线程（系统线程 id → 名字），对照采样里的 @t<id>
        private static void DumpThreads()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/bin/ps", $"-M -p {Environment.ProcessId}") { RedirectStandardOutput = true };
                Console.Error.WriteLine(System.Diagnostics.Process.Start(psi).StandardOutput.ReadToEnd());
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
            }
        }

        private static bool _lastFlush;

        private static void RecordWriteUntracked(ulong va, ReadOnlySpan<byte> data, bool flush) => _lastFlush = flush;

        private static readonly List<(ulong Va, ulong Size)> _writes = new();
        private static void RecordWrite(ulong va, ulong size) => _writes.Add((va, size));
        // 写跟踪替身：报写时把涉及的 16 KB 页解开（同 track.c prep_range），并记下报了哪段
        [System.Runtime.InteropServices.DllImport("libc")] private static extern int mprotect(nint addr, nuint len, int prot);
        private static void UnprotectWrite(ulong va, ulong size)
        {
            ulong a = va & ~0x3FFFUL, e = (va + size + 0x3FFF) & ~0x3FFFUL;
            mprotect((nint)a, (nuint)(e - a), 3 /* READ | WRITE */);
            _writes.Add((va, size));
        }
        // 代写记录：照写进内存（写跟踪不在时 GuestMemory 自己写的效果），记 (地址, 长度, flush)
        private static readonly List<(ulong Va, int Size, bool Flush)> _untracked = new();
        private static void RecordUntracked(ulong va, ReadOnlySpan<byte> data, bool flush)
        {
            data.CopyTo(new Span<byte>((void*)va, data.Length));

            // 副本区域并发用例从 32 条线程同时写回：List 不是线程安全的
            lock (_untracked)
            {
                _untracked.Add((va, data.Length, flush));
            }
        }
        private static readonly List<(ulong Va, ulong Size)> _quiet = new();
        private static void RecordQuiet(ulong va, ulong size) => _quiet.Add((va, size));
        private static bool _gpuMod;
        private static readonly List<(ulong Va, ulong Size, MemoryPermission Perm, bool Guest)> _reprot = new();
        private static void RecordReprotect(ulong va, ulong size, MemoryPermission perm, bool guest) => _reprot.Add((va, size, perm, guest));
        private static bool FakeGpuModified(ulong va, ulong size) => _gpuMod;

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond())
                {
                    return true;
                }

                System.Threading.Thread.Sleep(10);
            }

            return cond();
        }

        // AudioRendererConfiguration（Pack=1，51 字节）：8 个 u32、3 个字节（声部丢弃 / 设备 / 执行模式）、4 个 u32；版本 REV11
        private static byte[] AudioConfig(uint sampleRate, uint sampleCount, uint mixBuffers, uint voices, uint sinks)
        {
            const int rev11 = ('R' << 0) | ('E' << 8) | ('V' << 16) | ('0' << 24) + (11 << 24);
            // PerformanceMetricFramesCount 之后 4 个字节 { VoiceDropEnabled, 保留, RenderingDevice, ExecutionMode }（原先只补 3 个，修订号整体错一位）
            return Bytes(sampleRate, sampleCount, mixBuffers, 0u, voices, sinks, 0u, 0u, new byte[4], 0u, 0u, 0u, rev11);
        }

        private static byte[] Name8(string s)
        {
            byte[] b = new byte[8];
            Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
            return b;
        }

        private static byte[] Bytes(params object[] fields)
        {
            MemoryStream ms = new();
            BinaryWriter w = new(ms);

            foreach (object f in fields)
            {
                switch (f)
                {
                    case int i: w.Write(i); break;
                    case uint u: w.Write(u); break;
                    case long l: w.Write(l); break;
                    case ulong ul: w.Write(ul); break;
                    case byte[] b: w.Write(b); break;
                }
            }

            return ms.ToArray();
        }

        private static void Run()
        {
            TimeZoneBinaryTest.Run(Check);
            CheatsTest.Run(Check);
            GuestBufferAllocatorTest.Run(Check);
            GuestMemoryFar();
            ShaderCodeTableTest.Run(Check);
            ShaderCodeTableDiffTest.Run(Check);
            AccountLifetime();
            AudioLifetimeTest();

            // ---- sm: ----
            byte* port = _game;
            Encoding.ASCII.GetBytes("sm:\0").CopyTo(new Span<byte>(port, 4));
            (int rc, ulong[] r) = Svc(0x1F, 0, 0, (ulong)port);
            uint sm = (uint)r[1];
            Check("ConnectToNamedPort(\"sm:\") 返回", "Handled x0=0 会话", $"{(rc == SvcResult.Handled ? "Handled" : "Unhandled")} x0={r[0]} {(FakeNative.KindOf(sm) == ObjKind.Session ? "会话" : FakeNative.KindOf(sm).ToString())}");

            Msg init = Msg.Cmif(0, Bytes(0L));
            init.SendPid = true;
            Check("sm Initialize 结果", 0u, Call(sm, init).Result);

            Reply gs = Call(sm, Msg.Cmif(1, Name8("nvdrv")));
            uint nv = gs.Move.Count == 1 ? gs.Move[0] : 0;
            Check("sm GetService(nvdrv)", "结果 0、move 1 个会话句柄", $"结果 {gs.Result}、move {gs.Move.Count} 个{(FakeNative.KindOf(nv) == ObjKind.Session ? "会话" : "非会话")}句柄");

            long red0 = Red.Total;
            Reply gz = Call(sm, Msg.Cmif(1, Name8("zzzz")));
            Check("sm GetService(未实现服务 zzzz)", $"结果 0x{(7u << 9) | 21u:X}、报红 +1、无句柄", $"结果 0x{gz.Result:X}、报红 +{Red.Total - red0}、{(gz.Move.Count == 0 ? "无句柄" : "有句柄")}");

            // sm 上的 CMIF 控制消息（类型 5）：老 SDK（我的世界：故事模式）nninitInitializeSdkModule 连上 sm 即发；
            //   原先 sm 只认请求 / TIPC，回 0xF601 → 游戏 Break。真值 Ryujinx ServerBase：3 回 0x8000、2 / 4 克隆会话（move 句柄）
            {
                // 发送失败（内核层非 0，如旧代码的 0xF601）记一行红，不带走整套单测
                static string Try(Func<string> f)
                {
                    try { return f(); }
                    catch (InvalidOperationException e) { return e.Message; }
                }

                long redC = Red.Total;
                Check("sm 控制 QueryPointerBufferSize", "SFCO、结果 0、值 0x8000、报红 +0", Try(() =>
                {
                    Msg sq = Msg.Cmif(3); sq.Type = 5;
                    Reply sqr = Call(sm, sq);
                    return $"SFCO、结果 {sqr.Result}、值 0x{*(int*)sqr.Data:X}、报红 +{Red.Total - redC}";
                }));

                Check("sm 控制 CloneCurrentObject → 新句柄上 GetService(nvdrv)", "结果 0、move 1 个新会话句柄；GetService 结果 0、move 1 个", Try(() =>
                {
                    Msg sc = Msg.Cmif(2, Bytes(0)); sc.Type = 5;
                    Reply scr = Call(sm, sc);
                    uint smc = scr.Move.Count == 1 ? scr.Move[0] : 0;
                    Reply cgs = smc != 0 ? Call(smc, Msg.Cmif(1, Name8("nvdrv"))) : null;
                    return $"结果 {scr.Result}、move {scr.Move.Count} 个{(smc != 0 && smc != sm && FakeNative.KindOf(smc) == ObjKind.Session ? "新会话" : "非新会话")}句柄；"
                        + (cgs == null ? "未发" : $"GetService 结果 {cgs.Result}、move {cgs.Move.Count} 个");
                }));

                long redU = Red.Total;
                Check("sm 控制 未知命令 9 / 转域 0（sm 不支持）", $"结果 0x{Red.ResultUnknownCommand:X} / 0x{Red.ResultUnknownCommand:X}、报红 +2", Try(() =>
                {
                    Msg su = Msg.Cmif(9); su.Type = 5;
                    Reply sur = Call(sm, su);
                    Msg sd = Msg.Cmif(0); sd.Type = 5;
                    Reply sdr = Call(sm, sd);
                    return $"结果 0x{sur.Result:X} / 0x{sdr.Result:X}、报红 +{Red.Total - redU}";
                }));
            }

            // ---- nvdrv ----
            uint tmem = FakeNative.TmemCreate((ulong)(_game + 0x8000), 0x4000, 3);
            Msg ni = Msg.Cmif(3, Bytes((long)0x4000));
            ni.Copy.Add(0xFFFF8001);
            ni.Copy.Add(tmem);
            Reply nir = Call(nv, ni);
            Check("nvdrv Initialize(进程伪句柄, 传输内存)", "结果 0、NvResult 0", $"结果 {nir.Result}、NvResult {*(uint*)nir.Data}");

            byte* path = _game + 0x100;
            byte[] ctrl = Encoding.ASCII.GetBytes("/dev/nvhost-ctrl");
            ctrl.CopyTo(new Span<byte>(path, ctrl.Length));
            Msg op = Msg.Cmif(0);
            op.A.Add(((ulong)path, (ulong)ctrl.Length));
            Reply opr = Call(nv, op);
            int fd = *(int*)opr.Data;
            Check("nvdrv Open(/dev/nvhost-ctrl)", "结果 0、fd ≥ 0、NvResult 0", $"结果 {opr.Result}、fd {(fd >= 0 ? "≥ 0" : fd.ToString())}、NvResult {*(uint*)(opr.Data + 4)}");

            // ioctl NVHOST_IOCTL_CTRL_SYNCPT_READ = _IOWR(0, 0x14, 8)：读 0 号同步点，起局后值 0
            byte* io = _game + 0x200;
            *(uint*)io = 0;
            *(uint*)(io + 4) = 0xDEAD;
            Msg ioc = Msg.Cmif(1, Bytes(fd, 0xC0080014u));
            ioc.A.Add(((ulong)io, 8));
            ioc.B.Add(((ulong)io, 8));
            Reply ior = Call(nv, ioc);
            Check("nvdrv Ioctl SYNCPT_READ(0)", "结果 0、NvResult 0、值 0", $"结果 {ior.Result}、NvResult {*(uint*)ior.Data}、值 {*(uint*)(io + 4)}");

            // NVN 设备初始化（动森实跑）：ctrl-gpu Ioctl3 VsmsMapping 的内联输出缓冲末项预填哨兵 0xc0c0，必须被改写成 SM 映射 {GPC, TPC}
            byte* gpuPath = _game + 0x180;
            byte[] cg = Encoding.ASCII.GetBytes("/dev/nvhost-ctrl-gpu");
            cg.CopyTo(new Span<byte>(gpuPath, cg.Length));
            Msg opg = Msg.Cmif(0); opg.A.Add(((ulong)gpuPath, (ulong)cg.Length));
            Reply opgr = Call(nv, opg);
            int gfd = *(int*)opgr.Data;
            byte* vargs = _game + 0x2400;
            byte* vinl = _game + 0x2440;
            new Span<byte>(vargs, 8).Clear();
            *(ushort*)vinl = 0x0000; *(ushort*)(vinl + 2) = 0xc0c0;   // 两个 SM；末项哨兵
            Msg vm = Msg.Cmif(12, Bytes(gfd, 0xC0084713u));
            vm.X.Add(((ulong)vargs, 8));
            vm.C.Add(((ulong)vargs, 8));
            vm.C.Add(((ulong)vinl, 4));
            Reply vmr = Call(nv, vm);
            // 期望值取自真值 Ryujinx 对拍记录（该调用内联输出 4:#00000000）：游戏预填的 0xc0c0 哨兵被零盖掉
            Check("nvdrv Ioctl3 VsmsMapping（NVN 哨兵）", "结果 0、NvResult 0、内联 00000000", $"结果 {vmr.Result}、NvResult {*(uint*)vmr.Data}、内联 {Convert.ToHexString(new ReadOnlySpan<byte>(vinl, 4))}");

            MeasureAlloc("nvdrv Ioctl SYNCPT_READ（旧 HLE 框架）", nv, () => { Msg x = Msg.Cmif(1, Bytes(fd, 0xC0080014u)); x.A.Add(((ulong)io, 8)); x.B.Add(((ulong)io, 8)); return x; });
            // 同一请求换一条线程发（游戏请求来自多条游戏线程）
            System.Threading.Thread otherThread = new(() => MeasureAlloc("nvdrv Ioctl SYNCPT_READ（旧 HLE 框架，另一线程）", nv, () => { Msg x = Msg.Cmif(1, Bytes(fd, 0xC0080014u)); x.A.Add(((ulong)io, 8)); x.B.Add(((ulong)io, 8)); return x; }));
            otherThread.Start();
            otherThread.Join();


            red0 = Red.Total;
            Reply unk = Call(nv, Msg.Cmif(99));
            Check("nvdrv 未实现命令 99", $"结果 0x{Red.ResultUnknownCommand:X}、报红 +1", $"结果 0x{unk.Result:X}、报红 +{Red.Total - red0}");

            // ---- 原生接口返回值自检（S2A_Init 起局对拍真原生层；这里核自检本身：约定一致 0 条不符，换成旧约定判得出）----
            red0 = Red.Total;
            int selfOk = Native.SelfCheck();
            long redOk = Red.Total - red0;
            delegate*<uint, int> realClose = Native.HandleClose;
            Native.HandleClose = &CloseOldConvention;
            red0 = Red.Total;
            int selfOld = Native.SelfCheck();
            long redOld = Red.Total - red0;
            Native.HandleClose = realClose;
            Check("原生接口返回值自检", "约定一致：不符 0、报红 +0；关句柄换旧约定（0 = 成功）：不符 1、报红 +1",
                $"约定一致：不符 {selfOk}、报红 +{redOk}；关句柄换旧约定（0 = 成功）：不符 {selfOld}、报红 +{redOld}");

            // ---- 时间：像游戏那样读时间共享内存（稳态时钟基准 + 本地系统时钟偏移），用游戏读到的计数器算当前时间 ----
            // 期望 = 宿主当前 UTC（没设 S2A_SYSTEM_TIME）；FakeNative 计数器零点在 10 小时前（同真机：宿主开机远早于进程启动），
            // Ryujinx 若用自己的计数器写基准，这里会差出 10 小时（对拍查出动森游戏时间晚 10 小时的根因）
            {
                Reply gtu = Call(sm, Msg.Cmif(1, Name8("time:u")));
                uint timeSvc = gtu.Move.Count == 1 ? gtu.Move[0] : 0;
                Reply gsh = Call(timeSvc, Msg.Cmif(20));
                uint tsh = gsh.Copy.Count == 1 ? gsh.Copy[0] : 0;
                byte* tm = (byte*)FakeNative.CookieOf(tsh);
                long hostNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long gameNow = GameNowFromTimeShm(tm);
                Check("游戏按共享内存 + 自己的计数器算出的当前时间", "与宿主 UTC 差 ≤ 5 秒", Math.Abs(gameNow - hostNow) <= 5 ? "与宿主 UTC 差 ≤ 5 秒" : $"差 {gameNow - hostNow} 秒");

                // 游戏日历补上计数器没看到的时长（#863）：原生层报的「没看到的时长」+1 小时（= 后台 1 小时，含锁屏睡眠），壳调补时入口后，
                //   游戏读共享内存算出的时间和 IPC GetCurrentTime 都应比宿主 UTC 快 1 小时（假计数器在「后台」期间没停，所以是快 1 小时、不是追平）；
                //   再调一次（回前台 / 获得焦点两条通知都会到）不重复补
                Reply guc = Call(timeSvc, Msg.Cmif(0));
                uint userClock = guc.Move.Count == 1 ? guc.Move[0] : 0;
                long IpcNow() { Reply r = Call(userClock, Msg.Cmif(0)); return r.Result == 0 ? *(long*)r.Data : long.MinValue; }
                FakeNative.UnseenNs += 3600L * 1_000_000_000;
                delegate* unmanaged<void> catchUp = &NativeTickSource.CatchUp;
                catchUp();
                hostNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long shmAfter = GameNowFromTimeShm(tm) - hostNow, ipcAfter = IpcNow() - hostNow;
                catchUp();
                hostNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long shmAgain = GameNowFromTimeShm(tm) - hostNow;
                static string Near(long d, long want) => Math.Abs(d - want) <= 5 ? $"快 {want} 秒（±5）" : $"快 {d} 秒";
                Check("补时（#863）：没看到的时长 +1 小时后，共享内存 / IPC / 再调一次", "共享内存 快 3600 秒（±5）、IPC 快 3600 秒（±5）、再调一次 快 3600 秒（±5）",
                    $"共享内存 {Near(shmAfter, 3600)}、IPC {Near(ipcAfter, 3600)}、再调一次 {Near(shmAgain, 3600)}");
            }

            // ---- acc:u0 开用户（10-26 普查一起健身吧：上游把模拟器常开的缺省用户报成「已开」，游戏选完用户后以为自己开过、
            //   去关一个从没开成功的句柄崩在 CloseUser）。期望照 SDK 语义手写：开 = GetBaasAccountManagerForApplication 建管理对象，
            //   关 = 释放它；应用侧 ListOpenUsers 只列本应用开着的用户 → 开前 0 个、开后 1 个（缺省用户）、关会话后 0 个 ----
            {
                Reply gacc = Call(sm, Msg.Cmif(1, Name8("acc:u0")));
                uint acc = gacc.Move.Count == 1 ? gacc.Move[0] : 0;
                byte* lu = _game + 0x3900;
                int Listed()
                {
                    new Span<byte>(lu, 0x80).Fill(0xEE);
                    Msg m = Msg.Cmif(3); m.C.Add(((ulong)lu, 0x80)); Call(acc, m);
                    int n = 0;
                    for (int i = 0; i < 8; i++) if (*(ulong*)(lu + 16 * i) != 0 || *(ulong*)(lu + 16 * i + 8) != 0) n++;
                    return n;
                }
                int before = Listed();
                MemoryStream us = new(); BinaryWriter uw = new(us);
                Ryujinx.HLE.HOS.Services.Account.Acc.AccountManager.DefaultUserId.Write(uw);
                Reply gmgr = Call(acc, Msg.Cmif(101, us.ToArray()));
                uint mgr = gmgr.Move.Count == 1 ? gmgr.Move[0] : 0;
                int opened = Listed();
                ulong first = *(ulong*)lu, second = *(ulong*)(lu + 8);
                FakeNative.HandleClose(mgr);
                int after = Listed();
                Check("acc:u0 ListOpenUsers 只列本应用开着的用户：开前 / 建管理对象后 / 关会话后", "0 个 / 1 个（缺省用户）/ 0 个",
                    $"{before} 个 / {opened} 个（{(opened == 1 && (first | second) != 0 ? "缺省用户" : $"{first:X}:{second:X}")}）/ {after} 个");
            }

            // ---- hid：子会话（MakeObject 挂钩）+ 共享内存（s2a_shmem_wrap）----
            Reply gh = Call(sm, Msg.Cmif(1, Name8("hid")));
            uint hid = gh.Move.Count == 1 ? gh.Move[0] : 0;
            Msg car = Msg.Cmif(0, Bytes(0UL));
            car.SendPid = true;
            Reply carr = Call(hid, car);
            uint res = carr.Move.Count == 1 ? carr.Move[0] : 0;
            // 手柄设置小程序只收单只 Joy-Con（10-26 普查 Zumba 0100C1700CD94000：样式 JoyconLeft, JoyconRight、手柄号 Player1–4；
            //   0100AA400A238000：样式 Handheld + 单只、手柄号 Player1,2 没有掌机号）：真机上横握一只确认 → 应答 true、Player1 配成右只（JoyRight）
            {
                var ui = Host.Device.UIHandler;
                var P = new[] { Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player2,
                                Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player3, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player4 };
                string Style(int i) => ((uint)Host.Device.Hid.SharedMemory.Npads[i].InternalState.StyleSet).ToString();
                bool z = ui.DisplayMessageDialog(new Ryujinx.HLE.HOS.Applets.ControllerAppletUIArgs { PlayerCountMin = 1, PlayerCountMax = 4, SupportedPlayers = P, IsDocked = false,
                    SupportedStyles = Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconLeft | Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight });
                string zs = Style(0);
                bool h = ui.DisplayMessageDialog(new Ryujinx.HLE.HOS.Applets.ControllerAppletUIArgs { PlayerCountMin = 1, PlayerCountMax = 2, SupportedPlayers = P[..2], IsDocked = false,
                    SupportedStyles = Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld | Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconLeft | Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight });
                string hs = Style(0);
                bool l = ui.DisplayMessageDialog(new Ryujinx.HLE.HOS.Applets.ControllerAppletUIArgs { PlayerCountMin = 1, PlayerCountMax = 1, SupportedPlayers = P[..1], IsDocked = false,
                    SupportedStyles = Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconLeft });
                string ls = Style(0);
                InputPump.SetPrimary(Host.Device, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Handheld, Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld);   // 还原，不影响后面的用例
                Check("手柄设置小程序：只收单只 Joy-Con → 配成单只（右只 JoyRight=16，只收左只时 JoyLeft=8）", "true 16 / true 16 / true 8",
                    $"{z.ToString().ToLower()} {zs} / {h.ToString().ToLower()} {hs} / {l.ToString().ToLower()} {ls}");
            }
            // 游戏只声明 Player1、不含掌机格（11-02 七龙珠电光炸裂ZERO：掌机格被断开，触屏手柄全无反应）：主手柄从掌机格改到 Player1；游戏再声明掌机格时挪回
            {
                var npads = Host.Device.Hid.Npads;
                InputPump.SetPrimary(Host.Device, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Handheld, Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld);
                npads.ClearSupportedPlayers(); npads.SetSupportedPlayer(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
                InputPump.FollowSupportedPlayers(Host.Device);
                string only1 = InputPump.Player.ToString();
                InputPump.FollowSupportedPlayers(Host.Device);
                string again = InputPump.Player.ToString();
                npads.SetSupportedPlayer(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Handheld);
                InputPump.FollowSupportedPlayers(Host.Device);
                string back = InputPump.Player.ToString();
                for (int i = 0; i < 9; i++) npads.SetSupportedPlayer((Ryujinx.HLE.HOS.Services.Hid.PlayerIndex)i);   // 还原成 Player1–8 + 掌机格（下标 8）都收
                InputPump.SetPrimary(Host.Device, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Handheld, Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld);   // 还原
                Check("游戏只收 Player1 → 虚拟手柄离开掌机格；再收掌机格 → 挪回", "Player1 / Player1 / Handheld", $"{only1} / {again} / {back}");
            }
            // 游戏声明的样式不收主手柄现在的样式（#359：开局「用你要用的手柄按 A」只收 掌机 / 单只 Joy-Con / 精灵球；Remap 只会 Pro ↔ 双 Joy-Con，原先直接断开）：
            //   掌机档 → 掌机格；主机档（掌机格被 Remap 去掉）→ 单只右；游戏又收 Pro → 换回 Player1 的 Pro；只收双 Joy-Con 时 Pro 不动（Remap 自己换）
            {
                var npads = Host.Device.Hid.Npads;
                var full = npads.SupportedStyleSets; bool docked0 = Host.Device.System.State.DockedMode;
                var picky = Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld | Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconLeft | Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight;
                string Run(bool docked, Ryujinx.HLE.HOS.Services.Hid.ControllerType styles)
                {
                    Host.Device.System.State.DockedMode = docked; npads.SupportedStyleSets = full;
                    InputPump.SetPrimary(Host.Device, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1, Ryujinx.HLE.HOS.Services.Hid.ControllerType.ProController);
                    npads.SupportedStyleSets = styles; InputPump.FollowSupportedStyles(Host.Device);
                    string first = $"{InputPump.Player} {InputPump.Type}";
                    npads.SupportedStyleSets = full; InputPump.FollowSupportedStyles(Host.Device);
                    return first + " → " + $"{InputPump.Player} {InputPump.Type}";
                }
                string got = string.Join(" / ", Run(false, picky), Run(true, picky), Run(true, Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconPair));
                Host.Device.System.State.DockedMode = docked0; npads.SupportedStyleSets = full;
                InputPump.SetPrimary(Host.Device, Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Handheld, Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld);   // 还原
                Check("游戏不收 Pro 手柄 → 换成它收的样式，再收 Pro 时换回（掌机档 / 主机档 / 只收双 Joy-Con）",
                    "Handheld Handheld → Player1 ProController / Player1 JoyconRight → Player1 ProController / Player1 ProController → Player1 ProController", got);
            }
            // 单只 Joy-Con 横握（11-03 Super Mario Party）：横握后看到的 ABXY / 摇杆换成物理键 / 物理方向（右只顺时针 90°：A→X、B→A、X→Y、Y→B，摇杆 (x,y)→(−y,x)；
            //   左只逆时针 90°：A→下、B→左、X→右、Y→上，摇杆 (x,y)→(y,−x)）；竖握 / Pro 手柄不动
            {
                var HZ = Ryujinx.HLE.HOS.Services.Hid.Types.NpadJoyHoldType.Horizontal; var VT = Ryujinx.HLE.HOS.Services.Hid.Types.NpadJoyHoldType.Vertical;
                string R(Ryujinx.HLE.HOS.Services.Hid.ControllerType t, Ryujinx.HLE.HOS.Services.Hid.Types.NpadJoyHoldType h, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys k, int x, int y)
                { var g = new Ryujinx.HLE.HOS.Services.Hid.GamepadInput { Buttons = k }; g.LStick = new Ryujinx.HLE.HOS.Services.Hid.JoystickPosition { Dx = x, Dy = y }; InputPump.RotateSideways(ref g, t, h); return $"{g.Buttons}({g.LStick.Dx},{g.LStick.Dy})"; }
                string got = string.Join(" / ", R(Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight, HZ, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.A, 0, 32767),
                    R(Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight, HZ, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.B | Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.Plus, 32767, 0),
                    R(Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconLeft, HZ, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.A | Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.Y, 0, 32767),
                    R(Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconRight, VT, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.A, 0, 32767),
                    R(Ryujinx.HLE.HOS.Services.Hid.ControllerType.ProController, HZ, Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.A, 0, 32767));
                Check("单只 Joy-Con 横握换物理键：右只 A / 上推、右只 B+ / 右推、左只 A+Y / 上推、右只竖握、Pro", "X(-32767,0) / A, Plus(0,32767) / DpadUp, DpadDown(32767,0) / A(0,32767) / A(0,32767)", got);
            }
            StickButtonsTest();
            TwoPadsTest();

            Check("hid CreateAppletResource → 子会话", "结果 0、move 1 个会话句柄", $"结果 {carr.Result}、move {carr.Move.Count} 个{(FakeNative.KindOf(res) == ObjKind.Session ? "会话" : "非会话")}句柄");

            // 上游占位实现（Logger.Stub）进【桩】汇总：hid ActivateDebugPad 是上游桩（记一行 Stubbed 后回成功）
            Msg adp = Msg.Cmif(1, Bytes(0UL));
            adp.SendPid = true;
            Reply adpr = Call(hid, adp);
            Check("hid ActivateDebugPad（上游桩）进【桩】汇总", "结果 0、汇总含 ActivateDebugPad", $"结果 {adpr.Result}、汇总{(Red.StubSummary().Contains("ActivateDebugPad") ? "含" : "不含")} ActivateDebugPad");

            // hid SendVibrationValues（#206，怪猎每秒约 170 次）：生产口径（宿主不接震动输出、没有震动队列）稳态零分配；
            // 再接上 Player1 的队列核分组语义：位置 0 A、位置 1 B、位置 0 C → 入队 (A, B)、(C, B)，未更新的右侧保持。
            {
                var vh = (Ryujinx.HLE.HOS.Services.Hid.VibrationDeviceHandle*)(_game + 0x3800);
                var vv = (Ryujinx.HLE.HOS.Services.Hid.VibrationValue*)(_game + 0x3840);
                Ryujinx.HLE.HOS.Services.Hid.VibrationValue A = new() { AmplitudeLow = 0.1f, FrequencyLow = 160f, AmplitudeHigh = 0.2f, FrequencyHigh = 320f },
                    B = new() { AmplitudeLow = 0.3f, FrequencyLow = 160f, AmplitudeHigh = 0.4f, FrequencyHigh = 320f },
                    C = new() { AmplitudeLow = 0.5f, FrequencyLow = 160f, AmplitudeHigh = 0.6f, FrequencyHigh = 320f };
                byte[] pos = [0, 1, 0];
                for (int i = 0; i < 3; i++) { vh[i] = new() { DeviceType = 3, PlayerId = 0, Position = pos[i] }; }
                vv[0] = A; vv[1] = B; vv[2] = C;
                Msg Sv() { Msg m = Msg.Cmif(206, Bytes(0L)); m.X.Add(((ulong)vh, 12)); m.X.Add(((ulong)vv, 48)); return m; }
                MeasureAlloc("hid SendVibrationValues（#206，3 个设备句柄，宿主无震动队列）", hid, Sv);
                var q = Host.Device.Hid.Npads.GetRumbleQueue(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
                while (q.TryDequeue(out _)) { }
                Host.Device.Hid.Npads.LastVibrationValues.Remove(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
                Reply svr = Call(hid, Sv());
                var got = q.ToArray();
                bool okq = got.Length == 2 && got[0].Item1 == A && got[0].Item2 == B && got[1].Item1 == C && got[1].Item2 == B;
                Check("hid SendVibrationValues 分组入队（位置 0 A、1 B、0 C）", "结果 0、入队 2 项：(A, B)、(C, B)",
                    $"结果 {svr.Result}、入队 {got.Length} 项{(okq ? "：(A, B)、(C, B)" : "（内容不符）")}");
                Host.Device.Hid.Npads.RumbleQueues.Remove(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
            }

            HidInvalidParameters(hid);

            // ---- 六轴「是否静止」与各样式记录（11-05 #55 Switch Sports Joy-Con 校准「无法调整」）：期望手写 ----
            //   Player1 挂 Pro 手柄，写一笔手机平放静止的实测读数 (0.036, 0.004, -0.9996) g：① 静止（模长 1.0003、角速度 0）；② 角速度 (0.1, 0, 0) 转/秒 → 没静止
            //   （上游按「加速度绝对值和 ≤ 1」判，这笔和 1.0404 判没静止）；③ 其余样式记录（双 Joy-Con 左）加速度 Z = -0.9996（上游写全零 0）；
            //   ⑥ 改挂双 Joy-Con、只喂一份读数（单一体感源）：右手记录加速度 Z = -0.9996（上游等第二份、右手从不更新，仍是 0）
            {
                var npads = Host.Device.Hid.Npads;
                npads.Configure(new Ryujinx.HLE.HOS.Services.Hid.ControllerConfig { Player = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1, Type = Ryujinx.HLE.HOS.Services.Hid.ControllerType.ProController });
                npads.Update(new List<Ryujinx.HLE.HOS.Services.Hid.GamepadInput> { new() { PlayerId = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1 } });
                void Six(float gx) => npads.UpdateSixAxis(new List<Ryujinx.HLE.HOS.Services.Hid.SixAxisInput> { new() { PlayerId = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1,
                    Accelerometer = new System.Numerics.Vector3(0.036f, 0.004f, -0.9996f), Gyroscope = new System.Numerics.Vector3(gx, 0, 0), Orientation = new float[9] } });
                Six(0); bool r1 = npads.isAtRest(0); float z1 = Host.Device.Hid.SharedMemory.Npads[0].InternalState.JoyDualSixAxisSensor.GetCurrentEntryRef().Acceleration.Z;
                Six(0.1f); bool r2 = npads.isAtRest(0);
                Check("六轴静止：① 平放静止 / ② 转动中", "静止 / 没静止", $"{(r1 ? "静止" : "没静止")} / {(r2 ? "静止" : "没静止")}");
                Check("六轴其余样式记录：③ Pro 手柄时双 Joy-Con 左的加速度 Z", "-0.9996", $"{z1}");
                npads.Configure(new Ryujinx.HLE.HOS.Services.Hid.ControllerConfig { Player = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1, Type = Ryujinx.HLE.HOS.Services.Hid.ControllerType.JoyconPair });
                npads.Update(new List<Ryujinx.HLE.HOS.Services.Hid.GamepadInput> { new() { PlayerId = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1 } });
                Six(0); float zr = Host.Device.Hid.SharedMemory.Npads[0].InternalState.JoyDualRightSixAxisSensor.GetCurrentEntryRef().Acceleration.Z;
                Check("六轴单一体感源：⑥ 双 Joy-Con、只喂一份读数时右手记录的加速度 Z", "-0.9996", $"{zr}");
                // ⑤ 取玩家号：Player2 挂 Pro 手柄、写一笔转动中（0.5 转/秒）的读数，Player1 不接；经 IPC 82 用 Player2 的句柄（样式 3、玩家 1）问 → 没静止（0）。
                //   上游 (h << 8) & 0xff 恒取玩家 0：问到没接手柄的 Player1，固定回「静止」（1）
                npads.Configure(new Ryujinx.HLE.HOS.Services.Hid.ControllerConfig { Player = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player2, Type = Ryujinx.HLE.HOS.Services.Hid.ControllerType.ProController });
                npads.Update(new List<Ryujinx.HLE.HOS.Services.Hid.GamepadInput> { new() { PlayerId = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player2 } });
                npads.UpdateSixAxis(new List<Ryujinx.HLE.HOS.Services.Hid.SixAxisInput> { new() { PlayerId = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player2,
                    Accelerometer = new System.Numerics.Vector3(0, 0, -1), Gyroscope = new System.Numerics.Vector3(0.5f, 0.5f, 0.5f), Orientation = new float[9] } });
                Msg Rest(int h) { Msg m = Msg.Cmif(82, Bytes(h, 0, 0L)); m.SendPid = true; return m; }
                Reply rp2 = Call(hid, Rest(0x0103)); uint rp2r = rp2.Result; byte rp2v = rp2.Data[0];
                Check("六轴取玩家号：⑤ 用 Player2 的句柄 0x0103 问是否静止（Player2 转动中、Player1 没接；0 = 没静止）", "结果 0、0", $"结果 {rp2r}、{rp2v}");
                // ⑦ 掌机句柄（11-06 密特罗德 究极 复刻版开局即退）：句柄第 2 字节是 NpadIdType，掌机 0x20；按下标取会越界、IPC 82 报参数异常、游戏 Break。
                //   换成手柄下标（掌机 = 8）后：掌机没接 → 样式空 → 回「静止」（1），结果 0
                Reply rph = Call(hid, Rest(0x2002)); uint rphr = rph.Result; byte rphv = rph.Data[0];
                Check("六轴取玩家号：⑦ 用掌机句柄 0x2002 问是否静止（掌机没接；1 = 静止）", "结果 0、1", $"结果 {rphr}、{rphv}");
                npads.Configure();
            }

            // ---- 六轴校准存 / 读（IHidServer 86 / 87，11-05 Switch Sports Joy-Con 校准页按 A 存校准 → 原先 86 未实现报红、游戏 Break）----
            //   期望手写：句柄 5 存 0x744 字节（第 i 字节 = i·7+3 取低 8 位）→ 87 读回逐字节相同；句柄 6 没存过 → 读回全零（缓冲先填 0xEE，确认真写了）；
            //   存 → 存另一段 → 读回后一段（覆盖）；84 开句柄 5 直通、85 查句柄 5 = 1、查句柄 6 = 0（按句柄分开）
            {
                const int N = 0x744; byte* src = _game + 0x9000, dst = _game + 0x9800;
                for (int i = 0; i < N; i++) src[i] = (byte)(i * 7 + 3);
                Msg Store(int h, ulong len) { Msg m = Msg.Cmif(86, Bytes(h, 0, 0L)); m.SendPid = true; m.A.Add(((ulong)src, len)); return m; }
                Msg Load(int h) { Msg m = Msg.Cmif(87, Bytes(h, 0, 0L)); m.SendPid = true; m.B.Add(((ulong)dst, (ulong)N)); return m; }
                Reply rs = Call(hid, Store(5, N));
                new Span<byte>(dst, N).Fill(0xEE);
                Reply rl = Call(hid, Load(5));
                bool same = new Span<byte>(src, N).SequenceEqual(new Span<byte>(dst, N));
                Check("hid 86 存校准 → 87 读回（句柄 5，0x744 字节）", "存 结果 0、读 结果 0、逐字节相同", $"存 结果 {rs.Result}、读 结果 {rl.Result}、{(same ? "逐字节相同" : "不同")}");
                new Span<byte>(dst, N).Fill(0xEE);
                Reply r6 = Call(hid, Load(6));
                bool zero = new Span<byte>(dst, N).IndexOfAnyExcept((byte)0) < 0;
                Check("hid 87 没存过的句柄 6 → 读回全零", "结果 0、全零", $"结果 {r6.Result}、{(zero ? "全零" : "不是全零")}");
                for (int i = 0; i < N; i++) src[i] = (byte)(255 - i);
                Call(hid, Store(5, N)); Call(hid, Load(5));
                Check("hid 86 再存一段 → 87 读回后一段（覆盖）", "相同", new Span<byte>(src, N).SequenceEqual(new Span<byte>(dst, N)) ? "相同" : "不同");
                Msg En(int h, uint on) { Msg m = Msg.Cmif(84, Bytes(on, h, 0L)); m.SendPid = true; return m; }
                Msg Is(int h) { Msg m = Msg.Cmif(85, Bytes(h, 0, 0L)); m.SendPid = true; return m; }
                Reply e5 = Call(hid, En(5, 1));
                Reply q5 = Call(hid, Is(5)); uint r5 = q5.Result; byte v5 = q5.Data[0];   // 应答数据指向共用消息缓冲：下一次调用前取值
                Reply q6 = Call(hid, Is(6)); uint r6b = q6.Result; byte v6 = q6.Data[0];
                Check("hid 84 / 85 直通开关按句柄分开（开 5 → 查 5 / 查 6）", "结果 0 / 0 / 0，1 / 0", $"结果 {e5.Result} / {r5} / {r6b}，{v5} / {v6}");
                // 88 GetSixAxisSensorIcInformation：期望按我方六轴量程手写（±2000 度/秒、±8 g；零点容差 ±0.0612、灵敏度 0.95–1.05、串扰 ±0.003），按偏移逐个核；
                //   缓冲给 0x100、先填 0xEE：只写 0xC8 字节，后面不动
                {
                    byte* ic = _game + 0xA000; new Span<byte>(ic, 0x100).Fill(0xEE);
                    Msg Ic(int h) { Msg m = Msg.Cmif(88, Bytes(h, 0, 0L)); m.SendPid = true; m.B.Add(((ulong)ic, 0x100UL)); return m; }
                    Reply ri = Call(hid, Ic(5)); uint rir = ri.Result;
                    float F(int off) => *(float*)(ic + off);
                    (int Off, float Want)[] want = { (0x00, 2000f), (0x04, -0.0612f), (0x08, 0.0612f), (0x18, 0.0612f), (0x1C, 0.95f), (0x20, -0.003f), (0x2C, 0.95f), (0x3C, 0.95f),
                        (0x40, 1.05f), (0x44, 0.003f), (0x60, 1.05f), (0x64, 8f), (0x68, -0.0612f), (0x7C, 0.0612f), (0x80, 0.95f), (0x84, -0.003f), (0xA0, 0.95f), (0xA4, 1.05f), (0xC4, 1.05f) };
                    int bad = 0; string first = "";
                    foreach (var (off, w) in want) if (F(off) != w) { if (bad++ == 0) first = $"0x{off:X} = {F(off)}（期望 {w}）"; }
                    bool tail = new Span<byte>(ic + 0xC8, 0x100 - 0xC8).IndexOfAnyExcept((byte)0xEE) < 0;
                    Check("hid 88 IC 信息：量程 / 零点容差 / 灵敏度上下限按偏移核 19 处、只写 0xC8 字节", "结果 0、19 处全对、0xC8 之后未动",
                        $"结果 {rir}、{(bad == 0 ? "19 处全对" : $"{bad} 处不对（首个 {first}）")}、{(tail ? "0xC8 之后未动" : "0xC8 之后被改")}");
                    Check("六轴量程饱和与 88 同一份常量（超量程读数饱和到 ±2000 度/秒、±8 g）", "2000 / -2000 / 8 / -8 / 1.5",
                        $"{Ryujinx.HLE.HOS.Services.Hid.Types.SixAxisSensorIc.ClampAngularRate(5000f)} / {Ryujinx.HLE.HOS.Services.Hid.Types.SixAxisSensorIc.ClampAngularRate(-2500f)} / {Ryujinx.HLE.HOS.Services.Hid.Types.SixAxisSensorIc.ClampAcceleration(12f)} / {Ryujinx.HLE.HOS.Services.Hid.Types.SixAxisSensorIc.ClampAcceleration(-9f)} / {Ryujinx.HLE.HOS.Services.Hid.Types.SixAxisSensorIc.ClampAcceleration(1.5f)}");
                }
                lock (Host.Device.Hid.SixAxisCalibration) Host.Device.Hid.SixAxisCalibration.Clear();
                lock (Host.Device.Hid.SixAxisUnalteredPassthrough) Host.Device.Hid.SixAxisUnalteredPassthrough.Clear();
            }

            // ---- [输入能力] 收局摘要行（S2AInputCaps）：游戏自己向 hid 要的手柄样式并集 / 六轴启动次数 / 震动调用次数（能力卡 controller / rumble / gyro 的来源）----
            //   经真 IPC 调 hid 命令（钩子在 IHidServer 里），期望按本段发出的调用直接数：样式 Pro、再 Pro|掌机 → 并集 Pro,掌机；六轴 Start 2 次；
            //   震动 SendVibrationValue 零幅度 1 次 + SendVibrationValues 非零 1 次、全零 1 次 → 调用 3、非零 1。前后各复位，不进别的用例和收局行
            {
                Ryujinx.Common.S2AInputCaps.Reset();
                Check("[输入能力] 没调过相关 hid 命令时摘要行", "[输入能力] styles=- sixaxis_start=0 vib_calls=0 vib_nonzero=0", Ryujinx.Common.S2AInputCaps.Summary());

                var npads = Host.Device.Hid.Npads;
                System.Reflection.PropertyInfo styleProp = npads.GetType().GetProperty("SupportedStyleSets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                object styleKeep = styleProp.GetValue(npads);
                int pro = (int)Ryujinx.HLE.HOS.Services.Hid.ControllerType.ProController, hh = (int)Ryujinx.HLE.HOS.Services.Hid.ControllerType.Handheld;
                Msg Style(int t) { Msg m = Msg.Cmif(100, Bytes(t, 0, 0L)); m.SendPid = true; return m; }   // SetSupportedNpadStyleSet(u32 样式, 填充, aruid)
                Call(hid, Style(pro));
                Call(hid, Style(pro | hh));
                for (int i = 0; i < 2; i++) { Msg m = Msg.Cmif(66, Bytes(0, 0, 0L)); m.SendPid = true; Call(hid, m); }   // StartSixAxisSensor(句柄, 填充, aruid)
                Call(hid, Msg.Cmif(201, Bytes(3u, 0, 0, 0, 0, 0L)));   // SendVibrationValue：句柄（类型 3、玩家 0、位置 0）+ 全零幅度（4 个 float 0，按 int 0 写同字节）
                var cvh = (Ryujinx.HLE.HOS.Services.Hid.VibrationDeviceHandle*)(_game + 0x3D00);
                var cvv = (Ryujinx.HLE.HOS.Services.Hid.VibrationValue*)(_game + 0x3D40);
                cvh[0] = new() { DeviceType = 3, PlayerId = 0, Position = 0 };
                cvv[0] = new() { AmplitudeLow = 0.5f, FrequencyLow = 160f, AmplitudeHigh = 0f, FrequencyHigh = 320f };
                Msg Svs() { Msg m = Msg.Cmif(206, Bytes(0L)); m.X.Add(((ulong)cvh, 4)); m.X.Add(((ulong)cvv, 16)); return m; }
                Call(hid, Svs());
                cvv[0] = default;
                Call(hid, Svs());
                Host.Device.Hid.Npads.RumbleQueues.Remove(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
                Host.Device.Hid.Npads.LastVibrationValues.Remove(Ryujinx.HLE.HOS.Services.Hid.PlayerIndex.Player1);
                Check("[输入能力] 经 hid 命令调过后摘要行（样式并集、六轴次数、震动调用 / 非零次数）",
                    "[输入能力] styles=ProController,Handheld sixaxis_start=2 vib_calls=3 vib_nonzero=1", Ryujinx.Common.S2AInputCaps.Summary());

                styleProp.SetValue(npads, styleKeep);
                Ryujinx.Common.S2AInputCaps.Reset();
            }

            // 每帧都调的上游桩（动森 pctl IsFreeCommunicationAvailable 每秒 25 次）：稳态零分配、汇总里次数照计
            MeasureAlloc("hid EndPermitVibrationSession（上游桩，反复调）", hid, () => Msg.Cmif(210));
            string stubLine = Red.StubSummary();
            int at = stubLine.IndexOf("EndPermitVibrationSession: Stubbed.×", StringComparison.Ordinal);
            Check("上游桩反复调：汇总次数照计（预热 100 + 计量 1000 次）", "1100", at < 0 ? "汇总里没有" : new string(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.TakeWhile(stubLine[(at + "EndPermitVibrationSession: Stubbed.×".Length)..], char.IsDigit))));

            // 合成线程每帧取帧时等围栏（AndroidFence.WaitForever → NvFence.Wait）：围栏已到达时不该分配（原先先建事件 + 闭包约 192 B 才发现已到达）
            {
                var fence = new Ryujinx.HLE.HOS.Services.Nv.Types.NvFence { Id = 0, Value = Host.Device.Gpu.Synchronization.GetSyncpointValue(0) };
                for (int i = 0; i < 100; i++) fence.Wait(Host.Device.Gpu, TimeSpan.FromMilliseconds(100));
                long f0 = GC.GetAllocatedBytesForCurrentThread();
                bool timedOut = false;
                for (int i = 0; i < 1000; i++) timedOut |= fence.Wait(Host.Device.Gpu, TimeSpan.FromMilliseconds(100));
                _alloc.Add(("等已到达的围栏（合成线程取帧，每次）", (GC.GetAllocatedBytesForCurrentThread() - f0) / 1000.0));
                Check("等已到达的围栏：不超时", "不超时", timedOut ? "超时" : "不超时");
            }

            // 合成线程每圈（SurfaceFlinger.HandleComposition，10-21 动森 MARK 后每 20 秒约 100 KB 稳态分配）：发垂直同步、给没到达的围栏登记回调再由 GPU 递增触发
            {
                for (int i = 0; i < 100; i++) Host.Device.System.SignalVsync();
                long v0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) Host.Device.System.SignalVsync();
                _alloc.Add(("合成线程发垂直同步 SignalVsync（每次）", (GC.GetAllocatedBytesForCurrentThread() - v0) / 1000.0));

                var sync = Host.Device.Gpu.Synchronization; const uint sp = 5; int fired = 0;
                Action<Ryujinx.Graphics.Gpu.Synchronization.SyncpointWaiterHandle> cb = _ => fired++;
                // AndroidFence.RegisterCallback（internal）对每个围栏调 RegisterCallbackOnSyncpointPooled（不要句柄、走句柄池）：直接测这一层
                for (int i = 0; i < 1100; i++)
                {
                    if (i == 100) v0 = GC.GetAllocatedBytesForCurrentThread();
                    sync.RegisterCallbackOnSyncpointPooled(sp, sync.GetSyncpointValue(sp) + 1, cb);
                    sync.IncrementSyncpoint(sp);
                }
                _alloc.Add(("合成线程给没到达的围栏登记回调 + 递增触发（每帧）", (GC.GetAllocatedBytesForCurrentThread() - v0) / 1000.0));
                Check("围栏回调：登记 1100 次全部触发", "1100", fired.ToString());
            }

            int wrapped0 = FakeNative.Wrapped.Count;
            Reply shm = Call(res, Msg.Cmif(0));
            uint shmH = shm.Copy.Count == 1 ? shm.Copy[0] : 0;
            ulong wrapSize = FakeNative.Wrapped.Count > wrapped0 ? FakeNative.Wrapped[^1].Size : 0;
            ulong wrapHost = FakeNative.Wrapped.Count > wrapped0 ? FakeNative.Wrapped[^1].Host : 1;
            Check("hid 共享内存后备地址（原生层 vm_remap 要 16 KB 对齐）", "16 KB 对齐", wrapHost % 0x4000 == 0 ? "16 KB 对齐" : $"0x{wrapHost:X} 未对齐");
            Check("IAppletResource GetSharedMemoryHandle", "结果 0、copy 1 个共享内存句柄、后备 0x40000 字节", $"结果 {shm.Result}、copy {shm.Copy.Count} 个{(FakeNative.KindOf(shmH) == ObjKind.Shmem ? "共享内存" : "非共享内存")}句柄、后备 0x{wrapSize:X} 字节");

            // ---- 事件：Ryujinx KEvent 交给游戏 → 原生事件；Ryujinx 侧触发 / 清除同步；再次交出走 dup ----
            Ryujinx.HLE.HOS.Kernel.Threading.KEvent kev = new(Dispatcher.Instance.Kernel);
            uint eh1 = Dispatcher.EventHandleFor(kev.ReadableEvent);
            ulong evId = FakeNative.CookieOf(eh1);
            kev.WritableEvent.Signal();
            bool afterSignal = FakeNative.Events[evId];
            kev.WritableEvent.Clear();
            bool afterClear = FakeNative.Events[evId];
            uint eh2 = Dispatcher.EventHandleFor(kev.ReadableEvent);
            Check("KEvent 交给游戏后 Ryujinx 侧 Signal / Clear", "读端句柄、触发后 True、清除后 False", $"{(FakeNative.KindOf(eh1) == ObjKind.EventR ? "读端句柄" : "非读端")}、触发后 {afterSignal}、清除后 {afterClear}");
            Check("同一 KEvent 第二次交给游戏", "新句柄、同一原生事件", $"{(eh2 != eh1 && eh2 != 0 ? "新句柄" : "旧句柄或 0")}、{(FakeNative.CookieOf(eh2) == evId ? "同一原生事件" : "不同事件")}");

            // 已触发的 KEvent 首次交给游戏：原生事件补触发（游戏马上能等到）
            Ryujinx.HLE.HOS.Kernel.Threading.KEvent kev2 = new(Dispatcher.Instance.Kernel);
            kev2.WritableEvent.Signal();
            uint eh3 = Dispatcher.EventHandleFor(kev2.ReadableEvent);
            Check("已触发的 KEvent 首次交给游戏", "原生事件 True", $"原生事件 {FakeNative.Events[FakeNative.CookieOf(eh3)]}");

            // ---- 声音 audren:u（Ryujinx.Horizon 新框架；服务在我方线程上初始化并注册，游戏线程上直调）----
            bool registered = WaitFor(() => Array.IndexOf(HorizonHost.Services, "audren:u") >= 0, 10_000);
            Check("Horizon 服务注册（audren:u 进 sm 表，10 秒内）", "已注册", registered ? "已注册" : $"未注册（已注册：{string.Join(",", HorizonHost.Services)}）");
            // 服务堆预留（NativeSyscall.HeapReserve）：期望按服务端分配算——最大的 wlan 129 会话 ×（指针缓冲 0x1000 + 消息缓冲 0x100）= 561 KB，
            // HeapAllocator 按 2 MB 步长扩堆 ⇒ 每条服务线程最大一次请求 2 MB；wlan 是最后几个起的服务之一，等它进 sm 表再读
            bool wlanUp = WaitFor(() => Array.IndexOf(HorizonHost.Services, "wlan") >= 0, 10_000);
            Check("Horizon 服务堆：各服务线程最大一次扩堆（wlan 已注册）", "2 MB，不超预留 4 MB",
                $"{NativeSyscall.MaxHeapSize >> 20} MB，{(NativeSyscall.MaxHeapSize <= NativeSyscall.HeapReserve ? "不超" : "超出")}预留 {NativeSyscall.HeapReserve >> 20} MB{(wlanUp ? "" : "；wlan 10 秒内没注册")}");
            // 服务堆 39 位判定（审计 #859 F-heap-39-ok）：结尾恰好 = 1<<39 装得下、多 1 字节装不下；起点已在界外、长度把 ptr + len 撑到上溢也装不下
            {
                const ulong lim = 1UL << 39, r4 = NativeSyscall.HeapReserve;
                Check("Horizon 服务堆 39 位判定：恰好到界 / 多 1 字节 / 起点在界外 / 长度上溢", "装得下、装不下、装不下、装不下",
                    string.Join("、", new[] { (lim - r4, r4), (lim - r4 + 1, r4), (lim, 1UL), (0x1000UL, ulong.MaxValue) }.Select(c => NativeSyscall.FitsHipcAddr(c.Item1, c.Item2) ? "装得下" : "装不下")));
            }

            Reply ga = Call(sm, Msg.Cmif(1, Name8("audren:u")));
            uint aud = ga.Move.Count == 1 ? ga.Move[0] : 0;
            Check("sm GetService(audren:u)", "结果 0、move 1 个会话句柄", $"结果 {ga.Result}、move {ga.Move.Count} 个{(FakeNative.KindOf(aud) == ObjKind.Session ? "会话" : "非会话")}句柄");

            // aud:u（Switch 2 SDK 的统一音频入口，Pokopia）：命令 2 取到的渲染器管理对象与 audren:u 同一份——GetWorkBufferSize 同参数同结果；命令 0 / 1 各回一个对象
            {
                Reply gu = Call(sm, Msg.Cmif(1, Name8("aud:u")));
                uint audU = gu.Move.Count == 1 ? gu.Move[0] : 0;
                Reply r0 = audU != 0 ? Call(audU, Msg.Cmif(0)) : null, r1 = audU != 0 ? Call(audU, Msg.Cmif(1)) : null, r2 = audU != 0 ? Call(audU, Msg.Cmif(2)) : null;
                uint audUr = r2 != null && r2.Move.Count == 1 ? r2.Move[0] : 0;
                byte[] c0 = AudioConfig(sampleRate: 48000, sampleCount: 240, mixBuffers: 24, voices: 24, sinks: 1);
                long viaU = audUr != 0 ? *(long*)Call(audUr, Msg.Cmif(1, c0)).Data : -1, direct = *(long*)Call(aud, Msg.Cmif(1, c0)).Data;
                Check("aud:u：命令 0 / 1 / 2 各回对象；经命令 2 的渲染器 GetWorkBufferSize 与 audren:u 相同", "对象 1 1 1、相同",
                    $"对象 {r0?.Move.Count ?? 0} {r1?.Move.Count ?? 0} {r2?.Move.Count ?? 0}、{(viaU == direct ? "相同" : $"{viaU} ≠ {direct}")}");
            }

            byte[] cfg = AudioConfig(sampleRate: 48000, sampleCount: 240, mixBuffers: 24, voices: 24, sinks: 1);
            Reply wb = Call(aud, Msg.Cmif(1, cfg));
            long wbSize = *(long*)wb.Data;
            Check("audren GetWorkBufferSize(48 kHz, 240 样本, 24 声部)", "结果 0、大小 > 0 且 4 KB 对齐", $"结果 {wb.Result}、大小 {(wbSize > 0 && wbSize % 0x1000 == 0 ? "> 0 且 4 KB 对齐" : wbSize.ToString())}");

            // OpenAudioRenderer：参数（对齐 1）在 0，其后按 8 对齐：workBufferSize、aruid、pid 占位；复制句柄 [工作缓冲传输内存, 当前进程]
            int wbBytes = (int)Math.Min(Math.Max(wbSize, 0x1000), 0x400000);
            byte* work = FakeNative.Alloc(wbBytes);
            uint wtm = FakeNative.TmemCreate((ulong)work, (ulong)wbBytes, 0);
            byte[] open = new byte[80];
            cfg.CopyTo(open, 0);
            BitConverter.TryWriteBytes(open.AsSpan(56), (ulong)wbBytes);
            Msg oa = Msg.Cmif(0, open);
            oa.SendPid = true;
            oa.Copy.Add(wtm);
            oa.Copy.Add(0xFFFF8001);
            Reply oar = Call(aud, oa);
            uint ren = oar.Move.Count == 1 ? oar.Move[0] : 0;
            Check("audren OpenAudioRenderer → IAudioRenderer 子会话", "结果 0、move 1 个会话句柄", $"结果 {oar.Result}、move {oar.Move.Count} 个{(FakeNative.KindOf(ren) == ObjKind.Session ? "会话" : "非会话")}句柄");

            // 声音工作缓冲（10-20 普查中风险：按游戏给的 workBufferSize 建钉住的托管数组）：再开一个渲染器、游戏声称 256 MB 工作缓冲 →
            //   请求额外托管分配 < 1 MB（工作缓冲改为渲染器自持的原生内存、只要实际需要的大小）；关会话即释放
            {
                const long BigWork = 256L << 20;
                uint wtm2 = FakeNative.TmemCreate((ulong)work, (ulong)BigWork, 0);
                byte[] open2 = (byte[])open.Clone();
                BitConverter.TryWriteBytes(open2.AsSpan(56), (ulong)BigWork);
                Msg oa2 = Msg.Cmif(0, open2); oa2.SendPid = true; oa2.Copy.Add(wtm2); oa2.Copy.Add(0xFFFF8001);
                long w0 = GC.GetAllocatedBytesForCurrentThread();
                Reply oar2 = Call(aud, oa2);
                long wA = GC.GetAllocatedBytesForCurrentThread() - w0;
                uint ren2 = oar2.Move.Count == 1 ? oar2.Move[0] : 0;
                Check($"声音工作缓冲：游戏声称 256 MB → 开渲染器额外托管分配 < 1 MB（{wA >> 10} KB）", "结果 0、1 个会话、是", $"结果 {oar2.Result}、{oar2.Move.Count} 个会话、{(wA < (1 << 20) ? "是" : "否")}");
                if (ren2 != 0) FakeNative.HandleClose(ren2);
            }
            Check("游戏递出的复制句柄（工作缓冲）在游戏侧仍有效", "传输内存句柄仍在", FakeNative.KindOf(wtm) == ObjKind.Tmem ? "传输内存句柄仍在" : "被关掉了");

            if (ren != 0)
            {
                // 应答在同一块 TLR 里：每次调用后立即取值
                Reply sr = Call(ren, Msg.Cmif(0)); int srV = *(int*)sr.Data;
                Reply sc = Call(ren, Msg.Cmif(1)); int scV = *(int*)sc.Data;
                Reply mb = Call(ren, Msg.Cmif(2)); int mbV = *(int*)mb.Data;
                Check("IAudioRenderer GetSampleRate / GetSampleCount / GetMixBufferCount", "0/0/0、48000 / 240 / 24", $"{sr.Result}/{sc.Result}/{mb.Result}、{srV} / {scV} / {mbV}");

                MeasureAlloc("IAudioRenderer GetSampleRate（Horizon 框架）", ren, () => Msg.Cmif(0));

                // 声音渲染线程稳态（每 5 ms 一轮）：Start 后整进程 2 秒总分配折算到每轮（方案 §3-4：音频线程每轮 0 字节）
                System.Threading.Thread.Sleep(2000); // 静置：起局后的一次性分配（服务初始化收尾、JIT 分层编译）不算稳态
                AllocProfiler baseProf = Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1" ? new AllocProfiler() : null;
                // 整进程口径，连量 3 个 1.33 秒窗口取最少的一个：稳态分配（每帧 / 每圈 / 定时器）每个窗口都有，取最小也照样抓到；
                //   机器负载 60–80 时运行时自己的偶发事件（线程池补线程等）只落在个别窗口——10-21 列车与本地 2 B / 6 B 偶发红，单窗口量不出是谁
                long idleMin = long.MaxValue;
                for (int w = 0; w < 3; w++)
                {
                    long w0 = GC.GetTotalAllocatedBytes(true);
                    System.Threading.Thread.Sleep(1333);
                    idleMin = Math.Min(idleMin, GC.GetTotalAllocatedBytes(true) - w0);
                }

                if (baseProf != null)
                {
                    System.Threading.Thread.Sleep(int.Parse(Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE_MS") ?? "8000"));
                    Console.Error.WriteLine($"分配来源 空闲底噪：{baseProf.Top(12)}");
                    baseProf.Dispose();
                    DumpThreads();
                }
                _alloc.Add(("整进程空闲底噪每 5 ms（声音未启动，静置 2 秒后 3 个 1.33 秒窗口取最少）", idleMin / 266.6));

                // 声音线程按本线程计（每圈 S2AThreadAlloc.Tick 记本线程分配）：原先拿整进程 2 秒总分配折算，机器负载 60–80 时后台 JIT 分层编译 /
                //   线程池补线程的分配落进窗口（10-21 列车 11 B 偶发红，画幅线报），量的不是声音线程
                // DSP 缺省在渲染调度线程上同步执行（Ryujinx 0145，S2A_AUDIO_INLINE=0 才另起 DSP 线程）：只有一条线程时只看它
                bool audioInline = Environment.GetEnvironmentVariable("S2A_AUDIO_INLINE") != "0";
                string[] audioThreads = audioInline ? ["声音渲染调度"] : ["声音 DSP", "声音渲染调度"];
                Ryujinx.Common.S2AThreadAlloc.On = true;
                Reply st0 = Call(ren, Msg.Cmif(5));
                // 预热按圈数、不按时间：两条线程各转满 20 圈再开窗（第一圈建命令表 / 命令缓冲 / 生成器等一次性对象）。
                //   原先固定等 500 ms：负载 60–80 下调度线程 500 ms 内还没转出第一圈，一次性分配落进窗口（10-21 列车 81 B / 圈偶发红，画幅线报）
                //   圈数从 Start 返回后起算（11-05 不稳定项 #6）：原先从 On=true 起算，渲染调度线程是实时线程、Start 之前空转也计圈；负载下本测试线程在
                //   On 与 Start 返回之间被抢占超过 100 ms，回来时已满 20 圈、预热立即结束，Start 后第一圈的一次性分配（常态第 2 圈 24712 B）落进 2 秒窗口
                //   （11-02 red_1102165931：63 B / 圈 × 395 圈 ≈ 24.9 KB）
                long[] warmFrom = Array.ConvertAll(audioThreads, n => Ryujinx.Common.S2AThreadAlloc.Of(n).Rounds);
                var warm = System.Diagnostics.Stopwatch.StartNew();
                while (warm.ElapsedMilliseconds < 10000 && Array.Exists(audioThreads, n => Ryujinx.Common.S2AThreadAlloc.Of(n).Rounds - warmFrom[Array.IndexOf(audioThreads, n)] < 20))
                {
                    System.Threading.Thread.Sleep(20);
                }
                Console.WriteLine($"声音线程预热：{warm.ElapsedMilliseconds} ms，" + string.Join("，", Array.ConvertAll(audioThreads, n => $"{n} {Ryujinx.Common.S2AThreadAlloc.Of(n).Rounds} 圈 {Ryujinx.Common.S2AThreadAlloc.Of(n).Bytes} B")));
                AllocProfiler idleProf = Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1" ? new AllocProfiler() : null;
                var au0 = Array.ConvertAll(audioThreads, Ryujinx.Common.S2AThreadAlloc.Of);
                System.Threading.Thread.Sleep(2000);
                var au1 = Array.ConvertAll(audioThreads, Ryujinx.Common.S2AThreadAlloc.Of);
                Ryujinx.Common.S2AThreadAlloc.On = false;

                if (idleProf != null)
                {
                    System.Threading.Thread.Sleep(int.Parse(Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE_MS") ?? "8000")); // 放大样本
                    Console.Error.WriteLine($"分配来源 空闲 + 声音线程：{idleProf.Top(12)}");
                    idleProf.Dispose();
                }
                for (int i = 0; i < audioThreads.Length; i++)
                {
                    long dr = au1[i].Rounds - au0[i].Rounds;
                    _alloc.Add(($"声音线程「{audioThreads[i]}」每圈（本线程计，Start 后 2 秒 {dr} 圈）", dr > 0 ? (double)(au1[i].Bytes - au0[i].Bytes) / dr : 0));
                }
                // 期望按设计节拍：每 5 ms 一圈 ⇒ 2 秒约 400 圈；起码要转起来，否则上面的 0 B 是空判
                int si = audioThreads.Length - 1;
                long rSched = au1[si].Rounds - au0[si].Rounds, rDsp = audioInline ? -1 : au1[0].Rounds - au0[0].Rounds;
                Check($"声音线程在转（Start 后 2 秒：渲染调度 {rSched} 圈{(audioInline ? "，DSP 同线程" : $" / DSP {rDsp} 圈")}）", "都 > 100",
                    rSched > 100 && (audioInline || rDsp > 100) ? "都 > 100" : "不足");
                Check("IAudioRenderer Start", "0", $"{st0.Result}");

                Reply qe = Call(ren, Msg.Cmif(7));
                uint qeh = qe.Copy.Count == 1 ? qe.Copy[0] : 0;
                Check("IAudioRenderer QuerySystemEvent", "结果 0、copy 1 个原生事件读端", $"结果 {qe.Result}、copy {qe.Copy.Count} 个{(FakeNative.KindOf(qeh) == ObjKind.EventR ? "原生事件读端" : FakeNative.KindOf(qeh).ToString())}");

                // RequestUpdate（游戏每帧数次）：最小合法输入 = 各段按本渲染器的数目（24 声部、内存池 = 声部 × 4、1 个输出端、无效果 / 子混音 / 分路）填零参数
                {
                    const int rev11 = ('R' << 0) | ('E' << 8) | ('V' << 16) | ('0' << 24) + (11 << 24);
                    var bi = new Ryujinx.Audio.Renderer.Server.BehaviourInfo();
                    bi.SetUserRevision(rev11);
                    uint SizeOf<T>() where T : unmanaged => (uint)sizeof(T);
                    uint voiceSize = bi.IsBiquadFilterParameterFloatSupported() ? SizeOf<Ryujinx.Audio.Renderer.Parameter.VoiceInParameter2>() : SizeOf<Ryujinx.Audio.Renderer.Parameter.VoiceInParameter1>();
                    var h = new Ryujinx.Audio.Renderer.Common.UpdateDataHeader
                    {
                        Revision = rev11,
                        BehaviourSize = SizeOf<Ryujinx.Audio.Renderer.Common.BehaviourParameter>(),
                        MemoryPoolsSize = 24 * 4 * SizeOf<Ryujinx.Audio.Renderer.Parameter.MemoryPoolInParameter>(),
                        VoiceResourcesSize = 24 * SizeOf<Ryujinx.Audio.Renderer.Parameter.VoiceChannelResourceInParameter>(),
                        VoicesSize = 24 * voiceSize,
                        // 只更新脏混音的修订：头 + 0 个；否则 1 个（最终混音）零参数
                        MixesSize = bi.IsMixInParameterDirtyOnlyUpdateSupported() ? SizeOf<Ryujinx.Audio.Renderer.Parameter.MixInParameterDirtyOnlyUpdate>() : SizeOf<Ryujinx.Audio.Renderer.Parameter.MixParameter>(),
                        SinksSize = SizeOf<Ryujinx.Audio.Renderer.Parameter.SinkInParameter>(),
                        PerformanceBufferSize = SizeOf<Ryujinx.Audio.Renderer.Parameter.Performance.PerformanceInParameter>(),
                    };
                    h.TotalSize = SizeOf<Ryujinx.Audio.Renderer.Common.UpdateDataHeader>() + h.BehaviourSize + h.MemoryPoolsSize + h.VoiceResourcesSize + h.VoicesSize + h.MixesSize + h.SinksSize + h.PerformanceBufferSize;
                    byte* inp = FakeNative.Alloc((int)h.TotalSize);
                    new Span<byte>(inp, (int)h.TotalSize).Clear();
                    *(Ryujinx.Audio.Renderer.Common.UpdateDataHeader*)inp = h;
                    *(int*)(inp + sizeof(Ryujinx.Audio.Renderer.Common.UpdateDataHeader)) = rev11; // BehaviourParameter.UserRevision
                    byte* outp = FakeNative.Alloc(0x4000), perf = FakeNative.Alloc(0x100);
                    Msg ru = Msg.Cmif(4); ru.B.Add(((ulong)outp, 0x4000)); ru.B.Add(((ulong)perf, 0x100)); ru.A.Add(((ulong)inp, h.TotalSize));
                    Reply rur = Call(ren, ru);
                    Check("IAudioRenderer RequestUpdate（最小合法输入，24 声部）", "结果 0x0", $"结果 0x{rur.Result:X}");
                    MeasureAlloc("IAudioRenderer RequestUpdate（Horizon 框架，24 声部）", ren, () => ru);
                }

                // AudioOut 打开失败路径回收会话号（怪猎重试失败的 OpenAudioOut，上游异常路径漏回收，12 次后会话号用尽、IndexOutOfRange）
                {
                    var flaky = new FlakyDriver();
                    var mgr = new Ryujinx.Audio.Output.AudioOutputManager();
                    var evs = new Ryujinx.Audio.Integration.IWritableEvent[Ryujinx.Audio.Constants.AudioOutSessionCountMax];
                    for (int i = 0; i < evs.Length; i++) evs[i] = new NullEvent();
                    mgr.Initialize(flaky, evs);
                    var acfg = new Ryujinx.Audio.Common.AudioInputConfiguration { SampleRate = 48000, ChannelCount = 2 };
                    int thrown = 0;
                    string other = "";
                    flaky.Fail = true;
                    for (int i = 0; i < 20; i++)
                    {
                        try { mgr.OpenAudioOut(out _, out _, out _, Dispatcher.Instance.Memory, "", Ryujinx.Audio.Common.SampleFormat.PcmInt16, ref acfg); }
                        catch (InvalidOperationException) { thrown++; }
                        catch (Exception e) { other = e.GetType().Name; break; }
                    }
                    flaky.Fail = false;
                    string after;
                    try
                    {
                        after = mgr.OpenAudioOut(out _, out _, out var aobj, Dispatcher.Instance.Memory, "", Ryujinx.Audio.Common.SampleFormat.PcmInt16, ref acfg).ToString();
                        aobj?.Dispose();
                    }
                    catch (Exception e) { after = e.GetType().Name; }
                    Check("AudioOut 打开抛异常 20 次后仍能打开（会话号回收）", "抛 20 次、之后 Success", $"抛 {thrown} 次{(other != "" ? $"、另抛 {other}" : "")}、之后 {after}");
                }

                // AudioOut 取回已播完缓冲（#8 GetReleasedAudioOutBuffersAuto，游戏给 C 缓冲）：Horizon 服务把标签写进服务端指针缓冲，
                // 直调要按游戏的接收列表拷回（巫师3 回了计数却读不到标签，不再追加声音缓冲）。接收列表类型 3（按序号）、2（一块收全部）各测一次
                {
                    uint aout = Call(sm, Msg.Cmif(1, Name8("audout:u"))).Move[0];
                    byte* nameIn = _game + 0x6800, nameOut = _game + 0x6900, ub = _game + 0x6A00, pcm6 = _game + 0x6C00, cbuf = _game + 0x6E00;
                    new Span<byte>(nameIn, 0x100).Clear();
                    Msg oo = Msg.Cmif(1, Bytes(48000u, 0xCAFE0002u, 0UL));
                    oo.SendPid = true;
                    oo.Copy.Add(0xFFFF8001);
                    oo.A.Add(((ulong)nameIn, 0x100));
                    oo.B.Add(((ulong)nameOut, 0x100));
                    Reply oor = Call(aout, oo);
                    uint ses = oor.Move.Count == 1 ? oor.Move[0] : 0;
                    string results = "";
                    Call(ses, Msg.Cmif(1)); // Start（先开再追加：哑设备排队即播完并触发更新）
                    for (int mode = 3; mode >= 2; mode--)
                    {
                        ulong tag = 0x1234500 + (ulong)mode;
                        // AudioUserBuffer { 下一块, 数据, 容量, 数据长, 数据偏移 }
                        *(ulong*)ub = 0; *(ulong*)(ub + 8) = (ulong)pcm6; *(ulong*)(ub + 16) = 0x100; *(ulong*)(ub + 24) = 0x100; *(ulong*)(ub + 32) = 0;
                        Msg ap = Msg.Cmif(3, Bytes(tag));
                        ap.A.Add(((ulong)ub, 0x28));
                        Call(ses, ap);
                        uint count = 0;
                        // 释放靠声音管理器线程被唤醒后处理：机器重负载时会晚，等到墙钟 5 秒（原先 1 秒，负载 60–147 时门禁偶发读到 0）
                        var waitSw = System.Diagnostics.Stopwatch.StartNew();
                        while (count == 0 && waitSw.ElapsedMilliseconds < 5000)
                        {
                            *(ulong*)cbuf = 0;
                            Msg gr = Msg.Cmif(8, Bytes(0u, 0x40u)); // 原始数据偏移 4：指针输出长度（u16，同游戏发的 08000000）
                            gr.B.Add((0, 0));   // AutoSelect：B 地址 0 = 改用指针缓冲（游戏同样发 B 0 + C）
                            gr.C.Add(((ulong)cbuf, 0x40));
                            gr.RecvListMode = mode;
                            count = *(uint*)Call(ses, gr).Data;
                            if (count == 0) System.Threading.Thread.Sleep(10);
                        }
                        results += $"类型 {mode}：计数 {count}、C 缓冲 {(*(ulong*)cbuf == tag ? "是该标签" : $"0x{*(ulong*)cbuf:X}")}；";
                    }
                    Check("AudioOut 取回已播完缓冲经 C 缓冲回标签（接收列表类型 3 / 2）", "类型 3：计数 1、C 缓冲 是该标签；类型 2：计数 1、C 缓冲 是该标签；", results);
                }

                // bsd Select 等到超时 / 被唤醒（暗黑2：上游把 timeval 前的填充当超时、eventfd 的 Select 是 TODO 返回 EOPNOTSUPP，
                // 游戏 select→read EAGAIN 空转，200 秒 154 万次）。期望按 Horizon 语义：无写入等满超时；阻塞中别的线程写入即返回；
                // bsd 会话不串行（串行时阻塞的 select 挡住唤醒它的 Write）
                {
                    uint bsd = Call(sm, Msg.Cmif(1, Name8("bsd:u"))).Move[0];
                    Msg reg = Msg.Cmif(0, new byte[0x30]);   // BsdBufferConfig 0x20 + pid + 传输内存长度
                    reg.SendPid = true;
                    reg.Copy.Add(0xFFFF8001);
                    Call(bsd, reg);
                    int efd = *(int*)Call(bsd, Msg.Cmif(31, Bytes(0u, 0u, 0UL))).Data;   // EventFd(flags 0, 初值 0)
                    byte* masks = _game + 0x7000, wbuf = _game + 0x7800;

                    (int Ret, long Ms, bool InSet) Select(long sec, long usec, bool isNull)
                    {
                        new Span<byte>(masks, 0x300).Clear();
                        masks[efd / 8] = (byte)(1 << (efd % 8));
                        Msg m = Msg.Cmif(5, Bytes(efd + 1, 0u, sec, usec, isNull ? 1UL : 0UL));
                        for (int k = 0; k < 3; k++) m.A.Add(((ulong)(masks + k * 0x80), 0x80));
                        for (int k = 0; k < 3; k++) m.B.Add(((ulong)(masks + 0x180 + k * 0x80), 0x80));
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        int ret = *(int*)Call(bsd, m).Data;
                        return (ret, sw.ElapsedMilliseconds, (masks[0x180 + efd / 8] & (1 << (efd % 8))) != 0);
                    }

                    var a = Select(0, 50000, false);
                    // 另一条线程（自己的 TLS）30 ms 后往 eventfd 写 1
                    var writer = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread.Sleep(30);
                        *(ulong*)wbuf = 1;
                        Msg w = Msg.Cmif(24, Bytes(efd));
                        w.A.Add(((ulong)wbuf, 8));
                        CallTlr(_tlr2, bsd, w);
                    });
                    writer.Start();
                    var b = Select(2, 0, false);
                    bool joined = writer.Join(3000);
                    var c = Select(0, 0, true);
                    Check("bsd Select：无写入等满 50 ms / 阻塞中别的线程写入即返回 / 已可读 + 不限时立即返回",
                        "返回 0、≥45 ms；返回 1、<1000 ms、读集合含该 fd、写线程结束；返回 1、<100 ms",
                        $"返回 {a.Ret}、{(a.Ms >= 45 ? "≥45" : a.Ms.ToString())} ms；返回 {b.Ret}、{(b.Ms < 1000 ? "<1000" : b.Ms.ToString())} ms、读集合{(b.InSet ? "含" : "不含")}该 fd、写线程{(joined ? "结束" : "未结束")}；返回 {c.Ret}、{(c.Ms < 100 ? "<100" : c.Ms.ToString())} ms");

                    // bsd Poll 同样要能等（最终幻想 共鸣：起局 Poll 一个 eventfd、超时 100 ms，等待段调 Ryujinx 内核的 SleepThread /
                    // context.Thread.HandlePostSyscall，这里服务直接在游戏线程上跑、没有 KThread，空引用 → 游戏 Break）。
                    // 先把上面写进去的 1 读掉，eventfd 回到不可读
                    {
                        Msg rd = Msg.Cmif(25, Bytes(efd));
                        rd.B.Add(((ulong)wbuf, 8));
                        Call(bsd, rd);
                    }
                    byte* pin = _game + 0x7900, pout = _game + 0x7980;
                    (int Ret, long Ms, int Revents) Poll(int nfds, int timeoutMs)
                    {
                        *(int*)pin = efd; *(ushort*)(pin + 4) = 1; *(ushort*)(pin + 6) = 0;   // PollEventData：fd、events = POLLIN、revents
                        new Span<byte>(pout, 8).Clear();
                        Msg m = Msg.Cmif(6, Bytes(nfds, timeoutMs));
                        m.A.Add(((ulong)pin, (ulong)(nfds * 8)));
                        m.B.Add(((ulong)pout, (ulong)(nfds * 8)));
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        int ret = *(int*)Call(bsd, m).Data;
                        return (ret, sw.ElapsedMilliseconds, *(ushort*)(pout + 6));
                    }

                    var pa = Poll(1, 50);
                    var pw = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread.Sleep(30);
                        *(ulong*)(wbuf + 0x40) = 1;
                        Msg w = Msg.Cmif(24, Bytes(efd));
                        w.A.Add(((ulong)(wbuf + 0x40), 8));
                        CallTlr(_tlr2, bsd, w);
                    });
                    pw.Start();
                    var pb = Poll(1, 2000);
                    bool pjoined = pw.Join(3000);
                    var pc = Poll(1, -1);
                    var pd = Poll(0, 30);
                    Check("bsd Poll：无写入等满 50 ms / 阻塞中别的线程写入即返回 / 已可读 + 不限时立即返回 / 无描述符按超时睡",
                        "返回 0、≥45 ms；返回 1、<1000 ms、revents 含 POLLIN、写线程结束；返回 1、<100 ms；返回 0、≥25 ms",
                        $"返回 {pa.Ret}、{(pa.Ms >= 45 ? "≥45" : pa.Ms.ToString())} ms；返回 {pb.Ret}、{(pb.Ms < 1000 ? "<1000" : pb.Ms.ToString())} ms、revents {((pb.Revents & 1) != 0 ? "含" : "不含")} POLLIN、写线程{(pjoined ? "结束" : "未结束")}；返回 {pc.Ret}、{(pc.Ms < 100 ? "<100" : pc.Ms.ToString())} ms；返回 {pd.Ret}、{(pd.Ms >= 25 ? "≥25" : pd.Ms.ToString())} ms");

                    // 审计 #656 P1：revents 是纯输出（FreeBSD poll）——游戏在输入项里留的旧 revents 不许原样带回来（eventfd 轮询器只在就绪时才写 revents）。
                    //   先把 eventfd 读空回到不可读，输入项 revents 故意填 0x1F，超时 10 ms：期望返回 0、输出 revents 0
                    {
                        Msg rd = Msg.Cmif(25, Bytes(efd));
                        rd.B.Add(((ulong)wbuf, 8));
                        Call(bsd, rd);
                        *(int*)pin = efd; *(ushort*)(pin + 4) = 1; *(ushort*)(pin + 6) = 0x1F;
                        new Span<byte>(pout, 8).Fill(0xAA);
                        Msg m = Msg.Cmif(6, Bytes(1, 10));
                        m.A.Add(((ulong)pin, 8UL));
                        m.B.Add(((ulong)pout, 8UL));
                        int ret = *(int*)Call(bsd, m).Data;
                        Check("bsd Poll：超时返回时不带回游戏输入项里的旧 revents（审计 #656 P1）", "返回 0、revents 0x0",
                            $"返回 {ret}、revents 0x{*(ushort*)(pout + 6):X}");
                    }
                    // 审计 #656 P2：等待毫秒数——过了期限（哪怕晚 2 ms 以上）等 0、不是永久；只有不限时才 -1
                    {
                        long f = 1_000_000;   // 每毫秒 1000 个计时单位
                        Check("bsd Poll 等待毫秒：不限时 / 已过期 2.5 ms / 恰到期 / 还剩 10 ms（审计 #656 P2）", "-1、0、0、11",
                            $"{Ryujinx.HLE.HOS.Services.Sockets.Bsd.IClient.S2APollWaitMs(long.MaxValue, 0, f)}、{Ryujinx.HLE.HOS.Services.Sockets.Bsd.IClient.S2APollWaitMs(1000, 3500, f)}、{Ryujinx.HLE.HOS.Services.Sockets.Bsd.IClient.S2APollWaitMs(1000, 1000, f)}、{Ryujinx.HLE.HOS.Services.Sockets.Bsd.IClient.S2APollWaitMs(11000, 1000, f)}");
                    }
                }

                // 离线机不碰宿主网络（bsd / sfdnsres / 服务串行域，见 OfflineNet.cs）
                OfflineNetwork(sm);

                // 兼容层 + 声音峰值包装：硬件只支持立体声时，6 声道会话要由兼容层降混，它要求真实会话是 HardwareDeviceSessionOutputBase
                // （暗黑2 起局 FailFast「Real driver session class type isn't based on …」）
                {
                    var compat = new Ryujinx.Audio.Backends.CompatLayer.CompatLayerHardwareDeviceDriver(new AudioPeakDriver(new StereoOnlyDriver()));
                    string opened;
                    try
                    {
                        using var s6 = compat.OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction.Output, null, Ryujinx.Audio.Common.SampleFormat.PcmInt16, 48000, 6);
                        opened = s6.GetType().Name;
                    }
                    catch (Exception e)
                    {
                        opened = $"{e.GetType().Name}：{e.Message}";
                    }
                    Check("兼容层降混 6 → 2 声道，经声音峰值包装打开输出会话", "CompatLayerHardwareDeviceSession", opened);
                }

                // 哑设备的声道数（修干净清单 #10）：S2A_AUDIO_OFF=1 的哑设备原先报支持 6 声道，渲染器按 5.1 出声（虚拟设备报 6、输出端每 5 ms 交 240 × 6 个样本），
                //   「声音全关」实验反而比产品的立体声配置混得多。期望 = 产品最便宜的真设备配置：苹果后端只报 2 声道（S2A_AUDIO_CH2=1）——
                //   声道数 1 / 2 / 6 的支持与它逐项相同，虚拟设备与渲染器输出端都按 2 声道
                {
                    var dummy = new DummyHardwareDeviceDriver();
                    string sup = $"{dummy.SupportsChannelCount(1)}/{dummy.SupportsChannelCount(2)}/{dummy.SupportsChannelCount(6)}";
                    var compatDummy = new Ryujinx.Audio.Backends.CompatLayer.CompatLayerHardwareDeviceDriver(new DummyHardwareDeviceDriver());
                    uint virtualCh = new Ryujinx.Audio.Renderer.Device.VirtualDeviceSessionRegistry(compatDummy).ActiveDevice.ChannelCount;
                    // 渲染器输出端声道数（AudioProcessor.Start 给每个输出端用的值；单独起处理器线程再停会和渲染器管理线程的信箱交接冲突，直接取它的选择函数）
                    uint outCh = (uint)typeof(Ryujinx.Audio.Renderer.Dsp.AudioProcessor).GetMethod("GetHardwareChannelCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, [compatDummy]);
                    Check("哑设备声道数：支持 1 / 2 / 6、虚拟设备、渲染器输出端", "True/True/False、2、2", $"{sup}、{virtualCh}、{outCh}");
                    if (Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver.IsSupported)
                    {
                        Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", "1");
                        using var apple2 = new Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver();
                        Environment.SetEnvironmentVariable("S2A_AUDIO_CH2", null);
                        Check("哑设备声道支持（1 / 2 / 6）与苹果后端只报 2 声道时相同", $"{apple2.SupportsChannelCount(1)}/{apple2.SupportsChannelCount(2)}/{apple2.SupportsChannelCount(6)}", sup);
                    }
                }

                // 游戏直接用 AudioOut（暗黑3：5.1 声道、每块 1024 帧、每秒约 47 块）：硬件只有立体声时经兼容层降混。
                //   正确性：前左 3i、前右 −5i、中置 1000、其余 0 → 降混左 3i + 707、右 707 − 5i（上游系数 Q16：前 65536、中置 46333，+32768 取整右移 16；
                //     46333·1000 + 32768 = 46365768，÷65536 = 707.49 → 707），送设备的数据长 1024 帧 × 2 声道 × 2 字节；立体声直通原样送出；
                //   稳态：追加 + 播完释放 + 取回已播完一圈 0 字节托管分配（样本复制 / 降混数组已由 b01ad26 去掉；本项再去掉每块的 AudioBuffer 与 FlushToHardware 临时数组）
                foreach (uint ch in new uint[] { 6, 2 })
                {
                    var cap = new CaptureStereoDriver();
                    var mgr = new Ryujinx.Audio.Output.AudioOutputManager();
                    var evs = new Ryujinx.Audio.Integration.IWritableEvent[Ryujinx.Audio.Constants.AudioOutSessionCountMax];
                    for (int i = 0; i < evs.Length; i++) evs[i] = new NullEvent();
                    mgr.Initialize(new Ryujinx.Audio.Backends.CompatLayer.CompatLayerHardwareDeviceDriver(cap), evs);
                    var acfg = new Ryujinx.Audio.Common.AudioInputConfiguration { SampleRate = 48000, ChannelCount = (ushort)ch };
                    mgr.OpenAudioOut(out _, out _, out var ao, Dispatcher.Instance.Memory, "", Ryujinx.Audio.Common.SampleFormat.PcmInt16, ref acfg);
                    const int Frames = 1024;
                    short* upcm = (short*)FakeNative.Alloc(Frames * 6 * 2);
                    for (int i = 0; i < Frames; i++)
                    {
                        short* f = upcm + i * ch;
                        if (ch == 6) { f[0] = (short)(3 * i); f[1] = (short)(-5 * i); f[2] = 1000; f[3] = 0; f[4] = 0; f[5] = 0; }
                        else { f[0] = (short)(3 * i); f[1] = (short)(-5 * i); }
                    }
                    var ub = new Ryujinx.Audio.Common.AudioUserBuffer { Data = (ulong)upcm, Capacity = Frames * ch * 2, DataSize = Frames * ch * 2 };
                    ulong* tags = stackalloc ulong[4];
                    ao.Start();
                    ulong tag = 1;
                    long Cycle()
                    {
                        long a0 = GC.GetAllocatedBytesForCurrentThread();
                        ao.AppendBuffer(tag++, ref ub);
                        mgr.Update();
                        ao.GetReleasedBuffer(new Span<ulong>(tags, 4), out _);
                        return GC.GetAllocatedBytesForCurrentThread() - a0;
                    }
                    Cycle();
                    int same = 0;
                    for (int i = 0; i < Frames; i++)
                    {
                        int wl = ch == 6 ? 3 * i + 707 : 3 * i, wr = ch == 6 ? 707 - 5 * i : -5 * i;
                        same += cap.Last[2 * i] == wl && cap.Last[2 * i + 1] == wr ? 1 : 0;
                    }
                    Check($"AudioOut {ch} 声道 → 立体声设备：送出的 1024 帧", $"数据长 4096、1024 帧对、已释放标签 1",
                        $"数据长 {cap.LastSize}、{same} 帧对、已释放标签 {tags[0]}");
                    for (int i = 0; i < 100; i++) Cycle();
                    long total = 0;
                    for (int i = 0; i < 1000; i++) total += Cycle();
                    _alloc.Add(($"AudioOut {ch} 声道追加 + 释放 + 取回一圈（立体声设备{(ch == 6 ? "，兼容层降混" : "，直通")}）", total / 1000.0));
                    ao.Dispose();
                }

                // 苹果声音输出（产品后端）：渲染线程每轮 AppendBuffer → 会话 QueueBuffer，CoreAudio 回调线程按播放进度出队；整进程 300 轮折算。
                // 外部依赖防线（10-21 维护者定）：真 CoreAudio 是外部服务，整段放后台线程、墙钟 30 秒；超时报红并跳过（不挂死整个单测进程），
                //   打不开设备时跳过并记一行（不判红：门禁机没有可用的输出设备不是产品缺陷；长时间行为在真机 / 诊断局测）
                if (Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver.IsSupported)
                {
                    string openErr = null;
                    double appleBest = double.NaN;   // 后台线程只写这个，主线程确认跑完后再登记（超时后晚到的线程不碰共享列表）
                    string appleSplit = "";
                    var appleTh = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            using var drv = new Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver();
                            using var appleDev = new Ryujinx.Audio.Integration.HardwareDeviceImpl(drv, 2, 48000);
                            short[] silence = new short[240 * 2]; // 静音
                            void Rounds(int n)
                            {
                                for (int i = 0; i < n; i++)
                                {
                                    appleDev.AppendBuffer(silence, 2);
                                    System.Threading.Thread.Sleep(5);
                                }
                            }
                            // 预热：先一口气排 100 块（500 ms 积压）把会话的环形缓冲撑到位（它只增不减，产品里涨到最大积压后不再分配；
                            // 不撑的话测量期回调线程偶尔晚到、积压创新高就扩容一次，读数在 0 / 8 / 15 B 间跳），再按节拍跑 600 轮
                            for (int i = 0; i < 100; i++)
                            {
                                appleDev.AppendBuffer(silence, 2);
                            }
                            Rounds(600);
                            // 只计被测的两条线程：本线程（AppendBuffer → QueueBuffer）+ CoreAudio 回调线程（出队，每轮 S2AThreadAlloc.Tick("CoreAudio 回调")）。
                            //   原先整进程计，负载高时别的线程（JIT 分层编译 / 线程池补线程）的分配落进窗口（10-21 PR #5 首次入列车「0 → 1 B」偶发退回，原样重入就过）；
                            //   连量 3 个 600 轮窗口取最小；读数里两边分开写，红了一眼看出是哪条线程在分配
                            Ryujinx.Common.S2AThreadAlloc.On = true;
                            Rounds(50);   // 回调线程开了计账后先转几圈，让它的槽位起点落定（第一次 Tick 只记起点）
                            double best = double.MaxValue;
                            for (int w = 0; w < 3; w++)
                            {
                                var c0 = Ryujinx.Common.S2AThreadAlloc.Of("CoreAudio 回调");
                                long a0 = GC.GetAllocatedBytesForCurrentThread();
                                Rounds(600);
                                long a1 = GC.GetAllocatedBytesForCurrentThread();
                                var c1 = Ryujinx.Common.S2AThreadAlloc.Of("CoreAudio 回调");
                                double v = (a1 - a0 + c1.Bytes - c0.Bytes) / 600.0;
                                if (v < best) { best = v; appleSplit = $"本线程 {(a1 - a0) / 600.0:0.##} B + 回调线程 {(c1.Bytes - c0.Bytes) / 600.0:0.##} B（回调 {c1.Rounds - c0.Rounds} 圈）"; }
                            }
                            Ryujinx.Common.S2AThreadAlloc.On = false;
                            appleBest = best;
                        }
                        catch (Exception e)
                        {
                            openErr = $"{e.GetType().Name} {e.Message}";
                        }
                    }) { IsBackground = true, Name = "单测·苹果声音输出" };
                    appleTh.Start();

                    if (!appleTh.Join(30000))
                    {
                        Check("苹果声音输出用例 30 秒内跑完（真 CoreAudio，外部依赖）", "跑完", "超时（跳过；CoreAudio 回调 / 收尾等不到，见线程栈）");
                    }
                    else if (openErr != null)
                    {
                        Console.Error.WriteLine($"苹果声音输出：打不开输出设备，跳过（{openErr}）");
                    }
                    else
                    {
                        _alloc.Add(($"苹果声音输出每轮（AppendBuffer 线程 + CoreAudio 回调线程，本线程计，3 个 600 轮窗口取最小：{appleSplit}）", appleBest));
                    }
                }
            }

            AudioOutputBoundedAlloc();
            AudioOutputSamples();
            AudioDevicePump();

            red0 = Red.Total;
            Reply au = Call(aud, Msg.Cmif(77));
            Reply wb2 = Call(aud, Msg.Cmif(1, cfg));
            Check("audren 未实现命令 77，之后会话仍可用", $"结果 0x{Red.ResultUnknownCommand:X}、报红 +1、再调 GetWorkBufferSize 结果 0", $"结果 0x{au.Result:X}、报红 +{Red.Total - red0}、再调 GetWorkBufferSize 结果 {wb2.Result}");

            // 复制句柄语义（动森 iPhone 局：关 IAudioDevice 会话时服务析构 CloseHandle 回 InvalidHandle，上游 AbortOnFailure 终止进程）：
            // 游戏拿到的是新句柄，先关它不影响服务手里那份；之后关会话，服务析构关自己的事件句柄须成功
            red0 = Red.Total;
            Reply gd = Call(aud, Msg.Cmif(2, new byte[8]));
            uint dev = gd.Move.Count == 1 ? gd.Move[0] : 0;
            Reply de = Call(dev, Msg.Cmif(4));
            uint deh = de.Copy.Count == 1 ? de.Copy[0] : 0;
            ObjKind dehKind = FakeNative.KindOf(deh);
            int closeDe = FakeNative.HandleClose(deh);
            int closeDev = FakeNative.HandleClose(dev);
            Check("IAudioDevice QueryAudioDeviceSystemEvent 后游戏先关事件再关会话", "结果 0、copy 1 个事件读端、关事件成功、关会话成功、进程在、报红 +0",
                $"结果 {de.Result}、copy {de.Copy.Count} 个{(dehKind == ObjKind.EventR ? "事件读端" : dehKind.ToString())}、关事件{(closeDe == 1 ? "成功" : "失败")}、关会话{(closeDev == 1 ? "成功" : "失败")}、进程在、报红 +{Red.Total - red0}");

                // H05 普查（#150）：先前的零分配用例只覆盖了 nvdrv / hid 两条 / fs / aoc / vi / audren 的一部分。
                //   这里补「游戏每帧 / 每局都会调到」的取句柄类命令：它们每次都 new 一个服务子会话，
                //   走 IpcService 构造里的反射解析与域对象表创建，这批分配正是 JIT 下量不准、必须在 NativeAOT 发布版上量的那类。
                //   量法同上面的 MeasureAlloc：预热 100 次、量 1000 次取平均；子会话取完即关，和游戏一致。
                //   放在 RomFS 分支外面：这些命令不吃游戏数据，没有动森数据也能量（普查表要能在任意 Mac 上复现）。
                {
                    int census0 = _alloc.Count;
                    // time:u：三个时钟的取句柄命令。游戏每帧读一次时间，每次 new 一个 ISystemClock
                    Reply gtc = Call(sm, Msg.Cmif(1, Name8("time:u")));
                    uint timec = gtc.Move.Count == 1 ? gtc.Move[0] : 0;

                    if (timec != 0)
                    {
                        MeasureAllocCloseCopy("time:u GetStandardUserSystemClock（#0，取完即关）", timec, Msg.Cmif(0));
                        MeasureAllocCloseCopy("time:u GetSteadyClock（#2，取完即关）", timec, Msg.Cmif(2));
                        MeasureAllocCloseCopy("time:u GetLocalSystemClock（#3，取完即关）", timec, Msg.Cmif(3));
                        // #1 / #4 / #5：网络对时相关的三个时钟，同样是「取句柄 + 用完关」
                        MeasureAllocCloseCopy("time:u GetStandardNetworkSystemClock（#1，取完即关）", timec, Msg.Cmif(1));
                        MeasureAllocCloseCopy("time:u GetStandardEphemeralNetworkSystemClock（#4，取完即关）", timec, Msg.Cmif(4));
                        MeasureAllocCloseCopy("time:u GetEphemeralNetworkSystemClock（#5，取完即关）", timec, Msg.Cmif(5));
                    }

                    // audout:u：每帧取已播完的声音缓冲
                    Reply gao = Call(sm, Msg.Cmif(1, Name8("audout:u")));
                    uint aoo = gao.Move.Count == 1 ? gao.Move[0] : 0;

                    if (aoo != 0)
                    {
                        MeasureAlloc("audout:u GetReleasedAudioOutBuffer（#4，每帧）", aoo, () => Msg.Cmif(4));
                    }

                    _census.AddRange(_alloc.GetRange(census0, _alloc.Count - census0));
                    _alloc.RemoveRange(census0, _alloc.Count - census0);
                }

            // ---- 游戏登记 + fs 读 RomFS（本机游戏数据，不进仓库；没有就记「未测」）----
            string game = Environment.GetEnvironmentVariable("S2A_TEST_GAME") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".s2arm/data/acnh");

            if (Directory.Exists(Path.Combine(game, "romfs")))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // 旧翻译器版本的着色器磁盘缓存（夹具：动森实局写出的 9370 支，头里版本 7354 ≠ 当前）：登记前放进数据根
                string staleFixture = Path.Combine(Environment.GetEnvironmentVariable("S2A_RUN_ROOT") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".s2arm/run"), "s2arm/fixtures/acnh_shader_cache_7354");
                string shaderCacheDir = Path.Combine(Ryujinx.Common.Configuration.AppDataManager.GamesDirPath, "01006f8002326000", "cache", "shader");
                bool haveStale = Directory.Exists(staleFixture);
                long staleCount = 0;

                if (haveStale)
                {
                    Directory.CreateDirectory(shaderCacheDir);

                    foreach (string f in Directory.GetFiles(staleFixture, "*.*"))
                    {
                        if (!f.EndsWith(".txt"))
                        {
                            File.Copy(f, Path.Combine(shaderCacheDir, Path.GetFileName(f)), overwrite: true);
                        }
                    }

                    staleCount = (new FileInfo(Path.Combine(shaderCacheDir, "shared.toc")).Length - 32) / 8;   // 程序目录：32 字节头 + 每支一个 8 字节偏移
                }

                Application.Register(Host.Device, game);

                // 开局初始化着色器缓存（渲染循环起来时调的同一入口）：旧版本缓存不许让游戏等整批重编——整份作废、报红计数、立即返回
                if (haveStale)
                {
                    int discarded0 = Ryujinx.Graphics.Gpu.Shader.DiskCache.S2AShaderCacheGuard.Discarded;
                    var swc = System.Diagnostics.Stopwatch.StartNew();
                    Host.Device.Gpu.SetGpuThread();   // 同渲染循环的顺序：先认 GPU 线程（取渲染器能力），再读着色器磁盘缓存
                    Host.Device.Gpu.InitializeShaderCache(System.Threading.CancellationToken.None);
                    long initMs = swc.ElapsedMilliseconds;
                    int discarded = Ryujinx.Graphics.Gpu.Shader.DiskCache.S2AShaderCacheGuard.Discarded - discarded0;
                    // 上游清缓存 = 文件截成 0 字节
                    static bool Empty(string f) => !File.Exists(f) || new FileInfo(f).Length == 0;
                    bool cleared = Empty(Path.Combine(shaderCacheDir, "shared.toc")) && Empty(Path.Combine(shaderCacheDir, "guest.toc")) && Empty(Path.Combine(shaderCacheDir, "shared.data")) && Empty(Path.Combine(shaderCacheDir, "guest.data"));
                    Check("旧版本着色器缓存：开局不重编", $"5 秒内返回、作废 {staleCount} 支、旧缓存已清空",
                        $"{(initMs < 5000 ? "5 秒内返回" : $"用时 {initMs} ms")}、作废 {discarded} 支、旧缓存{(cleared ? "已清空" : "还在")}");
                }
                else
                {
                    Check("旧版本着色器缓存：开局不重编", "已测", $"未测（没有夹具 {staleFixture}）");
                }
                long regMs = sw.ElapsedMilliseconds;
                Check("动森登记（npdm 程序号）", "0x1006F8002326000", $"0x{Application.ProgramId:X}");
                // RomFS 预建索引：与按目录现建的镜像（LibHac 原路径）逐字节对拍——长度、头与各表区、随机 300 处 4 KB（含文件数据）
                {
                    string romDir = Path.Combine(game, "romfs");
                    string idxPath = Path.Combine(Path.GetTempPath(), $"s2a_romfs_{Environment.ProcessId}.s2aidx");
                    var swi = System.Diagnostics.Stopwatch.StartNew();
                    RomFsIndex.Write(romDir, idxPath);
                    long writeMs = swi.ElapsedMilliseconds;
                    swi.Restart();
                    IStorage byDir = Application.RomFsFromDirectory(romDir);
                    long dirMs = swi.ElapsedMilliseconds;
                    swi.Restart();
                    IStorage byIdx = RomFsIndex.Open(romDir, idxPath);
                    long idxMs = swi.ElapsedMilliseconds;
                    byDir.GetSize(out long szDir);
                    byIdx.GetSize(out long szIdx);
                    // RomFS 头：10 个 i64——头长、目录哈希表 偏移/长、目录元数据表 偏移/长、文件哈希表 偏移/长、文件元数据表 偏移/长、数据区偏移
                    byte[] hb = new byte[0x50];
                    byDir.Read(0, hb).ThrowIfFailure();
                    long H(int i) => BitConverter.ToInt64(hb, i * 8);
                    long metaFrom = Math.Min(Math.Min(H(1), H(3)), Math.Min(H(5), H(7)));
                    long metaTo = Math.Max(Math.Max(H(1) + H(2), H(3) + H(4)), Math.Max(H(5) + H(6), H(7) + H(8)));
                    long dataOffset = H(9);
                    bool Same(long off, int len)
                    {
                        byte[] x = new byte[len], y = new byte[len];
                        byDir.Read(off, x).ThrowIfFailure();
                        byIdx.Read(off, y).ThrowIfFailure();
                        return x.AsSpan().SequenceEqual(y);
                    }
                    bool head = Same(0, (int)dataOffset);
                    bool meta = true;
                    for (long o = metaFrom; o < metaTo && meta; o += 1 << 20) meta = Same(o, (int)Math.Min(1 << 20, metaTo - o));
                    Random rnd = new(11);
                    int diff = 0;
                    for (int k = 0; k < 300; k++)
                    {
                        long off = (long)(rnd.NextDouble() * (szDir - 4096));
                        if (!Same(off, 4096)) diff++;
                    }
                    long idxBytes = new FileInfo(idxPath).Length;
                    File.Delete(idxPath);
                    Console.Error.WriteLine($"RomFS 索引：生成 {writeMs} ms、{idxBytes} 字节；起局建镜像 按目录 {dirMs} ms / 按索引 {idxMs} ms");
                    Check("RomFS 预建索引与按目录现建逐字节对拍", "长度相同、头相同、表区相同、随机 300 处 4 KB 不同 0",
                        $"长度{(szDir == szIdx ? "相同" : $" {szDir}≠{szIdx}")}、头{(head ? "相同" : "不同")}、表区{(meta ? "相同" : "不同")}、随机 300 处 4 KB 不同 {diff}");
                }

                // 显存缓冲每帧映射 / 解除映射（动森岛上每帧约 5 对；Mac 局拆账 MapBufferEx 1039 B/次、UnmapBuffer 657 B/次）：
                // nvmap 建 64 KB 缓冲挂在一块游戏内存上，nvhost-as MapBufferEx（不给固定偏移，由地址空间分配）→ UnmapBuffer 成对
                {
                    int OpenDev(string dev)
                    {
                        byte[] p = Encoding.ASCII.GetBytes(dev);
                        p.CopyTo(new Span<byte>(path, p.Length));
                        Msg o = Msg.Cmif(0);
                        o.A.Add(((ulong)path, (ulong)p.Length));
                        return *(int*)Call(nv, o).Data;
                    }

                    Reply Ioctl(int dfd, uint cmd, byte* args, int len)
                    {
                        Msg m = Msg.Cmif(1, Bytes(dfd, cmd));
                        m.A.Add(((ulong)args, (ulong)len));
                        m.B.Add(((ulong)args, (ulong)len));
                        return Call(nv, m);
                    }

                    int mapFd = OpenDev("/dev/nvmap");
                    int asFd = OpenDev("/dev/nvhost-as-gpu");
                    // Ioctl3 GetVaRegions（10-26 普查脑锻炼：sdk NvRmGpuAddressSpaceGetInfo 从 Ioctl3 的内联输出缓冲读两个显存地址区，上游只写回参数结构、
                    //   内联缓冲原样留 0，游戏建显存分配器失败、在自己的回收路径里崩）。期望照 nvgpu 两区手写：小页区 [4 GB, 16 GB) 页 4 KB、
                    //   大页区 [16 GB, 128 GB) 页 64 KB → 页数 0x300000 / 0x1C0000；内联缓冲先填 0xEE，写回后两区逐字段对上
                    {
                        byte* va = _game + 0x2E00, inl = _game + 0x2E40;
                        new Span<byte>(va, 0x40).Clear(); *(uint*)(va + 8) = 0x30;
                        new Span<byte>(inl, 0x30).Fill(0xEE);
                        Msg m3 = Msg.Cmif(12, Bytes(asFd, 0xC0404108u));
                        m3.A.Add(((ulong)va, 0x40)); m3.B.Add(((ulong)va, 0x40)); m3.B.Add(((ulong)inl, 0x30));
                        Reply r3 = Call(nv, m3);
                        string Reg(byte* r) => $"{*(ulong*)r:X}/{*(uint*)(r + 8):X}/{*(ulong*)(r + 16):X}";
                        Check("nvhost-as Ioctl3 GetVaRegions：两区写回内联输出缓冲", "NvResult 0、100000000/1000/300000、400000000/10000/1C0000",
                            $"NvResult {*(uint*)r3.Data}、{Reg(inl)}、{Reg(inl + 0x18)}");
                    }
                    // 开多媒体通道不建 GPU 通道（帧率能耗线 10-24）：nvhost-vic / nvhost-nvdec 走 Host1x 提交、从不用 GPU 通道，原先构造里照建一个整套 3D / 计算 / DMA 状态的 GpuChannel，
                    //   真机马车8 每开一次托管分配 19.3 MB、全进 gen2（开 4 次 77 MB），老代变大、读盘段 gen1 / gen2 停顿跟着变长。手算期望：开 vic 的整进程托管分配 < 1 MB（原先约 19 MB）
                    {
                        long v0 = GC.GetTotalAllocatedBytes(true);
                        int vicFd = OpenDev("/dev/nvhost-vic");
                        long dv = GC.GetTotalAllocatedBytes(true) - v0;
                        Check("nvdrv Open(/dev/nvhost-vic) 不建 GPU 通道", "fd ≥ 0、整进程托管分配 < 1 MB", $"fd {(vicFd >= 0 ? "≥ 0" : vicFd.ToString())}、整进程托管分配 {(dv < (1 << 20) ? "< 1 MB" : $"{dv >> 10} KB")}");
                    }
                    byte* buf = FakeNative.Alloc(0x100000);
                    // 显存后备清零：下面「写跟踪排空」用例调真 DispatchCalls，GPU 会执行命令条目指向的这块内存（cmdVa → buf）。
                    //   不清零时内容是分配器复用的旧数据：解到越界同步点号 → ArgumentOutOfRange 中止（10-21 复现 rc 134），解到信号量等待 → 永远等（门禁「卡死」150 秒超时）；
                    //   全 0 = 空命令（新页本来就是 0，所以多数局能过）
                    new Span<byte>(buf, 0x100000).Clear();
                    byte* a = _game + 0x2C00;
                    *(uint*)a = 0x10000; *(int*)(a + 4) = 0;                                   // NvMapCreate { Size, Handle }
                    Ioctl(mapFd, 0xC0080101, a, 8);
                    int nvmapHandle = *(int*)(a + 4);
                    *(int*)a = nvmapHandle; *(int*)(a + 4) = 0; *(int*)(a + 8) = 0; *(int*)(a + 12) = 0x1000; *(long*)(a + 16) = 0; *(ulong*)(a + 24) = (ulong)buf;   // NvMapAlloc
                    Reply alr = Ioctl(mapFd, 0xC0200104, a, 0x20);
                    byte* mp = _game + 0x2D00;   // MapBufferEx { Flags, Kind, NvMapHandle, PageSize, BufferOffset, MappingSize, Offset }
                    byte* um = _game + 0x2D40;   // UnmapBuffer { Offset }
                    void FillMap()
                    {
                        *(uint*)mp = 0; *(int*)(mp + 4) = 0; *(int*)(mp + 8) = nvmapHandle; *(int*)(mp + 12) = 0x10000;
                        *(ulong*)(mp + 16) = 0; *(ulong*)(mp + 24) = 0x10000; *(ulong*)(mp + 32) = 0;
                    }
                    FillMap();
                    Reply mpr = Ioctl(asFd, 0xC0284106, mp, 0x28);
                    ulong gpuVa = *(ulong*)(mp + 32);
                    *(ulong*)um = gpuVa;
                    Reply umr = Ioctl(asFd, 0xC0084105, um, 8);
                    Check("nvmap 建缓冲 + nvhost-as MapBufferEx / UnmapBuffer", "NvResult 0 / 0 / 0、显存地址非 0",
                        $"NvResult {*(uint*)alr.Data} / {*(uint*)mpr.Data} / {*(uint*)umr.Data}、显存地址{(gpuVa != 0 ? "非 0" : " 0")}");

                    // 显存地址分配按「从低地址找第一个空位」（同真机 nvgpu）：非后进先出的映射 / 解除映射不会让地址一路上漂
                    // （上游从最高空闲区间起分配，每跨一个 64 MB 区段新建 128 KB 页表、永不释放）
                    ulong MapNew()
                    {
                        FillMap();
                        Ioctl(asFd, 0xC0284106, mp, 0x28);
                        return *(ulong*)(mp + 32);
                    }


                    void UnmapVa(ulong va)
                    {
                        *(ulong*)um = va;
                        Ioctl(asFd, 0xC0084105, um, 8);
                    }

                    ulong vaA = MapNew();
                    ulong vaB = MapNew();
                    UnmapVa(vaA);
                    ulong vaC = MapNew();
                    UnmapVa(vaB);
                    UnmapVa(vaC);
                    Check("显存地址分配：映射 A、B，解除 A，再映射 C", "C 落回 A 的地址", vaC == vaA ? "C 落回 A 的地址" : $"C 在 0x{vaC:X}（A 0x{vaA:X}、B 0x{vaB:X}）");

                    Msg mapMsg = Msg.Cmif(1, Bytes(asFd, 0xC0284106u)); mapMsg.A.Add(((ulong)mp, 0x28)); mapMsg.B.Add(((ulong)mp, 0x28));
                    Msg unmapMsg = Msg.Cmif(1, Bytes(asFd, 0xC0084105u)); unmapMsg.A.Add(((ulong)um, 8)); unmapMsg.B.Add(((ulong)um, 8));
                    FillMap();
                    // 模拟 GPU 线程：虚拟区间缓存的延迟解除映射队列由 GPU 线程（TryGetOrAddRange）定期清空，单测没有 GPU 工作，夹具代清（不计入）
                    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    object asDev = Ryujinx.HLE.HOS.Services.Nv.INvDrvServices.DeviceFileIdRegistry.GetData(asFd);
                    object asCtx = asDev.GetType().GetField("_asContext", bf).GetValue(asDev);
                    object gmm = asCtx.GetType().GetProperty("Gmm", bf).GetValue(asCtx);
                    object vrc = gmm.GetType().GetProperty("VirtualRangeCache", bf).GetValue(gmm);
                    var deferred = (System.Collections.Concurrent.ConcurrentQueue<(ulong, ulong)>)vrc.GetType().GetField("_deferredUnmaps", bf).GetValue(vrc);
                    MeasureAllocPair("nvdrv 显存 MapBufferEx + UnmapBuffer（一对）", nv, mapMsg, unmapMsg, () => { *(ulong*)um = *(ulong*)(mp + 32); FillMap(); while (deferred.TryDequeue(out _)) { } });

                    // 提交命令（nvhost-gpu Ioctl2 SubmitGpfifo，动森每帧约 2 次、Mac 局拆账 600–740 B/次）：1 条命令条目、带「完成后递增围栏」；
                    // 单测没有渲染线程取命令，夹具在两次之间清 GPU 命令队列（不计入）
                    int gpuFd = OpenDev("/dev/nvhost-gpu");
                    byte* bind = _game + 0x2DF0;  // nvhost-as BindChannel { 通道 fd }：通道绑到这个显存地址空间（游戏建完通道即调）
                    *(int*)bind = gpuFd;
                    Ioctl(asFd, 0xC0044101, bind, 4);
                    byte* sg = _game + 0x2E00;    // SubmitGpfifoArguments { Address, NumEntries, Flags, Fence{Id, Value} }
                    byte* ent = _game + 0x2E40;   // 内联条目
                    ulong cmdVa = MapNew();       // 命令缓冲：一块保持映射的显存（PushEntries 会预读条目指向的命令）
                    void FillSubmit()
                    {
                        *(long*)sg = 0; *(int*)(sg + 8) = 1; *(int*)(sg + 12) = 1 << 1; *(uint*)(sg + 16) = 0; *(uint*)(sg + 20) = 0;
                        // GP 条目：低字 = 地址低 32 位（低 2 位清零），高字 = 地址 39:32 位 | 长度（字）<< 10
                        *(ulong*)ent = (cmdVa & 0xFFFFFFFC) | (((cmdVa >> 32) & 0xFF) << 32) | ((ulong)0x10 << 42);
                    }
                    FillSubmit();
                    Msg sub = Msg.Cmif(11, Bytes(gpuFd, 0xC018481Bu));
                    sub.A.Add(((ulong)sg, 0x18)); sub.A.Add(((ulong)ent, 8)); sub.B.Add(((ulong)sg, 0x18));
                    Reply subr = Call(nv, sub);
                    Check("nvhost-gpu SubmitGpfifo（Ioctl2，1 条、递增围栏）", "结果 0、NvResult 0", $"结果 {subr.Result}、NvResult {*(uint*)subr.Data}");
                    object fifo = Host.Device.Gpu.GPFifo;
                    object cbq = fifo.GetType().GetField("_commandBufferQueue", bf).GetValue(fifo);
                    var tryDequeue = cbq.GetType().GetMethod("TryDequeue");
                    object[] outArg = new object[1];
                    var cbType = cbq.GetType().GetGenericArguments()[0];
                    var cbWords = cbType.GetField("Words");
                    var cbPooled = cbType.GetField("PooledWords");
                    // 模拟 GPU 线程：取走并处理完后归还预取快照（同 DispatchCalls）
                    void Drain()
                    {
                        while ((bool)tryDequeue.Invoke(cbq, outArg))
                        {
                            if ((bool)cbPooled.GetValue(outArg[0])) System.Buffers.ArrayPool<int>.Shared.Return((int[])cbWords.GetValue(outArg[0]));
                        }
                    }
                    MeasureAllocPair("nvhost-gpu SubmitGpfifo（Ioctl2，两次）", nv, sub, sub, () => { Drain(); FillSubmit(); });

                    // 写跟踪排空时机（GPFifoDevice.S2ATrackSync，补丁 0114；10-21 八方2 选项页屏闪根因）：GPU 线程唤醒时排空一次后，DispatchCalls 会接着取走
                    // 排空之后才入队的命令——游戏在那之前写的常量 / 顶点数据还没变成跟踪事件，命令就拿 GPU 侧旧数据画。操作序列：
                    //   提交 E1 → GPU 线程唤醒排空（S2ASync）→ 游戏再写、提交 E2 → DispatchCalls 处理 E1、E2。
                    // 每次提交入队 2 条：命令条目 + 带「完成后递增围栏」标志的围栏递增命令（NvHostChannelDeviceFile.SubmitGpfifo PushHostCommandBuffer）。
                    // 期望（手算）：共排空 2 次；第 2 次在 E1 两条之后、E2 命令条目出队后处理前（此刻队列剩 E2 的围栏递增 1 条）；E1 两条不再触发排空。
                    // 旧代码（只在唤醒时排空）：1 次，E2 用的是没排空的数据
                    {
                        Drain();
                        int syncs = 0, leftAt2nd = -1;
                        var count = cbq.GetType().GetProperty("Count");
                        Ryujinx.Graphics.Gpu.Engine.GPFifo.GPFifoDevice.S2ATrackSync = () => { if (++syncs == 2) leftAt2nd = (int)count.GetValue(cbq); };
                        FillSubmit(); Call(nv, sub);
                        int perSubmit = (int)count.GetValue(cbq);
                        Host.Device.Gpu.GPFifo.S2ASync();
                        FillSubmit(); Call(nv, sub);
                        Host.Device.Gpu.GPFifo.DispatchCalls();
                        Ryujinx.Graphics.Gpu.Engine.GPFifo.GPFifoDevice.S2ATrackSync = null;
                        Check("写跟踪排空：每次提交入队条数 / 共排空次数 / 第 2 次排空时队列剩余", "2 / 2 / 1", $"{perSubmit} / {syncs} / {leftAt2nd}");
                        Drain();
                    }
                }

                // bcat 建投递缓存存储（LibHac 按 ArpIReader 的应用号找当前应用；对拍查出漏设时回 0x147a）
                Reply gb = Call(sm, Msg.Cmif(1, Name8("bcat:u")));
                uint bcat = gb.Move.Count == 1 ? gb.Move[0] : 0;
                Msg cds = Msg.Cmif(1, Bytes(0UL));
                cds.SendPid = true;
                Reply cdsr = Call(bcat, cds);
                Check("bcat:u CreateDeliveryCacheStorageService", "结果 0、move 1 个会话句柄", $"结果 0x{cdsr.Result:X}、move {cdsr.Move.Count} 个{(FakeNative.KindOf(cdsr.Move.Count == 1 ? cdsr.Move[0] : 0) == ObjKind.Session ? "会话" : "非会话")}句柄".Replace("结果 0x0、", "结果 0、"));

                string shaderDir = Path.Combine(Ryujinx.Common.Configuration.AppDataManager.GamesDirPath, "01006f8002326000", "cache", "shader");
                Check("着色器磁盘缓存开（登记后 games/<程序号>/cache/shader 已建）", "已建", Directory.Exists(shaderDir) ? "已建" : "未建");
                Console.Error.WriteLine($"登记用时 {regMs} ms（含 RomFS 目录表）");

                // 读盘目标页写保护（空洞骑士 10-27 切房转场：IStorage.Read 的目标是游戏内存，读盘期间页又被写跟踪保护，pread 由内核写只读页 → EFAULT「Bad address」、游戏崩）：
                //   临时文件 0x8000 字节（第 i 字节 = i*7+3），目标两页 16 KB 设只读；LazyHostFile 从偏移 0x10 读 0x7000 字节。
                //   期望（手算）：返回成功、读到 0x7000、内容等于文件 [0x10, 0x7010)、报过一次写且范围正是目标 [m, m+0x7000)。
                //   旧代码：直接 pread 抛 IOException（错误码 14）——这里另核直接 pread 进只读页确实是 14，证明触发条件与判别码
                {
                    string tf = Path.Combine(Path.GetTempPath(), $"s2a_efault_{Environment.ProcessId}.bin");
                    byte[] data = new byte[0x8000];
                    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
                    File.WriteAllBytes(tf, data);
                    byte* m = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(0x8000, 0x4000);
                    int direct = 0;
                    mprotect((nint)m, 0x8000, 1 /* READ */);
                    try
                    {
                        using (var h = File.OpenHandle(tf)) RandomAccess.Read(h, new Span<byte>(m, 0x100), 0);
                    }
                    catch (IOException e) { direct = e.HResult; }
                    _writes.Clear();
                    GuestMemory.WriteHook = &UnprotectWrite;
                    var lf = new LazyHostFile(tf, data.Length);
                    LibHac.Result lr = lf.Read(out long lgot, 0x10, new Span<byte>(m, 0x7000), LibHac.Fs.ReadOption.None);
                    GuestMemory.WriteHook = null;
                    bool same = new Span<byte>(m, 0x7000).SequenceEqual(data.AsSpan(0x10, 0x7000));
                    bool hooked = _writes.Count == 1 && _writes[0].Va == (ulong)m && _writes[0].Size == 0x7000;
                    Check("fs 读盘目标页写保护（EFAULT）：直接 pread 错误码 / 改中转读 结果 / 读到 / 内容 / 报写范围",
                        "14 / 成功 / 0x7000 / 同文件 / 一次、正是目标",
                        $"{direct} / {(lr.IsSuccess() ? "成功" : lr.ToString())} / 0x{lgot:X} / {(same ? "同文件" : "不同")} / {(hooked ? "一次、正是目标" : $"{_writes.Count} 次")}");
                    lf.Dispose(); mprotect((nint)m, 0x8000, 3); System.Runtime.InteropServices.NativeMemory.AlignedFree(m); File.Delete(tf); _writes.Clear();
                }

                Reply gf = Call(sm, Msg.Cmif(1, Name8("fsp-srv")));
                uint fsp = gf.Move.Count == 1 ? gf.Move[0] : 0;
                Msg scp = Msg.Cmif(1, Bytes(0UL));
                scp.SendPid = true;
                Reply scpr = Call(fsp, scp);
                Reply ods = Call(fsp, Msg.Cmif(200));
                uint st = ods.Move.Count == 1 ? ods.Move[0] : 0;
                Check("fsp-srv SetCurrentProcess + OpenDataStorageByCurrentProcess", "0、0、move 1 个会话句柄", $"{scpr.Result}、{ods.Result}、move {ods.Move.Count} 个{(FakeNative.KindOf(st) == ObjKind.Session ? "会话" : "非会话")}句柄");

                if (st != 0)
                {
                    byte* head = _game + 0x1000;
                    Msg rd = Msg.Cmif(0, Bytes(0UL, 0x50UL));
                    rd.B.Add(((ulong)head, 0x50));
                    Reply rdr = Call(st, rd);
                    Reply gsz = Call(st, Msg.Cmif(4));
                    long romSize = *(long*)gsz.Data;
                    MeasureAlloc("fs IStorage Read 0x50（旧 HLE 框架）", st, () => { Msg x = Msg.Cmif(0, Bytes(0UL, 0x50UL)); x.B.Add(((ulong)head, 0x50)); return x; });
                    Check("IStorage Read(0, 0x50) / GetSize：RomFS 头", "0、头长 0x50、镜像 > 9 GB", $"{rdr.Result}、头长 0x{*(ulong*)head:X}、镜像 {(romSize > 9L << 30 ? "> 9 GB" : romSize.ToString())}");

                    // fs 读盘不互相挡（10-26 动森 Mac 满载 90 秒黑屏：fs 服务端一把锁 + LazyHostFile 全局锁都包着阻塞的 pread，一次慢读让所有 RomFS 读排队）：
                    //   RomFS 文件表里取两个不同文件 A / B；钩子让第一次进 pread 的请求停住（最多 5 秒），A 在别的线程经会话 1 读、停在 pread 里，
                    //   主线程经会话 2 读 B——期望 1 秒内返回、结果 0；放行后 A 也返回 0。旧代码 B 排在 A 的锁后面，要等满 5 秒（反例即旧行为）
                    {
                        long Hd(int i) => ((long*)head)[i];
                        long fmOff = Hd(7), dataOff = Hd(9);
                        byte* fm = _game + 0x3000;
                        Msg rfm = Msg.Cmif(0, Bytes((ulong)fmOff, 0x400UL)); rfm.B.Add(((ulong)fm, 0x400));
                        Call(st, rfm);
                        // 文件元数据项：父 u32、兄弟 u32、数据偏移 u64、大小 u64、哈希兄弟 u32、名长 u32、名（按 4 对齐）
                        var files = new List<(long Off, long Size)>();
                        for (int o = 0; o + 0x20 <= 0x400 && files.Count < 4;)
                        {
                            long dOff = *(long*)(fm + o + 8), dSize = *(long*)(fm + o + 16); int nameLen = *(int*)(fm + o + 28);
                            if (nameLen < 0 || nameLen > 0x300) break;
                            if (dSize >= 16) files.Add((dataOff + dOff, dSize));
                            o += 0x20 + ((nameLen + 3) & ~3);
                        }
                        Reply ods2 = Call(fsp, Msg.Cmif(200));
                        uint st2 = ods2.Move.Count == 1 ? ods2.Move[0] : 0;
                        // 两种停法各用一对还没打开过的文件：停在 pread（文件已打开）、停在打开文件（含 stat）
                        void Blocked(string what, int ia, int ib, bool atOpen)
                        {
                            string name = $"fs 读盘不互相挡：A 停在{what}时另一个会话读 B";
                            if (files.Count < ib + 1 || st2 == 0) { Check(name, "已测", $"未测（文件表取到 {files.Count} 个文件、会话 2 {(st2 != 0 ? "开了" : "没开")}）"); return; }
                            using var entered = new System.Threading.ManualResetEventSlim();
                            using var release = new System.Threading.ManualResetEventSlim();
                            int armed = 1;
                            Action<string> hook = _ => { if (System.Threading.Interlocked.Exchange(ref armed, 0) == 1) { entered.Set(); release.Wait(5000); } };
                            if (atOpen) LazyHostFile.S2ATestBeforeOpen = hook; else LazyHostFile.S2ATestBeforeRead = hook;
                            byte* bufA = _game + 0x3800, bufB = _game + 0x3900;
                            uint rA = 0xFFFF;
                            var tA = new System.Threading.Thread(() =>
                            {
                                Msg m = Msg.Cmif(0, Bytes((ulong)files[ia].Off, 16UL)); m.B.Add(((ulong)bufA, 16));
                                rA = CallTlr(_tlr2, st, m).Result;
                            });
                            tA.Start();
                            bool inA = entered.Wait(3000);
                            var swB = System.Diagnostics.Stopwatch.StartNew();
                            Msg mb = Msg.Cmif(0, Bytes((ulong)files[ib].Off, 16UL)); mb.B.Add(((ulong)bufB, 16));
                            uint rB = Call(st2, mb).Result;
                            long bMs = swB.ElapsedMilliseconds;
                            release.Set();
                            tA.Join(10000);
                            LazyHostFile.S2ATestBeforeRead = null; LazyHostFile.S2ATestBeforeOpen = null;
                            Check(name, $"A 已停在{what}、B 结果 0 且 < 1000 ms、A 放行后结果 0",
                                $"A {(inA ? $"已停在{what}" : $"没停在{what}")}、B 结果 {rB} 且 {(bMs < 1000 ? "< 1000 ms" : $"{bMs} ms")}、A 放行后结果 {rA}");
                        }
                        Blocked("打开文件", 0, 1, atOpen: true);
                        Blocked(" pread 里", 2, 3, atOpen: false);

                        // 两个数据存储会话并发读（审计 / 情报 #883：每次 OpenDataStorage 都在同一个只读流上新包一个 StreamStorage，各带一把锁、
                        //   流的读位置共用——一边刚设好位置、另一边改掉，读到的是别处的字节）。期望按「各读各的偏移」定：两条线程各经自己的会话
                        //   读 20000 次不同文件的头 16 字节，每次都与单线程先读到的那 16 字节逐字节相同，不符 0 次
                        {
                            string name = "RomFS 两个会话并发读：不符次数（A / B 各 20000 次，读不同偏移）";
                            if (files.Count < 2 || st2 == 0) Check(name, "已测", $"未测（文件表取到 {files.Count} 个文件）");
                            else
                            {
                                byte* bufA = _game + 0x3A00, bufB = _game + 0x3B00;
                                byte[] Once(uint sess, byte* b, long off) { Msg m = Msg.Cmif(0, Bytes((ulong)off, 16UL)); m.B.Add(((ulong)b, 16)); Call(sess, m); return new Span<byte>(b, 16).ToArray(); }
                                long offA = 0, offB = files[0].Off;   // 镜像头对文件数据；文件表里有头部不同的两个文件就改用它们
                                for (int x = 1; x < files.Count; x++) if (!Once(st, bufA, files[0].Off).AsSpan().SequenceEqual(Once(st, bufB, files[x].Off))) { offA = files[0].Off; offB = files[x].Off; break; }
                                byte[] wantA = Once(st, bufA, offA), wantB = Once(st2, bufB, offB);
                                int badA = 0, badB = 0;
                                var tA = new System.Threading.Thread(() =>
                                {
                                    for (int i = 0; i < 20000; i++) { Msg m = Msg.Cmif(0, Bytes((ulong)offA, 16UL)); m.B.Add(((ulong)bufA, 16)); CallTlr(_tlr2, st, m); if (!new Span<byte>(bufA, 16).SequenceEqual(wantA)) badA++; }
                                });
                                tA.Start();
                                for (int i = 0; i < 20000; i++) { Msg m = Msg.Cmif(0, Bytes((ulong)offB, 16UL)); m.B.Add(((ulong)bufB, 16)); Call(st2, m); if (!new Span<byte>(bufB, 16).SequenceEqual(wantB)) badB++; }
                                tA.Join();
                                Check(name, $"0 / 0（两个文件头{(wantA.AsSpan().SequenceEqual(wantB) ? "相同，判据失效" : "不同")}）", $"{badA} / {badB}（两个文件头{(wantA.AsSpan().SequenceEqual(wantB) ? "相同，判据失效" : "不同")}）");
                            }
                        }
                    }
                }

                // IFile.Write 大写入不按长度另开托管数组（10-20 普查：原先 new byte[SendBuff.Size] 整段拷贝；iPhone 低区地址只剩几百 MB）：
                //   设备存档里建 32 MB 文件、一次写 32 MB，服务请求本身额外分配 < 64 KB，读回一致
                {
                    const int Big = 32 << 20;
                    // 动森设备存档（SaveDataSpaceId.User、SaveDataAttribute：程序号 / 用户号 0 / 类型 Device=3），目录存档、写完删文件、不提交
                    byte[] attr = new byte[0x40]; BitConverter.GetBytes(0x01006F8002326000UL).CopyTo(attr, 0); attr[0x20] = 3;
                    Reply sdr = Call(fsp, Msg.Cmif(51, Bytes(1L, attr)));
                    uint sdfs = sdr.Move.Count == 1 ? sdr.Move[0] : 0;
                    if (sdfs == 0) { Check("IFile.Write 大写入：开动森设备存档", "结果 0、1 个会话", $"结果 0x{sdr.Result:X}、{sdr.Move.Count} 个"); goto IFileWriteDone; }
                    byte* wpath = _game + 0x2000;
                    new Span<byte>(wpath, 0x301).Clear();
                    Encoding.UTF8.GetBytes("/s2a_wtest.bin").CopyTo(new Span<byte>(wpath, 0x301));
                    Msg del = Msg.Cmif(1); del.X.Add(((ulong)wpath, 0x301)); Call(sdfs, del);
                    Msg cf = Msg.Cmif(0, Bytes(0u, 0u, (long)Big)); cf.X.Add(((ulong)wpath, 0x301));
                    Reply cfr = Call(sdfs, cf);
                    Msg of = Msg.Cmif(8, Bytes(3u)); of.X.Add(((ulong)wpath, 0x301));
                    Reply ofr = Call(sdfs, of);
                    uint file = ofr.Move.Count == 1 ? ofr.Move[0] : 0;
                    byte* big = FakeNative.Alloc(Big);
                    for (int i = 0; i < Big; i++) big[i] = (byte)(i * 31 + 7);
                    Msg wr = Msg.Cmif(1, Bytes(0u, 0u, 0L, (long)Big)); wr.A.Add(((ulong)big, (ulong)Big));
                    long w0 = GC.GetAllocatedBytesForCurrentThread();
                    Reply wrr = Call(file, wr);
                    long wa = GC.GetAllocatedBytesForCurrentThread() - w0;
                    new Span<byte>(big, Big).Clear();
                    Msg rd2 = Msg.Cmif(0, Bytes(0u, 0u, 0L, (long)Big)); rd2.B.Add(((ulong)big, (ulong)Big));
                    Reply rd2r = Call(file, rd2);
                    bool okData = true; for (int i = 0; i < Big && okData; i += 4093) okData = big[i] == (byte)(i * 31 + 7);
                    Check($"IFile.Write 32 MB 一次写：额外分配 < 64 KB（{wa >> 10} KB）、读回一致", "建 0、开 0、写 0、读 0 / 是 / 一致",
                        $"建 {cfr.Result}、开 {ofr.Result}、写 {wrr.Result}、读 {rd2r.Result} / {(wa < 65536 ? "是" : "否")} / {(okData ? "一致" : "不一致")}");
                    Call(sdfs, del);

                    // fs 命令失败看得见（10-26 普查双点医院：开文件失败被静默吞掉，游戏拿空文件对象写存档崩在 FileAccessor::Write）：
                    //   开不存在的文件 = 游戏本该自己处理的探测结果 → 普通一行、不报红；打开模式 0（既不读也不写）→ 报红，带结果码与路径
                    List<string> fsLines = new();
                    Action<string> logWas = Red.Log;
                    Red.Log = line => { fsLines.Add(line); logWas(line); };
                    int fsAt0 = 0;
                    Encoding.UTF8.GetBytes("/s2a_nofile.bin\0").CopyTo(new Span<byte>(wpath, 0x301));
                    Msg on = Msg.Cmif(8, Bytes(1u)); on.X.Add(((ulong)wpath, 0x301));
                    Reply onr = Call(sdfs, on);
                    bool noteLine = fsLines.Skip(fsAt0).Any(l => l == "[fs] 没有 IFileSystem.OpenFile：2002-0001，路径 /s2a_nofile.bin");
                    bool noteRed = fsLines.Skip(fsAt0).Any(l => l.StartsWith("【红】"));
                    Encoding.UTF8.GetBytes("/s2a_wtest.bin\0").CopyTo(new Span<byte>(wpath, 0x301));
                    Call(sdfs, cf);
                    int fsAt1 = fsLines.Count;
                    Msg om = Msg.Cmif(8, Bytes(0u)); om.X.Add(((ulong)wpath, 0x301));
                    Reply omr = Call(sdfs, om);
                    string redLine = fsLines.Skip(fsAt1).FirstOrDefault(l => l.StartsWith("【红】fs 失败：IFileSystem.OpenFile")) ?? "无";
                    Check("fs 失败上报：开不存在的文件 / 打开模式 0", "结果 0x202、普通一行、不报红 / 结果非 0、【红】带路径 /s2a_wtest.bin",
                        $"结果 0x{onr.Result:X}、{(noteLine ? "普通一行" : "没有普通行")}、{(noteRed ? "报了红" : "不报红")} / 结果{(omr.Result != 0 ? "非 0" : " 0")}、{(redLine.Contains("路径 /s2a_wtest.bin") ? "【红】带路径 /s2a_wtest.bin" : redLine)}");
                    Red.Log = logWas;
                    Call(sdfs, del);

                    // 同一存档重复挂载共用一个视图（10-26 普查双点医院：一个挂载整局开着，每次存档步骤前后又挂同一存档；
                    //   原先每次打开都把已提交版本盖回工作目录，冲掉开着的挂载没提交的目录，CreateFile 落空、拿空文件对象写崩）。
                    //   期望按语义手写：A 建目录 → 之前开的 B、之后开的 C 都看得到、C 开后 A 仍看得到；全部关掉不提交再挂 → 没有（未提交即丢）
                    {
                        byte* dp = _game + 0x2600; new Span<byte>(dp, 0x301).Clear(); Encoding.UTF8.GetBytes("/s2a_share_dir").CopyTo(new Span<byte>(dp, 0x301));
                        uint Mount() { Reply r = Call(fsp, Msg.Cmif(51, Bytes(1L, attr))); return r.Move.Count == 1 ? r.Move[0] : 0; }
                        uint Entry(uint fs, uint cmd) { Msg m = Msg.Cmif(cmd); m.X.Add(((ulong)dp, 0x301)); return Call(fs, m).Result; }
                        uint fsB = Mount();
                        uint mk = Entry(sdfs, 2), inB = Entry(fsB, 7);
                        uint fsC = Mount();
                        uint inC = Entry(fsC, 7), inA = Entry(sdfs, 7);
                        FakeNative.HandleClose(fsB); FakeNative.HandleClose(fsC); FakeNative.HandleClose(sdfs);
                        uint fsD = Mount();
                        uint inD = Entry(fsD, 7);
                        Entry(fsD, 3); FakeNative.HandleClose(fsD);
                        Check("同一存档重复挂载共用视图：建目录 / 先开的 B / 后开的 C / C 开后 A / 全关不提交再挂", "0 / 0 / 0 / 0 / 0x202",
                            $"{mk} / {inB} / {inC} / {inA} / 0x{inD:X}");
                    }

                    // 同一存档两次首次打开并发（审计 #836 F-save-share-race）：两条线程同时打开同一存档键，真打开要 50 ms（LibHac 挂载会按已提交版本重同步工作目录）。
                    //   期望按「一个存档同一时刻只有一个视图」定：真打开 1 次、共享计数 2；两个都关掉后计数 0
                    {
                        string key = "s2a-share-race-test";
                        int opens = 0;
                        using System.Threading.Barrier go = new(2);
                        Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.OpenFileSystem slowOpen = (ref LibHac.Common.SharedRef<LibHac.FsSrv.Sf.IFileSystem> fs) =>
                        {
                            System.Threading.Interlocked.Increment(ref opens);
                            System.Threading.Thread.Sleep(50);
                            return LibHac.Result.Success;
                        };
                        void Opener()
                        {
                            using LibHac.Common.SharedRef<LibHac.FsSrv.Sf.IFileSystem> fs = new();
                            go.SignalAndWait();
                            Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.OpenShared(key, ref fs.Ref, slowOpen);
                        }
                        System.Threading.Thread ta = new(Opener), tb = new(Opener);
                        ta.Start(); tb.Start(); ta.Join(); tb.Join();
                        int shared = Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.Count(key);
                        Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.Closed(key); Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.Closed(key);
                        int after = Ryujinx.HLE.HOS.Services.Fs.S2ASaveShare.Count(key);
                        Check("同一存档两次首次打开并发：真打开次数 / 共享计数 / 全关后计数", "1 / 2 / 0", $"{opens} / {shared} / {after}");
                    }

                    // 存档原子提交（体系提升方案 F1，照上游参考实现 / 奥德赛 iOS）：挂载把已提交的 0 clonefile 成工作目录 1，提交把 1 克隆后 RENAME_SWAP 换进 0。
                    //   期望按语义手写：写模式文件没关时提交回 WriteModeFileNotClosed（LibHac 常量）、关了提交 0；提交后 0 与 1 的文件共享 APFS 克隆号（不复制字节）；
                    //   宿主上放一个「提交到一半被杀」留下的 .s2a_new，再挂：残留清掉、读到上次提交的 AAAA、0 与 1 仍共享克隆号；改成 BBBB 不提交再挂仍是 AAAA。
                    //   旧代码（LibHac 逐字节复制）：克隆号不同、残留不清 → 判红
                    {
                        byte* ap = _game + 0x2800; new Span<byte>(ap, 0x301).Clear(); Encoding.UTF8.GetBytes("/s2a_atom.bin").CopyTo(new Span<byte>(ap, 0x301));
                        byte* buf = _game + 0x2C00;
                        uint Mount() { Reply r = Call(fsp, Msg.Cmif(51, Bytes(1L, attr))); return r.Move.Count == 1 ? r.Move[0] : 0; }
                        uint PathCmd(uint fs, uint cmd, byte[] head = null) { Msg m = Msg.Cmif(cmd, head ?? Array.Empty<byte>()); m.X.Add(((ulong)ap, 0x301)); return Call(fs, m).Result; }
                        uint Open(uint fs, uint mode) { Msg m = Msg.Cmif(8, Bytes(mode)); m.X.Add(((ulong)ap, 0x301)); Reply r = Call(fs, m); return r.Move.Count == 1 ? r.Move[0] : 0; }
                        uint Write(uint f, string v) { Encoding.ASCII.GetBytes(v).CopyTo(new Span<byte>(buf, 4)); Msg m = Msg.Cmif(1, Bytes(0u, 0u, 0L, 4L)); m.A.Add(((ulong)buf, 4)); return Call(f, m).Result; }
                        string Read(uint fs) { uint f = Open(fs, 1); if (f == 0) return "开不了"; new Span<byte>(buf, 4).Clear(); Msg m = Msg.Cmif(0, Bytes(0u, 0u, 0L, 4L)); m.B.Add(((ulong)buf, 4)); Call(f, m); FakeNative.HandleClose(f); return Encoding.ASCII.GetString(new Span<byte>(buf, 4)); }
                        uint Commit(uint fs) => Call(fs, Msg.Cmif(10)).Result;
                        uint expectNotClosed = LibHac.Fs.ResultFs.WriteModeFileNotClosed.Value.Value;

                        uint fa = Mount();
                        PathCmd(fa, 1);
                        uint cr = PathCmd(fa, 0, Bytes(0u, 0u, 4L));
                        uint fw = Open(fa, 3);
                        uint wA = Write(fw, "AAAA");
                        uint c1 = Commit(fa);
                        FakeNative.HandleClose(fw);
                        uint c2 = Commit(fa);
                        FakeNative.HandleClose(fa);
                        string root = Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, "bis", "user", "save");
                        string sd = Directory.Exists(root) ? Directory.GetDirectories(root).FirstOrDefault(d => File.Exists(Path.Combine(d, "0", "s2a_atom.bin"))) : null;
                        string sf0 = sd == null ? null : Path.Combine(sd, "0", "s2a_atom.bin"), sf1 = sd == null ? null : Path.Combine(sd, "1", "s2a_atom.bin");
                        string shared1 = sd == null ? "找不到存档目录" : S2ACloneId.Of(sf0) != 0 && S2ACloneId.Of(sf0) == S2ACloneId.Of(sf1) ? "共享" : "不共享";
                        string committed = sd == null ? "-" : File.ReadAllText(sf0);

                        string leftover = sd == null ? null : Path.Combine(sd, ".s2a_new");
                        if (leftover != null) { Directory.CreateDirectory(leftover); File.WriteAllText(Path.Combine(leftover, "s2a_atom.bin"), "JUNK"); }
                        uint fb = Mount();
                        string cleared = leftover == null ? "-" : Directory.Exists(leftover) ? "残留还在" : "清掉";
                        string readB = Read(fb);
                        string shared2 = sd == null ? "-" : S2ACloneId.Of(sf0) != 0 && S2ACloneId.Of(sf0) == S2ACloneId.Of(sf1) ? "共享" : "不共享";
                        uint fwb = Open(fb, 3); Write(fwb, "BBBB"); FakeNative.HandleClose(fwb);
                        FakeNative.HandleClose(fb);
                        uint fc = Mount();
                        string readC = Read(fc);
                        PathCmd(fc, 1); Commit(fc); FakeNative.HandleClose(fc);
                        Check("存档原子提交：建 / 写 / 开着提交 / 关了提交 / 已提交内容 / 0 与 1 克隆共享 / 重挂清残留 / 读 / 重挂后共享 / 改了不提交再挂",
                            $"0 / 0 / 0x{expectNotClosed:X} / 0 / AAAA / 共享 / 清掉 / AAAA / 共享 / AAAA",
                            $"{cr} / {wA} / 0x{c1:X} / {c2} / {committed} / {shared1} / {cleared} / {readB} / {shared2} / {readC}");

                        // 提交与增删改共锁（11-01 全量审计 P0-2）：直接在存档事务这一层测（服务层上面还有别的包装层，测不出这把锁）。底层是只记「建文件发生过」的假存档文件系统，
                        //   宿主目录原语包一层让克隆停住。期望按语义手写：克隆停住的 400 ms 里另一线程的建文件没有落到底层；放行后提交 0、建文件 0、底层建文件 1 次。
                        //   旧代码（建文件不持锁）在克隆停住时就落到底层 → 判红
                        {
                            var realTx = Ryujinx.HLE.FileSystem.S2AHostFileSystem.Transaction; var btx = new BlockingTransaction(realTx);
                            string td = Path.Combine(Path.GetTempPath(), $"s2a_txlock_{Environment.ProcessId}");
                            if (Directory.Exists(td)) Directory.Delete(td, true);
                            Directory.CreateDirectory(Path.Combine(td, "0")); Directory.CreateDirectory(Path.Combine(td, "1")); File.WriteAllText(Path.Combine(td, "1", "a.bin"), "AAAA");
                            FakeSaveFs fake = new();
                            using LibHac.Common.SharedRef<LibHac.FsSystem.ISaveDataFileSystem> baseFs = new(fake);
                            var copy = LibHac.Common.SharedRef<LibHac.FsSystem.ISaveDataFileSystem>.CreateCopy(in baseFs);
                            var tx = new Ryujinx.HLE.FileSystem.S2ASaveTransaction(ref copy, td);
                            Ryujinx.HLE.FileSystem.S2AHostFileSystem.Transaction = btx;
                            int rCommit = -1, rCreate = -1;
                            btx.Block = true;
                            System.Threading.Thread tc = new(() => rCommit = tx.Commit().IsSuccess() ? 0 : 1);
                            tc.Start();
                            bool inClone = btx.InClone.Wait(5000);
                            System.Threading.Thread tcr = new(() => { var p0 = new LibHac.Fs.Path(); rCreate = tx.CreateFile(in p0, 4, LibHac.Fs.Fsa.CreateFileOptions.None).IsSuccess() ? 0 : 1; });
                            tcr.Start();
                            System.Threading.Thread.Sleep(400);
                            int createdDuringClone = System.Threading.Volatile.Read(ref fake.Creates);
                            btx.Block = false; btx.Release.Set(); tc.Join(); tcr.Join();
                            Ryujinx.HLE.FileSystem.S2AHostFileSystem.Transaction = realTx;
                            tx.Dispose(); Directory.Delete(td, true);
                            Check("存档提交与增删改共锁：克隆停住 / 停住时另一线程建文件已落到底层 / 提交 / 建文件 / 底层建文件次数",
                                "True / 0 / 0 / 0 / 1", $"{inClone} / {createdDuringClone} / {rCommit} / {rCreate} / {fake.Creates}");
                        }

                        // 宿主目录原语约定（#1072；苹果实现 platform/apple/cs/savecommit）：期望按接口约定手写（Darwin：EINVAL 22、ERANGE 34）——
                        //   Supported 真且不留探测目录；超 64 字节的记号 EINVAL 且没写（随后读回 0、长度 0）；写 16 字节后用 4 字节缓冲读 ERANGE、长度回填 16；
                        //   目录落盘 0；对指向目录的符号链接落盘返回非 0（不跟随；Darwin 带 O_DIRECTORY 为 ENOTDIR）；
                        //   安全只读打开：普通文件 0 且读得到内容、指向它的链接非 0、FIFO 立即返回非 0（不阻塞：没有写端也不卡）、失败时句柄为空
                        {
                            Ryujinx.HLE.FileSystem.IHostDirectoryTransaction tx = Ryujinx.HLE.FileSystem.S2AHostFileSystem.Transaction;
                            string td = Path.Combine(Path.GetTempPath(), $"s2a_txn_{Environment.ProcessId}"), tl = td + "_link";
                            if (Directory.Exists(td)) Directory.Delete(td, true);
                            if (File.Exists(tl) || Directory.Exists(tl)) File.Delete(tl);
                            Directory.CreateDirectory(td); File.CreateSymbolicLink(tl, td);
                            bool sup = tx.Supported(td);
                            int probes = Directory.GetFileSystemEntries(td, ".s2a_probe_*").Length;
                            int w65 = tx.WriteToken(td, new byte[65]);
                            int rNone = tx.ReadToken(td, new byte[16], out int lNone);
                            int w16 = tx.WriteToken(td, new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                            int rSmall = tx.ReadToken(td, new byte[4], out int lSmall);
                            int sync = tx.SyncDirectory(td), syncLink = tx.SyncDirectory(tl);
                            string tf = Path.Combine(td, "f.bin"), tfl = Path.Combine(td, "f_link"), tff = Path.Combine(td, "fifo");
                            File.WriteAllText(tf, "S2A1"); File.CreateSymbolicLink(tfl, tf);
                            System.Diagnostics.Process.Start("/usr/bin/mkfifo", tff).WaitForExit();
                            int oFile = tx.OpenRead(tf, out Microsoft.Win32.SafeHandles.SafeFileHandle hf);
                            string oText = "-"; if (oFile == 0) using (var fs = new FileStream(hf, FileAccess.Read)) oText = new StreamReader(fs).ReadToEnd();
                            int oLink = tx.OpenRead(tfl, out var hl), oFifo = tx.OpenRead(tff, out var hp);
                            bool nullOnFail = hl == null && hp == null;
                            File.Delete(tl); Directory.Delete(td, true);
                            Check("存档宿主目录原语：可用 / 探测残留 / 超长记号 / 无记号读 / 写 16 字节 / 小缓冲读 / 目录落盘 / 链接落盘 / 安全只读打开（文件、内容 / 链接 / FIFO 不阻塞 / 失败句柄空）",
                                "True / 0 / 22 / 0、0 / 0 / 34、16 / 0 / 非 0 / 0、S2A1 / 非 0 / 非 0 / True",
                                $"{sup} / {probes} / {w65} / {rNone}、{lNone} / {w16} / {rSmall}、{lSmall} / {sync} / {(syncLink != 0 ? "非 0" : "0")} / {oFile}、{oText} / {(oLink != 0 ? "非 0" : "0")} / {(oFifo != 0 ? "非 0" : "0")} / {nullOnFail}");
                        }

                        // 交换之后附加数据落不了地（审计 #836 F-save-extra-after-swap）：宿主把 ExtraData1 改成新内容 M1（保留区 0x1F0 一字节翻转），
                        //   ExtraData0 仍是旧的 M0 并设成不可改（chflags uchg），让交换后写 ExtraData0 失败。期望按「交换即提交」定：提交回 0；
                        //   清掉不可改标记后再挂，附加数据是新的 M1（不能用旧 M0 盖掉）、文件内容是这次提交的 DDDD
                        {
                            string ed0 = sd == null ? null : Path.Combine(sd, "ExtraData0"), ed1 = sd == null ? null : Path.Combine(sd, "ExtraData1");
                            string extraWas = "无 ExtraData0", readE = "-"; uint xcd = 0xFFFF;
                            if (ed0 != null && File.Exists(ed0))
                            {
                                static void Chflags(string flag, string p) { using System.Diagnostics.Process proc = System.Diagnostics.Process.Start("/usr/bin/chflags", new[] { flag, p }); proc.WaitForExit(); }
                                uint xfd = Mount();
                                PathCmd(xfd, 0, Bytes(0u, 0u, 4L));
                                uint fwd = Open(xfd, 3); Write(fwd, "DDDD"); FakeNative.HandleClose(fwd);
                                byte[] m0 = File.ReadAllBytes(ed0), m1 = (byte[])m0.Clone(); m1[0x1F0] ^= 0x5A;
                                File.WriteAllBytes(ed1, m1);
                                Chflags("uchg", ed0);
                                xcd = Commit(xfd);
                                Chflags("nouchg", ed0);
                                FakeNative.HandleClose(xfd);
                                uint fe = Mount(); readE = Read(fe); PathCmd(fe, 1); Commit(fe); FakeNative.HandleClose(fe);
                                byte n0 = File.ReadAllBytes(ed0)[0x1F0], n1 = File.ReadAllBytes(ed1)[0x1F0];
                                extraWas = n0 == m1[0x1F0] && n1 == m1[0x1F0] ? "新" : n1 == m0[0x1F0] ? "旧的盖掉了新的" : $"不对（0 {n0:x2} / 1 {n1:x2}）";
                            }
                            Check("存档提交：交换后附加数据落不了地 → 提交结果 / 重挂后附加数据 / 文件内容", "0 / 新 / DDDD", $"{xcd} / {extraWas} / {readE}");
                        }
                    }
                }
                IFileWriteDone:
                // DLC 登记（aoc.tsv + aoc/<idx>/）：一个有数据（idx 1）、一个仅授权（idx 2）→ aoc:u 数 / 列按清单回答；有数据的挂得上 RomFS，仅授权的挂不上（报红）
                {
                    const ulong App = 0x01006F8002326000UL, Base = App + 0x1000;
                    // 目录带进程号（10-24 门禁偶发红 2002-1002：几棵树的门禁同时跑、共用 TMPDIR，固定目录被别的门禁删掉重建，DLC 数据中途消失）
                    string ad = Path.Combine(Path.GetTempPath(), $"s2a_aoc_test_{Environment.ProcessId}");
                    if (Directory.Exists(ad)) Directory.Delete(ad, true);
                    Directory.CreateDirectory(Path.Combine(ad, "aoc", "1"));
                    File.WriteAllText(Path.Combine(ad, "aoc", "1", "hello.txt"), "s2a dlc");
                    File.WriteAllText(Path.Combine(ad, "aoc.tsv"), $"idx\ttitle_id\tversion\tfiles\tbytes\n1\t{Base + 1:x16}\t0\t1\t7\n2\t{Base + 2:x16}\t0\t-1\t0\n");
                    int nreg = Aoc.Register(ad);
                    Reply gaoc = Call(sm, Msg.Cmif(1, Name8("aoc:u")));
                    uint aocs = gaoc.Move.Count == 1 ? gaoc.Move[0] : 0;
                    Reply cnt = Call(aocs, Msg.Cmif(0, Bytes(App)));
                    uint* lb = (uint*)(_game + 0x3000);
                    Msg lsm = Msg.Cmif(1, Bytes(App, 0u, 8u)); lsm.B.Add(((ulong)lb, 32));
                    Reply lsr = Call(aocs, lsm);
                    Check("DLC 登记：清单 2 个 → 登记 / Count / List", "2 / 结果 0、2 / 结果 0、2 个：1 2",
                        $"{nreg} / 结果 {cnt.Result}、{*(uint*)cnt.Data} / 结果 {lsr.Result}、{*(uint*)lsr.Data} 个：{lb[0]} {lb[1]}");
                    // aoc 列表（怪猎每分钟约 37 次，原先每次把全部 DLC 编号复制成新列表 4.4 KB）：按序号读，稳态 0 字节
                    //   夹具没登记游戏进程：GetProcess 每次走「找不到 pid → 退回当前应用」并打一行 Warning（插值字符串约 500 B）；
                    //   正式局进程找得到（10-22 怪猎 / 动森真机 host.log 这行各只 1 次），计量时关 Warning 级，量的是生产路径
                    Ryujinx.Common.Logging.Logger.SetEnable(Ryujinx.Common.Logging.LogLevel.Warning, false);
                    MeasureAlloc("aoc ListAddOnContentByApplicationId（#1，2 个 DLC）", aocs, () => { Msg m = Msg.Cmif(1, Bytes(App, 0u, 8u)); m.B.Add(((ulong)lb, 32)); return m; });
                    Ryujinx.Common.Logging.Logger.SetEnable(Ryujinx.Common.Logging.LogLevel.Warning, true);
                    // aoc #8 GetAddOnContentListChangedEvent（暗黑3 每秒约 30 次取一次就关）：应答 1 个复制句柄，稳态 0 字节
                    Reply lce = Call(aocs, Msg.Cmif(8));
                    Check("aoc GetAddOnContentListChangedEvent", "结果 0、复制 1 个事件句柄", $"结果 {lce.Result}、复制 {lce.Copy.Count} 个{(lce.Copy.Count == 1 && FakeNative.KindOf(lce.Copy[0]) is ObjKind.EventR ? "事件" : "非事件")}句柄");
                    foreach (uint h in lce.Copy) FakeNative.HandleClose(h);
                    MeasureAllocCloseCopy("aoc GetAddOnContentListChangedEvent（#8，取完即关）", aocs, Msg.Cmif(8));
                    byte[] sid = { 0, 0, 0, 0, 0, 0, 0, 0 };
                    Reply od1 = Call(fsp, Msg.Cmif(202, Bytes(sid, Base + 1)));
                    uint dst = od1.Move.Count == 1 ? od1.Move[0] : 0;
                    byte* dlch = _game + 0x4000; *(ulong*)dlch = 0;
                    Msg dr = Msg.Cmif(0, Bytes(0UL, 0x50UL)); dr.B.Add(((ulong)dlch, 0x50));
                    Reply drr = dst != 0 ? Call(dst, dr) : default;
                    Reply od2 = Call(fsp, Msg.Cmif(202, Bytes(sid, Base + 2)));
                    Check("DLC 挂载：有数据的给 RomFS（头长 0x50）、仅授权的挂不上", "结果 0、头长 0x50 / 失败",
                        $"结果 {od1.Result}、头长 0x{(dst != 0 ? *(ulong*)dlch : 0):X} / {(od2.Result != 0 && od2.Move.Count == 0 ? "失败" : $"结果 {od2.Result}、{od2.Move.Count} 个句柄")}");
                    try { Directory.Delete(ad, true); } catch (IOException) { }
                }

                // appletOE 命令 10 OpenApplicationProxyWithFlags（Switch 2 SDK 起局用它替代命令 0，Pokopia）：标志 0 → 回一个 IApplicationProxy 对象，且能照常取 ICommonStateGetter（命令 0）
                {
                    Reply goe = Call(sm, Msg.Cmif(1, Name8("appletOE")));
                    uint oe = goe.Move.Count == 1 ? goe.Move[0] : 0;
                    Msg opx = Msg.Cmif(10, Bytes(0u, 0u, 0UL)); opx.SendPid = true; opx.Copy.Add(0xFFFF8001);
                    Reply r10 = Call(oe, opx);
                    uint proxy = r10.Move.Count == 1 ? r10.Move[0] : 0;
                    Reply csg = proxy != 0 ? Call(proxy, Msg.Cmif(0)) : default;
                    Check("appletOE 命令 10（带标志打开应用代理）→ 代理对象 → 取 ICommonStateGetter", "对象 1 个、子对象 1 个",
                        $"对象 {r10.Move.Count} 个、子对象 {(proxy != 0 ? csg.Move.Count : 0)} 个");
                }
                // AM 小程序存储（10-20 普查高风险：按游戏给的大小建托管数组、读写各再拷一份）：CreateStorage 32 MB → 访问器写 32 MB → 读回 32 MB，
                //   每步服务请求额外托管分配 < 64 KB、读回一致；传输内存存储走游戏内存原地视图（GuestView 首地址 = 游戏地址，不拷）
                {
                    const int Big = 32 << 20;
                    Reply goe = Call(sm, Msg.Cmif(1, Name8("appletOE")));
                    uint oe = goe.Move.Count == 1 ? goe.Move[0] : 0;
                    Msg opx = Msg.Cmif(0, Bytes(0UL)); opx.SendPid = true;
                    Reply amOpr = Call(oe, opx);
                    uint proxy = amOpr.Move.Count == 1 ? amOpr.Move[0] : 0;
                    Reply lcr = Call(proxy, Msg.Cmif(11));
                    uint creator = lcr.Move.Count == 1 ? lcr.Move[0] : 0;
                    long m0 = GC.GetAllocatedBytesForCurrentThread();
                    Reply csr = Call(creator, Msg.Cmif(10, Bytes((long)Big)));
                    long csA = GC.GetAllocatedBytesForCurrentThread() - m0;
                    uint amSto = csr.Move.Count == 1 ? csr.Move[0] : 0;
                    Reply acr = Call(amSto, Msg.Cmif(0));
                    uint amAcc = acr.Move.Count == 1 ? acr.Move[0] : 0;
                    byte* amBuf = FakeNative.Alloc(Big);
                    for (int i = 0; i < Big; i++) amBuf[i] = (byte)(i * 13 + 5);
                    Msg amw = Msg.Cmif(10, Bytes(0UL)); amw.A.Add(((ulong)amBuf, (ulong)Big));
                    m0 = GC.GetAllocatedBytesForCurrentThread();
                    Reply amwr = Call(amAcc, amw);
                    long wA = GC.GetAllocatedBytesForCurrentThread() - m0;
                    new Span<byte>(amBuf, Big).Clear();
                    Msg amr = Msg.Cmif(11, Bytes(0UL)); amr.B.Add(((ulong)amBuf, (ulong)Big));
                    m0 = GC.GetAllocatedBytesForCurrentThread();
                    Reply amrr = Call(amAcc, amr);
                    long rA = GC.GetAllocatedBytesForCurrentThread() - m0;
                    bool amSame = true; for (int i = 0; i < Big && amSame; i += 4093) amSame = amBuf[i] == (byte)(i * 13 + 5);
                    Check($"AM 存储 32 MB：建 / 写 / 读 额外托管分配都 < 64 KB（{csA >> 10} / {wA >> 10} / {rA >> 10} KB）、读回一致", "结果 0 / 0 / 0、是、一致",
                        $"结果 {csr.Result} / {amwr.Result} / {amrr.Result}、{(csA < 65536 && wA < 65536 && rA < 65536 ? "是" : "否")}、{(amSame ? "一致" : "不一致")}");
                    System.Memory<byte>? gv = Ryujinx.HLE.S2AHooks.GuestView?.Invoke((ulong)amBuf, Big);
                    bool inPlace; fixed (byte* amGp = gv.HasValue ? gv.Value.Span : default) inPlace = amGp == amBuf && gv.Value.Length == Big;
                    Check("传输内存存储：GuestView 是游戏内存原地视图（首地址 = 游戏地址、长度相同）", "是", inPlace ? "是" : "否");
                }

                // ro LoadNro（10-20 普查高风险：整段 NRO 拷两份——new byte[nroSize] + NroExecutable.Program）：32 MB 的 NRO（NRR 登记其 SHA-256）→ LoadNro
                //   服务路径额外托管分配 < 64 KB；映射阶段换成回 3 的假实现（不打红、不入日志队列），结果必须是 AlreadyLoaded（解析真跑完）。拆法见 RoAllocProbeTest.cs
                {
                    (uint ros, uint rinr, uint lnrrr, ulong nro, ulong warm) = RoSetup(sm);
                    // 预热用小 NRO（12 KB，同样的段布局）：解析路径上的一次性懒初始化谁先付取决于线程先后（10-21 列车高负载下偶发 101 KB），先加载小的一次再量
                    RoLoadNroAlloc(ros, warm, RoWarmSz);
                    (long nA, uint lr) = RoLoadNroAlloc(ros, nro, RoNroSz);
                    Check($"ro LoadNro 32 MB：服务路径额外托管分配 < 64 KB（{nA >> 10} KB）", $"初始化 0、登记 NRR 0、结果 0x{RoAlreadyLoaded:X}、是",
                        $"初始化 {rinr}、登记 NRR {lnrrr}、结果 0x{lr:X}、{(nA < 65536 ? "是" : "否")}");
                }

                // 确定性档 GetWritableRegion（10-20 普查高风险：按区域长度建托管副本）：开确定性档取 32 MB 区域 → 改 → 释放写回；
                //   取区域额外托管分配 < 64 KB、写回到游戏内存、写回走 s2a_det_host_write（按序记一条 32 MB）
                {
                    const int Big = 32 << 20;
                    byte* dbuf = FakeNative.Alloc(Big);
                    new Span<byte>(dbuf, Big).Fill(7);
                    bool detWas = GuestMemory.Det; int onWas = FakeNative.DetOn;
                    FakeNative.DetOn = 1; GuestMemory.Det = true; int log0 = FakeNative.DetLog.Count;
                    GuestMemory detGm = new();
                    long d0 = GC.GetAllocatedBytesForCurrentThread();
                    WritableRegion detWr = detGm.GetWritableRegion((ulong)dbuf, Big);
                    long dA = GC.GetAllocatedBytesForCurrentThread() - d0;
                    detWr.Memory.Span[123] = 42; detWr.Memory.Span[Big - 1] = 43;
                    bool notYet = dbuf[123] == 7;
                    detWr.Dispose();
                    bool back = dbuf[123] == 42 && dbuf[Big - 1] == 43;
                    bool logged = FakeNative.DetLog.Count == log0 + 1 && FakeNative.DetLog[^1].Len == (ulong)Big;
                    GuestMemory.Det = detWas; FakeNative.DetOn = onWas;
                    Check($"确定性档 32 MB 区域：取区域额外托管分配 < 64 KB（{dA >> 10} KB）、释放前游戏内存未改、释放后写回、写回经 det 记录", "是 / 是 / 是 / 是",
                        $"{(dA < 65536 ? "是" : "否")} / {(notYet ? "是" : "否")} / {(back ? "是" : "否")} / {(logged ? "是" : "否")}");
                }

                // 截图服务（10-20 普查中风险：GetSpan(...).ToArray() 把游戏截图缓冲 ≥ 3.6 MB 拷一份）：caps:su SaveScreenShotEx0 → 请求额外托管分配 < 64 KB
                {
                    const int Shot = 1280 * 720 * 4;
                    Reply gcs = Call(sm, Msg.Cmif(1, Name8("caps:su")));
                    uint capsu = gcs.Move.Count == 1 ? gcs.Move[0] : 0;
                    byte* shot = FakeNative.Alloc(Shot);
                    for (int i = 0; i < Shot; i++) shot[i] = (byte)i;
                    (long real, long svc, uint r1, uint r2) = CapsSaveAlloc(capsu, shot, Shot);
                    Check($"截图 SaveScreenShotEx0（3.6 MB）：真编码器整条路径无 ≥ 截图大小的托管分配（{real >> 10} KB）、固定编码器下服务路径额外托管分配 < 64 KB（{svc >> 10} KB）",
                        "结果 0 / 0、是 / 是", $"结果 {r1} / {r2}、{(real < Shot ? "是" : "否")} / {(svc < 65536 ? "是" : "否")}");
                }

                // 服务处理请求中途调别的服务（prepo 存报告时经 sm 连 arp:r 查应用实例号）：游戏线程上直调，嵌套请求走同一块消息缓冲
                // （动森 Mac 局：「Horizon 服务在没有 TLS 的线程上发请求」→ arp 初始化 AbortOnFailure）
                red0 = Red.Total;
                Reply gp = Call(sm, Msg.Cmif(1, Name8("prepo:u")));
                uint prepo = gp.Move.Count == 1 ? gp.Move[0] : 0;
                byte* room = _game + 0x3000;
                byte* report = _game + 0x3100;
                Encoding.ASCII.GetBytes("s2a_test").CopyTo(new Span<byte>(room, 8));
                report[0] = 0x80;   // MessagePack 空表
                Msg sr = Msg.Cmif(10104);
                sr.SendPid = true;
                sr.X.Add(((ulong)room, 8));
                sr.A.Add(((ulong)report, 1));
                Reply srr = Call(prepo, sr);
                Check("prepo:u SaveReportOld（处理中经 sm 连 arp:r）", "结果 0、报红 +0", $"结果 {srr.Result}、报红 +{Red.Total - red0}");

                // prepo 大报告（10-20 普查中风险：ToArray + MessagePack 对象树都随报告长度）：8 MB 报告 → 超 64 KB 只记长度不解析，请求额外托管分配 < 64 KB
                {
                    const int BigRep = 8 << 20;
                    byte* bigRep = FakeNative.Alloc(BigRep);
                    new Span<byte>(bigRep, BigRep).Clear(); bigRep[0] = 0x80;
                    Msg sr2 = Msg.Cmif(10104); sr2.SendPid = true; sr2.X.Add(((ulong)room, 8)); sr2.A.Add(((ulong)bigRep, (ulong)BigRep));
                    long p0 = GC.GetAllocatedBytesForCurrentThread();
                    Reply sr2r = Call(prepo, sr2);
                    long pA = GC.GetAllocatedBytesForCurrentThread() - p0;
                    Check($"prepo 8 MB 报告：额外托管分配 < 64 KB（{pA >> 10} KB）", "结果 0、是", $"结果 {sr2r.Result}、{(pA < 65536 ? "是" : "否")}");
                }

                // prepo 64 KB 以内的报告不解析不排版（帧率能耗线 10-24）：原先每份报告解成 MessagePack 对象树、整份排成缩进 JSON（字节数组每字节「0x.., 」6 字符）、
                //   经四五次整串拷贝写日志，再每份起一条线程发给没人订阅的事件。真机马车8 读盘段第 412–420 帧一批报告产 69 MB 字符串，正好夹着一次 28 ms gen1。
                //   手算期望：32 KB 报告（表里一个 32 KB 字节数组）整进程额外托管分配 < 64 KB（原先约为报告的几十倍）
                {
                    const int MidRep = 32 << 10;
                    byte* midRep = FakeNative.Alloc(MidRep + 16);
                    new Span<byte>(midRep, MidRep + 16).Clear();
                    midRep[0] = 0x81; midRep[1] = 0xa1; midRep[2] = (byte)'d'; midRep[3] = 0xc6; midRep[4] = 0; midRep[5] = 0; midRep[6] = (byte)((MidRep >> 8) & 0xff); midRep[7] = (byte)(MidRep & 0xff);   // {"d": bin32[32768]}
                    Msg sr3 = Msg.Cmif(10104); sr3.SendPid = true; sr3.X.Add(((ulong)room, 8)); sr3.A.Add(((ulong)midRep, (ulong)(MidRep + 8)));
                    long q0 = GC.GetTotalAllocatedBytes(true);
                    Reply sr3r = Call(prepo, sr3);
                    long qA = GC.GetTotalAllocatedBytes(true) - q0;
                    Check("prepo 32 KB 报告：不解析不排版，整进程额外托管分配 < 64 KB", "结果 0、是", $"结果 {sr3r.Result}、{(qA < 65536 ? "是" : $"否（{qA >> 10} KB）")}");
                }

                // 带存档起局（S2A_SAVE_DIR/device/）：拷入后经 LibHac 挂回读逐字节相同；再播一份不同的，旧文件被清掉（每局独立，不读上局存档）
                {
                    string sd = Path.Combine(Path.GetTempPath(), $"s2a_save_test_{Environment.ProcessId}");
                    string devDir = Path.Combine(sd, "device");
                    Directory.CreateDirectory(Path.Combine(devDir, "Villager0"));
                    byte[] main = new byte[300_000];
                    new Random(7).NextBytes(main);
                    File.WriteAllBytes(Path.Combine(devDir, "main.dat"), main);
                    File.WriteAllBytes(Path.Combine(devDir, "Villager0", "personal.dat"), [1, 2, 3]);
                    Environment.SetEnvironmentVariable("S2A_SAVE_DIR", sd);
                    red0 = Red.Total;
                    Saves.Seed(Host.Device, Application.ProgramId);
                    byte[] back = ReadDeviceSave("/main.dat");
                    byte[] back2 = ReadDeviceSave("/Villager0/personal.dat");
                    File.Delete(Path.Combine(devDir, "main.dat"));
                    Directory.Delete(Path.Combine(devDir, "Villager0"), true);
                    File.WriteAllBytes(Path.Combine(devDir, "other.dat"), [9]);
                    Saves.Seed(Host.Device, Application.ProgramId);
                    bool oldGone = ReadDeviceSave("/main.dat") == null;
                    byte[] other = ReadDeviceSave("/other.dat");
                    Check("带存档起局 device/：拷入回读、重播清旧", "main.dat 30 万字节一致、子目录文件 [1,2,3]、重播后 main.dat 不在、other.dat [9]、报红 +0",
                        $"main.dat {(back != null && back.AsSpan().SequenceEqual(main) ? "30 万字节一致" : "不一致")}、子目录文件 {(back2 == null ? "缺" : "[" + string.Join(",", back2) + "]")}、重播后 main.dat {(oldGone ? "不在" : "还在")}、other.dat {(other == null ? "缺" : "[" + string.Join(",", other) + "]")}、报红 +{Red.Total - red0}");

                    // 带存档起局按块拷入 + 链接拒收（审计 P2-176 / P2-194，11-09）：先播一份 250 万字节（跨 3 个 1 MB 块）→ 回读一致；
                    //   再在种子里放一个指向种子外文件的软链重播 → Seed 抛 InvalidOperationException（本次起局失败）、链接目标没进存档、
                    //   已提交的旧档原样（big.dat 仍一致）。旧代码：链接目标 [7,7,7] 被读进存档、不抛
                    {
                        byte[] big = new byte[2_500_000];
                        new Random(13).NextBytes(big);
                        string bigPath = Path.Combine(devDir, "big.dat"), linkPath = Path.Combine(devDir, "link.dat");
                        string outside = Path.Combine(Path.GetTempPath(), $"s2a_seed_outside_{Environment.ProcessId}");
                        File.WriteAllBytes(bigPath, big);
                        string e1 = "无", e2 = "无";
                        try { Saves.Seed(Host.Device, Application.ProgramId); } catch (Exception e) { e1 = e.GetType().Name; }
                        byte[] bigBack = ReadDeviceSave("/big.dat");
                        File.WriteAllBytes(outside, [7, 7, 7]);
                        File.CreateSymbolicLink(linkPath, outside);
                        try { Saves.Seed(Host.Device, Application.ProgramId); } catch (Exception e) { e2 = e.GetType().Name; }
                        byte[] bigAfter = ReadDeviceSave("/big.dat");
                        bool linkIn = ReadDeviceSave("/link.dat") != null;
                        File.Delete(linkPath); File.Delete(outside); File.Delete(bigPath);
                        Check("带存档起局：按块拷入、种子含软链则起局失败且已提交档不动",
                            "首播 异常 无、big.dat 250 万字节一致；带链接重播 异常 InvalidOperationException、link.dat 不在、big.dat 仍一致",
                            $"首播 异常 {e1}、big.dat {(bigBack != null && bigBack.AsSpan().SequenceEqual(big) ? "250 万字节一致" : bigBack == null ? "缺" : "不一致")}；带链接重播 异常 {e2}、link.dat {(linkIn ? "在" : "不在")}、big.dat {(bigAfter != null && bigAfter.AsSpan().SequenceEqual(big) ? "仍一致" : bigAfter == null ? "缺" : "不一致")}");
                    }

                    // 带存档起局 account/：当前用户的账号存档（10-20 塞尔达：LibHac.Fs.SaveData 收 Uid 的 MountSaveData 是 throw NotImplementedException，S2A_Init 当场失败）
                    {
                        var uid = Host.Device.System.AccountManager.LastOpenedUser.UserId;
                        var fu = new LibHac.Fs.UserId((ulong)uid.High, (ulong)uid.Low);
                        LibHac.HorizonClient hc = Host.Device.System.LibHacHorizonManager.RyujinxClient;
                        LibHac.Result cr = LibHac.Fs.Shim.SaveDataManagement.CreateSaveData(hc.Fs, new LibHac.Ncm.ApplicationId(Application.ProgramId), fu, Application.ProgramId, 0x100000, 0x100000, LibHac.Fs.SaveDataFlags.None);
                        string accDir = Path.Combine(sd, "account");
                        Directory.CreateDirectory(Path.Combine(accDir, "0"));
                        byte[] opt = new byte[5000];
                        new Random(11).NextBytes(opt);
                        File.WriteAllBytes(Path.Combine(accDir, "0", "option.sav"), opt);
                        long redA = Red.Total;
                        string err = "无";
                        try { Saves.Seed(Host.Device, Application.ProgramId); } catch (Exception e) { err = e.GetType().Name; }
                        byte[] ab = err == "无" ? ReadDeviceSave("/0/option.sav", fu) : null;
                        Check("带存档起局 account/：当前用户账号存档拷入回读", "建档 成功或已存在、异常 无、0/option.sav 5000 字节一致、报红 +0",
                            $"建档 {(cr.IsSuccess() || LibHac.Fs.ResultFs.PathAlreadyExists.Includes(cr) ? "成功或已存在" : cr.ToStringWithName())}、异常 {err}、0/option.sav {(ab != null && ab.AsSpan().SequenceEqual(opt) ? "5000 字节一致" : ab == null ? "缺" : "不一致")}、报红 +{Red.Total - redA}");

                        // 存档点副本（录手柄每个存档点留一份，回放局带它起局）：此刻已提交的 device / 当前用户 account 存档整份拷出，布局同 S2A_SAVE_DIR，
                        //   期望 = 上面播进去的内容（device 只剩重播后的 other.dat [9]、account 0/option.sav 5000 字节）+ user 文件 = 当前用户
                        string snap = Path.Combine(sd, "snap.save1");
                        long redS = Red.Total;
                        int kinds = Saves.Snapshot(Host.Device, Application.ProgramId, snap);
                        byte[] sDev = File.Exists(Path.Combine(snap, "device", "other.dat")) ? File.ReadAllBytes(Path.Combine(snap, "device", "other.dat")) : null;
                        bool sDevOld = File.Exists(Path.Combine(snap, "device", "main.dat"));
                        byte[] sAcc = File.Exists(Path.Combine(snap, "account", "0", "option.sav")) ? File.ReadAllBytes(Path.Combine(snap, "account", "0", "option.sav")) : null;
                        string sUser = File.Exists(Path.Combine(snap, "user")) ? File.ReadAllText(Path.Combine(snap, "user")).Trim() : "缺";
                        Check("存档点副本：已提交存档整份拷出（布局同 S2A_SAVE_DIR）", $"2 类、device/other.dat [9]、无旧 main.dat、account/0/option.sav 5000 字节一致、user {uid}、报红 +0",
                            $"{kinds} 类、device/other.dat {(sDev == null ? "缺" : "[" + string.Join(",", sDev) + "]")}、{(sDevOld ? "有" : "无")}旧 main.dat、account/0/option.sav {(sAcc != null && sAcc.AsSpan().SequenceEqual(opt) ? "5000 字节一致" : sAcc == null ? "缺" : "不一致")}、user {sUser}、报红 +{Red.Total - redS}");

                        // 存档点副本中途失败（审计 #859）：已提交副本里 option.sav 不可读 → 拷到一半抛错；不许留半份 .saveN（padrec_cut 见目录就整棵拿去回放），临时目录也清掉
                        string optSrc = Directory.EnumerateFiles(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, "bis", "user", "save"), "option.sav", SearchOption.AllDirectories).FirstOrDefault();
                        string snapBad = Path.Combine(sd, "snap.save2");
                        long redB = Red.Total;
                        int kindsBad = -1;

                        if (optSrc != null)
                        {
                            UnixFileMode optMode = File.GetUnixFileMode(optSrc);
                            File.SetUnixFileMode(optSrc, UnixFileMode.None);

                            try { kindsBad = Saves.Snapshot(Host.Device, Application.ProgramId, snapBad); }
                            finally { File.SetUnixFileMode(optSrc, optMode); }
                        }

                        Check("存档点副本：中途拷贝失败不留半份目录", "找到源 option.sav、0 类、snap.save2 不在、snap.save2.tmp 不在、报红 +1",
                            $"{(optSrc == null ? "没找到" : "找到")}源 option.sav、{kindsBad} 类、snap.save2 {(Directory.Exists(snapBad) ? "在" : "不在")}、snap.save2.tmp {(Directory.Exists(snapBad + ".tmp") ? "在" : "不在")}、报红 +{Red.Total - redB}");
                        Directory.Delete(accDir, true);
                    }

                    // 用户号：存档目录 user 文件 → 当前用户（动森岛民按建档用户号认人）；非法值报红、用户不变
                    var accounts = Host.Device.System.AccountManager;
                    File.WriteAllText(Path.Combine(sd, "user"), "78626c000000000153574e4154495645\n");
                    long redU = Red.Total;
                    Saves.SelectUser(Host.Device);
                    string u1 = accounts.LastOpenedUser.UserId.ToString();
                    File.WriteAllText(Path.Combine(sd, "user"), "xyz");
                    Saves.SelectUser(Host.Device);
                    string u2 = accounts.LastOpenedUser.UserId.ToString();
                    long redU2 = Red.Total - redU;
                    accounts.OpenUser(Ryujinx.HLE.HOS.Services.Account.Acc.AccountManager.DefaultUserId);
                    Check("带存档起局 user 文件：当前用户 / 非法值", "78626c000000000153574e4154495645 / 不变、报红 +1",
                        $"{u1} / {(u2 == u1 ? "不变" : u2)}、报红 +{redU2}");

                    Environment.SetEnvironmentVariable("S2A_SAVE_DIR", null);
                    Directory.Delete(sd, true);

                    // 存档位置 SaveData（11-09，SaveFolder.cs）：识别（规则同 pack/jobs/save_formats.py）、起局读入、退出写回。期望按规则手写
                    {
                        string pid = Application.ProgramId.ToString("x16");
                        string t = Path.Combine(Path.GetTempPath(), $"s2a_savedata_{Environment.ProcessId}");
                        if (Directory.Exists(t)) Directory.Delete(t, true);
                        string D(string name, params (string rel, byte[] data)[] files)
                        {
                            string d = Path.Combine(t, name);
                            foreach (var (rel, data) in files) { string f = Path.Combine(d, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllBytes(f, data); }
                            return d;
                        }
                        string K(string root, System.Collections.Generic.List<(string kind, string dir)> r, string why) =>
                            r == null ? $"无（{why}）" : string.Join("+", r.Select(x => x.kind + ":" + (Path.GetRelativePath(root, x.dir) is var rel && rel == "." ? "." : rel)));
                        var own = new System.Collections.Generic.HashSet<string> { "Saved Games" };
                        string w;
                        string sd_a1 = D("a", ("save/x.bin", [1]), ("save_device/y.bin", [2]));
                        string sd_b1 = D("b", ($"0x{pid} Game/2026-01-01/x.bin", [1]), ($"0x{pid} Game/2026-02-01/x.bin", [2]));
                        Directory.SetLastWriteTimeUtc(Path.Combine(sd_b1, $"0x{pid} Game", "2026-01-01"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                        Directory.SetLastWriteTimeUtc(Path.Combine(sd_b1, $"0x{pid} Game", "2026-02-01"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
                        string sd_c1 = D("c", ("JKSV/Game/P1 - 2026.01.01 @ 10.00.00/x.bin", [1]), ("JKSV/Game/P1 - 2026.02.01 @ 10.00.00/x.bin", [2]));
                        Directory.SetLastWriteTimeUtc(Path.Combine(sd_c1, "JKSV/Game/P1 - 2026.01.01 @ 10.00.00"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                        Directory.SetLastWriteTimeUtc(Path.Combine(sd_c1, "JKSV/Game/P1 - 2026.02.01 @ 10.00.00"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
                        string sd_r1 = D("r", ("bis/user/save/0000000000000005/0/x.bin", [1]));
                        byte[] meta = new byte[0x40]; BitConverter.GetBytes(Application.ProgramId).CopyTo(meta, 0); meta[0x20] = 3;
                        File.WriteAllBytes(Path.Combine(sd_r1, "bis/user/save/0000000000000005/ExtraData0"), meta);
                        string sd_e1 = D("e", ("0x0100000000001000 Other/2026/x.bin", [1]));
                        string sd_f1 = D("f", ("MySave/data.bin", [1]));
                        string sd_g1 = D("g", ("cheats/a.txt", [1]));
                        string sd_h1 = D("h", ("Saved Games/data.bin", [1]));
                        string got = string.Join(" / ", new[] { sd_a1, sd_b1, sd_c1, sd_r1, sd_e1, sd_f1, sd_g1, sd_h1 }.Select(d => K(d, SaveFolder.Detect(d, Application.ProgramId, own, out w), w)));
                        Check("存档位置识别：固定结构 / Checkpoint 取最新 / JKSV 取最新 / Ryujinx / 别款 / 外面套一层 / 金手指 / 游戏自己的顶层目录",
                            $"save:save+device:save_device / save:0x{pid} Game/2026-02-01 / save:JKSV/Game/P1 - 2026.02.01 @ 10.00.00 / device:bis/user/save/0000000000000005/0 / 无（这是另一款游戏的存档（程序号 0100000000001000）） / save:MySave / 无（放进来的是金手指（cheats），不是存档） / save:.",
                            got);

                        // 起局读入 → 退出写回：放 save/（账号）+ save_device/（设备）→ 读入后两份存档是放进来的内容；写回后存档位置是固定结构、内容一致；
                        //   再起局（摘要和写回时一样）不读（当前存档改成 [9] 后仍是 [9]）；局内玩家动过存档位置 → 退出不写回（玩家放的文件还在）
                        string folder = Path.Combine(t, "SaveData");
                        string state = Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, SaveFolder.StateFile);
                        File.Delete(state);
                        Environment.SetEnvironmentVariable("S2A_SAVE_FOLDER", folder);
                        var uidF = Host.Device.System.AccountManager.LastOpenedUser.UserId;
                        var fuF = new LibHac.Fs.UserId((ulong)uidF.High, (ulong)uidF.Low);
                        byte[] prog = new byte[3000]; new Random(5).NextBytes(prog);
                        D("SaveData", ("save/0/progress.sav", prog), ("save_device/dev.dat", [5, 6]));
                        long redF = Red.Total;
                        SaveFolder.ReadIn(Host.Device, Application.ProgramId);
                        byte[] accIn = ReadDeviceSave("/0/progress.sav", fuF), devIn = ReadDeviceSave("/dev.dat");
                        SaveFolder.WriteBack(Host.Device);
                        byte[] accOut = File.Exists(Path.Combine(folder, "save/0/progress.sav")) ? File.ReadAllBytes(Path.Combine(folder, "save/0/progress.sav")) : null;
                        byte[] devOut = File.Exists(Path.Combine(folder, "save_device/dev.dat")) ? File.ReadAllBytes(Path.Combine(folder, "save_device/dev.dat")) : null;
                        string nine = D("nine", ("dev.dat", [9]));
                        Saves.SeedFrom(Host.Device, Application.ProgramId, [("device", nine)]);
                        SaveFolder.ReadIn(Host.Device, Application.ProgramId);
                        byte[] devKept = ReadDeviceSave("/dev.dat");
                        File.WriteAllBytes(Path.Combine(folder, "player.bin"), [7]);   // 局内玩家往存档位置里放了东西
                        SaveFolder.WriteBack(Host.Device);
                        bool playerKept = File.Exists(Path.Combine(folder, "player.bin"));
                        byte[] devStill = File.Exists(Path.Combine(folder, "save_device/dev.dat")) ? File.ReadAllBytes(Path.Combine(folder, "save_device/dev.dat")) : null;
                        Check("存档位置：起局读入 / 退出写回 / 没动不读 / 局内动过不写回",
                            "账号 3000 字节一致、设备 [5,6]；写回 save/0/progress.sav 一致、save_device/dev.dat [5,6]；再起局设备仍 [9]；玩家文件在、save_device/dev.dat 仍 [5,6]、报红 +0",
                            $"账号 {(accIn != null && accIn.AsSpan().SequenceEqual(prog) ? "3000 字节一致" : accIn == null ? "缺" : "不一致")}、设备 {(devIn == null ? "缺" : "[" + string.Join(",", devIn) + "]")}；" +
                            $"写回 save/0/progress.sav {(accOut != null && accOut.AsSpan().SequenceEqual(prog) ? "一致" : accOut == null ? "缺" : "不一致")}、save_device/dev.dat {(devOut == null ? "缺" : "[" + string.Join(",", devOut) + "]")}；" +
                            $"再起局设备仍 {(devKept == null ? "缺" : "[" + string.Join(",", devKept) + "]")}；玩家文件{(playerKept ? "在" : "不在")}、save_device/dev.dat 仍 {(devStill == null ? "缺" : "[" + string.Join(",", devStill) + "]")}、报红 +{Red.Total - redF}");
                        // 放进来的认不出（只有金手指）：不读、写原因；退出不写回，玩家放的原样留着（11-09 Mac 实测发现：原先退出时被当前存档盖掉）
                        Directory.Delete(folder, true);
                        D("SaveData", ("cheats/a.txt", [1]));
                        long redK = Red.Total;
                        SaveFolder.ReadIn(Host.Device, Application.ProgramId);
                        string problem = File.Exists(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, SaveFolder.ProblemFile)) ? File.ReadAllText(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, SaveFolder.ProblemFile)) : "无";
                        SaveFolder.WriteBack(Host.Device);
                        Check("存档位置：认不出 → 不读、写原因、退出不写回", "原因 放进来的是金手指（cheats），不是存档、cheats/a.txt 还在、没有 save/、报红 +1",
                            $"原因 {problem}、cheats/a.txt {(File.Exists(Path.Combine(folder, "cheats/a.txt")) ? "还在" : "没了")}、{(Directory.Exists(Path.Combine(folder, "save")) ? "有" : "没有")} save/、报红 +{Red.Total - redK}");
                        File.Delete(Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, SaveFolder.ProblemFile));
                        Environment.SetEnvironmentVariable("S2A_SAVE_FOLDER", null);
                        File.Delete(state);
                        Directory.Delete(t, true);
                    }
                }
            }
            else
            {
                CheckUncovered("动森登记 + fs 读 RomFS", $"未覆盖（没有 {game}/romfs，玩家自己的游戏数据）");
            }

            // ---- 系统数据按号（fsp-srv OpenDataStorageByDataId）：马车8 产物根的 0100000000000802 Mii 资源 ----
            string sysRoot = Environment.GetEnvironmentVariable("S2A_TEST_SYSTEMDATA") ?? Path.Combine(Environment.GetEnvironmentVariable("S2A_RUN_ROOT") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".s2arm/run"), "s2arm/roots/mk8d@现行/systemdata");

            if (Directory.Exists(Path.Combine(sysRoot, "0100000000000802")))
            {
                Ryujinx.HLE.S2AHooks.SystemDataRoot = sysRoot;
                Reply gf2 = Call(sm, Msg.Cmif(1, Name8("fsp-srv")));
                uint fsp2 = gf2.Move.Count == 1 ? gf2.Move[0] : 0;
                Msg scp2 = Msg.Cmif(1, Bytes(0UL)); scp2.SendPid = true; Call(fsp2, scp2);
                Reply odd = Call(fsp2, Msg.Cmif(202, Bytes(0UL, 0x0100000000000802UL)));   // storageId（u8 + 7 填充）、dataId
                uint dst = odd.Move.Count == 1 ? odd.Move[0] : 0;
                string got = $"结果 {odd.Result}";

                if (dst != 0)
                {
                    byte* hd = _game + 0x1800;
                    Msg rd2 = Msg.Cmif(0, Bytes(0UL, 0x50UL)); rd2.B.Add(((ulong)hd, 0x50));
                    Reply r2 = Call(dst, rd2);
                    got += $"、读 {r2.Result}、头长 0x{*(ulong*)hd:X}";
                }

                Check("fsp-srv OpenDataStorageByDataId(0100000000000802 Mii 资源，目录形式)", "结果 0、读 0、头长 0x50", got);
            }
            else
            {
                CheckUncovered("系统数据按号", $"未覆盖（没有 {sysRoot}/0100000000000802，玩家自己的游戏数据）");
            }

            // ---- 共享字体（pl:u）：本机旧库字体数据（S2A_SHARED_FONT_DIR，不进仓库；没有就记未测）----
            if (Ryujinx.HLE.S2AHooks.SharedFontDir != null)
            {
                Reply gp = Call(sm, Msg.Cmif(1, Name8("pl:u")));
                uint pl = gp.Move.Count == 1 ? gp.Move[0] : 0;
                Reply fs0 = Call(pl, Msg.Cmif(2, Bytes(0u)));   // GetSize(JapanUsEurope)
                long stdSize = new FileInfo(Path.Combine(Ryujinx.HLE.S2AHooks.SharedFontDir, "nintendo_udsg-r_std_003.bfttf")).Length - 8;
                Check("pl GetSize(标准字体)（期望 = .bfttf 长度 − 8 字节头）", $"0、{stdSize}", $"{fs0.Result}、{*(int*)fs0.Data}");
            }
            else
            {
                Check("pl 共享字体", "已测", "未测（没有字体目录）");
            }

            // ---- 时间：时区系统数据（S2A_TZDATA，test.sh 由宿主 IANA 库生成）、设备时区跟宿主、日历换算不回零 ----
            Reply gt = Call(sm, Msg.Cmif(1, Name8("time:u")));
            uint tu = gt.Move.Count == 1 ? gt.Move[0] : 0;
            Reply gtz = Call(tu, Msg.Cmif(3));
            uint tzs = gtz.Move.Count == 1 ? gtz.Move[0] : 0;
            Reply gln = Call(tzs, Msg.Cmif(0));
            string loc = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(gln.Data, 0x24)).TrimEnd('\0');
            Check("time GetDeviceLocationName（有时区数据时跟宿主）", TimeZoneInfo.Local.Id, loc);
            Reply ctr = Call(tzs, Msg.Cmif(101, Bytes(1700000000L)));
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(loc.Length > 0 ? loc : "UTC"));
            Check("time ToCalendarTimeWithMyRule(1700000000)（期望值由 .NET 时区库独立换算）", $"0、{local:yyyy-MM-dd HH:mm:ss}",
                $"{ctr.Result}、{*(short*)ctr.Data:D4}-{((sbyte*)ctr.Data)[2]:D2}-{((sbyte*)ctr.Data)[3]:D2} {((sbyte*)ctr.Data)[4]:D2}:{((sbyte*)ctr.Data)[5]:D2}:{((sbyte*)ctr.Data)[6]:D2}");

            // ---- VP8（暗黑2 片头，Nvdec → libvpx）：libvpx 官方测试向量逐帧解码，按 IVF 帧头还原 PictureInfo（同 Nvdec 只给压缩数据），逐帧比 I420 的 MD5 ----
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "data");
                byte[] ivf = File.ReadAllBytes(Path.Combine(dir, "vp80-00-comprehensive-001.ivf"));
                string[] want = Array.ConvertAll(File.ReadAllLines(Path.Combine(dir, "vp80-00-comprehensive-001.ivf.md5")), l => l.Split(' ')[0]);
                using var dec = new Ryujinx.Graphics.Nvdec.Vpx.VpxVp8Decoder();
                int pos = BitConverter.ToUInt16(ivf, 6), frames = 0, same = 0;
                ushort w = 0, h = 0;
                using var md5 = System.Security.Cryptography.MD5.Create();
                while (pos + 12 <= ivf.Length)
                {
                    int size = BitConverter.ToInt32(ivf, pos);
                    ReadOnlySpan<byte> f = ivf.AsSpan(pos + 12, size);
                    pos += 12 + size;
                    bool key = (f[0] & 1) == 0;
                    if (key) { w = (ushort)((f[6] | f[7] << 8) & 0x3fff); h = (ushort)((f[8] | f[9] << 8) & 0x3fff); }
                    var pi = new Ryujinx.Graphics.Video.Vp8PictureInfo { KeyFrame = key, Version = (uint)(f[0] >> 1) & 7, FirstPartSize = (uint)(f[0] | f[1] << 8 | f[2] << 16) >> 5, FrameWidth = w, FrameHeight = h };
                    using var surf = dec.CreateSurface(w, h);
                    if (!dec.Decode(ref pi, surf, f[(key ? 10 : 3)..])) break;
                    byte[] img = new byte[w * h + 2 * surf.UvWidth * surf.UvHeight];
                    int o = 0;
                    void Rows(Ryujinx.Graphics.Video.Plane p, int stride, int rw, int rh) { for (int r = 0; r < rh; r++) { new ReadOnlySpan<byte>((byte*)p.Pointer + r * stride, rw).CopyTo(img.AsSpan(o)); o += rw; } }
                    Rows(surf.YPlane, surf.Stride, w, h);
                    Rows(surf.UPlane, surf.UvStride, surf.UvWidth, surf.UvHeight);
                    Rows(surf.VPlane, surf.UvStride, surf.UvWidth, surf.UvHeight);
                    if (frames < want.Length && Convert.ToHexString(md5.ComputeHash(img)).ToLowerInvariant() == want[frames]) same++;
                    frames++;
                }
                Check("VP8 解码（libvpx）：官方测试向量逐帧 MD5", $"解出 {want.Length} 帧、MD5 对 {want.Length} 帧", $"解出 {frames} 帧、MD5 对 {same} 帧");
            }

            // ---- 硬件 Opus 解码 hwopus（libopus）：单流 / 多流各采样率声道与直调 libopus 逐样本比对、错误码、稳态零分配（HwOpusTest.cs）----
            TestHwOpus(sm);

            // ---- 宽屏第一层：游戏查到的显示分辨率按屏幕宽高比（iPhone 17 Pro Max 2346:1080 → 掌机 1564x720）----
            var oldScreenW = Native.ScreenW; var oldScreenH = Native.ScreenH;
            Native.ScreenW = &TestScreenWidth; Native.ScreenH = &TestScreenHeight;
            Exports.ApplyDisplayAspect(true);
            Check("4:3 屏幕按 1080 基准设置游戏宽高比", 1280UL, Ryujinx.HLE.S2ADisplay.Width(720));
            // 11-05 高屏加长：屏 1920×1440（4:3，iPad）→ 掌机 720 × 1440/1080 = 960、主机 1080 → 1440；宽不变
            Check("4:3 屏幕高按 屏高 ÷ 1080 加长（掌机 / 主机）", "960 1440", $"{Ryujinx.HLE.S2ADisplay.Height(720)} {Ryujinx.HLE.S2ADisplay.Height(1080)}");
            Native.ScreenH = &TestScreenMissing;
            bool missingRejected = false;
            try { Exports.ApplyDisplayAspect(true); } catch (InvalidOperationException) { missingRejected = true; }
            Check("缺屏高不按 1080 兜底", true, missingRejected);
            Native.ScreenW = oldScreenW; Native.ScreenH = oldScreenH; Ryujinx.HLE.S2ADisplay.ScaleY = 1.0;

            Ryujinx.HLE.S2ADisplay.Aspect = 2346.0 / 1080.0;
            Reply gvi = Call(sm, Msg.Cmif(1, Name8("vi:u")));
            uint viu = gvi.Move.Count == 1 ? gvi.Move[0] : 0;
            Reply gds = Call(viu, Msg.Cmif(0, Bytes(0u)));
            uint ids = gds.Move.Count == 1 ? gds.Move[0] : 0;
            Reply gdr = Call(ids, Msg.Cmif(1102, Bytes(0UL)));
            ulong dw = *(ulong*)gdr.Data, dh = *(ulong*)(gdr.Data + 8);
            Check("vi GetDisplayResolution（屏幕 2346:1080）", "0、1564x720", $"{gdr.Result}、{dw}x{dh}");
            Ryujinx.HLE.S2ADisplay.Aspect = 16.0 / 9.0;
            Reply gdr2 = Call(ids, Msg.Cmif(1102, Bytes(0UL)));
            Check("vi GetDisplayResolution（缺省 16:9 = 上游原值）", "1280x720", $"{*(ulong*)gdr2.Data}x{*(ulong*)(gdr2.Data + 8)}");
            // Duo 内屏 1920×1350（1.422）：宽 1280 不变，高 720 × 1350/1080 = 900（与宽屏核心加高 720 → 900 同值）
            Ryujinx.HLE.S2ADisplay.ScaleY = 1350.0 / 1080.0;
            Reply gdr3 = Call(ids, Msg.Cmif(1102, Bytes(0UL)));
            Check("vi GetDisplayResolution（Duo 1920×1350 加长）", "1280x900", $"{*(ulong*)gdr3.Data}x{*(ulong*)(gdr3.Data + 8)}");
            Ryujinx.HLE.S2ADisplay.ScaleY = 1.0;
            Reply gdr4 = Call(ids, Msg.Cmif(1102, Bytes(0UL)));
            Check("vi GetDisplayResolution（ScaleY 复位 = 原 720 高）", "1280x720", $"{*(ulong*)gdr4.Data}x{*(ulong*)(gdr4.Data + 8)}");

            // ---- vi 中继（IHOSBinderDriver）：游戏每帧 DequeueBuffer + QueueBuffer 各一次 TransactParcel ----
            {
                var bfa = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                uint hos = Call(ids, Msg.Cmif(100)).Move[0];
                byte[] dn = new byte[0x40]; Encoding.ASCII.GetBytes("Default").CopyTo(dn, 0);
                long displayId = *(long*)Call(ids, Msg.Cmif(1010, dn)).Data;
                byte* lp = _game + 0x4000;
                Msg csl = Msg.Cmif(2030, Bytes(0L, displayId)); csl.B.Add(((ulong)lp, 0x100));
                long layerId = *(long*)Call(ids, csl).Data;
                int binderId = (int)*(long*)(lp + *(uint*)(lp + 4) + 8); // 出 Parcel：负载里的 FlatBinderObject { Type, Flags, BinderId }
                // 夹具当消费方（合成线程没有可用的显存句柄，停掉它，由 between 代为取帧 / 还帧，同 SurfaceFlinger 合成）
                object sf = Host.Device.System.GetType().GetProperty("SurfaceFlinger", bfa).GetValue(Host.Device.System);
                sf.GetType().GetProperty("RenderLayerId").GetSetMethod(true).Invoke(sf, [0L]);
                object layer = ((System.Collections.IDictionary)sf.GetType().GetField("_layers", bfa).GetValue(sf))[layerId];
                object consumer = layer.GetType().GetField("Consumer").GetValue(layer);
                var acquire = consumer.GetType().GetMethod("AcquireBuffer");
                var release = consumer.GetType().GetMethod("ReleaseBuffer");
                object noFence = Activator.CreateInstance(release.GetParameters()[1].ParameterType.GetElementType());

                // 输入 Parcel：头 16 字节 { 负载长, 负载偏移 0x10, 对象长 0, 对象偏移 } + 接口名（策略、长度、UTF-16 含结尾 0，4 对齐）+ 负载
                int WriteParcel(byte* p, byte[] payload)
                {
                    MemoryStream ms = new(); BinaryWriter w = new(ms);
                    const string token = "android.gui.IGraphicBufferProducer";
                    w.Write(0x100); w.Write(token.Length); w.Write(Encoding.Unicode.GetBytes(token + "\0"));
                    while (ms.Length % 4 != 0) w.Write((byte)0);
                    w.Write(payload);
                    byte[] body = ms.ToArray();
                    *(uint*)p = (uint)body.Length; *(uint*)(p + 4) = 0x10; *(uint*)(p + 8) = 0; *(uint*)(p + 12) = 0x10 + (uint)body.Length;
                    body.CopyTo(new Span<byte>(p + 0x10, body.Length));
                    return 0x10 + body.Length;
                }
                byte* rp = _game + 0x4800;
                Msg Transact(uint code, byte* p, int len)
                {
                    Msg m = Msg.Cmif(0, Bytes(binderId, code, 0u)); m.A.Add(((ulong)p, (ulong)len)); m.B.Add(((ulong)rp, 0x200));
                    return m;
                }
                int RepInt(int index) => *(int*)(rp + *(uint*)(rp + 4) + index * 4); // 应答负载第 index 个 int

                byte* cp = _game + 0x4100;
                Call(hos, Transact(10, cp, WriteParcel(cp, Bytes(0, 1, 0)))); // Connect(无监听, NVN, 非应用控制)
                int connectStatus = RepInt(4); // QueueBufferOutput 16 字节 { 宽, 高, 变换提示, 待显数 } 之后是状态
                // 三块预分配 64x64 RGBA8888：GraphicBuffer = 头 0x28 { Magic, W, H, Stride, Format, Usage = 0xB00, Pid, RefCount, Fds, Ints = 0x144 / 4 } + NvGraphicBuffer 0x144
                int preStatus = 0;
                for (int s = 0; s < 3; s++)
                {
                    byte[] gb = new byte[0x16C];
                    BitConverter.GetBytes(64).CopyTo(gb, 4); BitConverter.GetBytes(64).CopyTo(gb, 8); BitConverter.GetBytes(64).CopyTo(gb, 12);
                    BitConverter.GetBytes(1).CopyTo(gb, 16); BitConverter.GetBytes(0xB00).CopyTo(gb, 20); BitConverter.GetBytes(0x144 / 4).CopyTo(gb, 0x24);
                    Call(hos, Transact(14, cp, WriteParcel(cp, [.. Bytes(s, 1, 0x16CL), .. gb])));
                    preStatus |= RepInt(0);
                }
                byte* dp = _game + 0x4200, qpp = _game + 0x4300;
                int dLen = WriteParcel(dp, Bytes(0, 0, 0, 1, 0xB00)); // DequeueBuffer(同步, 0x0 = 缺省尺寸, RGBA8888, 用途 = 预分配头里的用途)
                // QueueBuffer(槽, QueueBufferInput 0x54 = { 时间戳, 自动, 裁剪 0,0,64,64, 缩放, 变换, 粘滞, 异步, 交换间隔 1, 空围栏 })
                byte[] qin = [.. Bytes(0, 0x54L, 0L, 0, 0, 0, 64, 64, 0, 0, 0, 0, 1), .. new byte[0x24]];
                int qLen = WriteParcel(qpp, qin);
                int slotOff = 0x10 + (qLen - 0x10 - qin.Length); // 负载里槽号的偏移
                Msg deq = Transact(3, dp, dLen), que = Transact(7, qpp, qLen);
                object[] acq = [null, 0UL, false];
                void FrameCycle()
                {
                    // 上一帧：消费方取走并归还（同合成线程）
                    if (Convert.ToInt32(acquire.Invoke(consumer, acq)) == 0) release.Invoke(consumer, [acq[0], noFence]);
                    *(int*)(qpp + slotOff) = RepInt(0); // 刚出队的槽号
                }
                // 预热：每个槽首次出队后要 RequestBuffer（游戏同样只做一次）
                int frameStatus = 0;
                for (int i = 0; i < 6; i++)
                {
                    Call(hos, deq);
                    int slot = RepInt(0);
                    FrameCycle();
                    byte* rq = _game + 0x4400;
                    Call(hos, Transact(1, rq, WriteParcel(rq, Bytes(slot))));
                    Call(hos, que);
                    frameStatus |= RepInt(4);
                }
                Check("vi 中继 Connect / SetPreallocatedBuffer ×3 / 出队-请求-入队 ×6（状态全 0）", "Connect 0、预分配 0、入队 0", $"Connect {connectStatus}、预分配 {preStatus}、入队 {frameStatus}");
                MeasureAllocPair("vi TransactParcel DequeueBuffer + QueueBuffer（一帧）", hos, deq, que, FrameCycle);
                // GetBufferHistory（10-26 巫师3 直连每帧查一次缓冲历史，原先每次 new BufferInfo[n]，248 B）：要 8 条 → 状态 0、条数 8（历史环 8 项），零分配
                byte* hp = _game + 0x4600;
                Msg hist = Transact(17, hp, WriteParcel(hp, Bytes(8)));
                Call(hos, hist);
                Check("vi GetBufferHistory 要 8 条：状态 / 条数", "0 / 8", $"{RepInt(0)} / {RepInt(1)}");
                MeasureAlloc("vi TransactParcel GetBufferHistory（巫师3 直连每帧一次）", hos, () => hist);
            }

            // ---- 域（nifm:u）：动森在 CreateGeneralService 之后崩；按 Horizon 域协议逐字节核 ----
            Reply gn = Call(sm, Msg.Cmif(1, Name8("nifm:u")));
            uint nif = gn.Move.Count == 1 ? gn.Move[0] : 0;
            Msg qp = Msg.Cmif(3); qp.Type = 5;
            Reply qpr = Call(nif, qp);
            int pbs = *(int*)qpr.Data;
            Msg cd = Msg.Cmif(0); cd.Type = 5;
            Reply cdr = Call(nif, cd);
            int rootId = *(int*)cdr.Data;
            Check("nifm 控制命令：QueryPointerBufferSize / ConvertCurrentObjectToDomain", "0x8000 / 对象号 1", $"0x{pbs:X} / 对象号 {rootId}");

            Msg cgs = Msg.Cmif(5, Bytes(0UL)); cgs.SendPid = true; cgs.DomainObject = rootId;
            cgs.Write(_tlr);
            Svc(0x21, FakeNative.CookieOf(nif));
            Reply cgr = Reply.ParseDomain(_tlr);
            int gsId = *(int*)cgr.Data; // 无数据，出对象号紧跟 SFCO 头
            Check("nifm 域内 CreateGeneralService", "结果 0、出对象 1 个、新对象号 > 1", $"结果 {cgr.Result}、出对象 {cgr.DomainOutCount} 个、新对象号 {(gsId > rootId ? "> 1" : gsId.ToString())}");

            byte* cid = _game + 0x2000;
            Msg gci = Msg.Cmif(1); gci.DomainObject = gsId; gci.C.Add(((ulong)cid, 0x10));
            gci.Write(_tlr);
            Svc(0x21, FakeNative.CookieOf(nif));
            Reply gcr = Reply.ParseDomain(_tlr);
            Check("IGeneralService GetClientId（C 缓冲，应答 X 描述符回告长度）", "结果 0、X 长度 4", $"结果 {gcr.Result}、X 长度 {Reply.FirstXSize(_tlr)}");

            // 无联网权限 = 真机离线：当前 IP（12）/ 连接状态（18）回 NoInternetConnection（补丁 0078；旧版回宿主 IP，暗黑2 去连战网认证失败、标题页死循环）
            {
                uint noNet = (uint)Ryujinx.HLE.HOS.Services.Nifm.ResultCode.NoInternetConnection;
                Msg ip = Msg.Cmif(12); ip.DomainObject = gsId; ip.Write(_tlr); Svc(0x21, FakeNative.CookieOf(nif)); Reply ipr = Reply.ParseDomain(_tlr);
                Msg cs = Msg.Cmif(18); cs.DomainObject = gsId; cs.Write(_tlr); Svc(0x21, FakeNative.CookieOf(nif)); Reply csr = Reply.ParseDomain(_tlr);
                Check("nifm 离线：GetCurrentIpAddress / GetInternetConnectionStatus", $"0x{noNet:X} / 0x{noNet:X}", $"0x{(uint)ipr.Result:X} / 0x{(uint)csr.Result:X}");
            }

            // ---- Horizon 域（friend:u）：暗黑3 每秒约 85 次在域里轮询通知 Pop（队列空）。域会话 → CreateNotificationService（域内出对象）→ Pop：
            //   结果 NotificationQueueEmpty（模块 121、描述 15 = 0x1E79）；每次 0 字节托管分配（原先每条域消息新建处理器 + 两个对象号数组，176 B）----
            {
                bool fr = WaitFor(() => Array.IndexOf(HorizonHost.Services, "friend:u") >= 0, 10_000);
                uint fs = fr ? Call(sm, Msg.Cmif(1, Name8("friend:u"))).Move[0] : 0;
                Msg fcd = Msg.Cmif(0); fcd.Type = 5;
                int froot = *(int*)Call(fs, fcd).Data;
                Msg cns = Msg.Cmif(1, Bytes(1UL, 2UL)); cns.DomainObject = froot;
                cns.Write(_tlr);
                Svc(0x21, FakeNative.CookieOf(fs));
                Reply cnr = Reply.ParseDomain(_tlr);
                int nsId = *(int*)cnr.Data;
                Msg pop = Msg.Cmif(2); pop.DomainObject = nsId;
                pop.Write(_tlr);
                Svc(0x21, FakeNative.CookieOf(fs));
                uint popRc = Reply.ParseDomain(_tlr).Result;
                // 出对象数不核：Horizon 框架不写域出头的对象数（上游如此，后续项），只核新对象号
                Check("friend:u 域内 CreateNotificationService → Pop（队列空）", "结果 0、新对象号 > 根对象号、Pop 0x1E79", $"结果 {cnr.Result}、新对象号 {(nsId > froot ? "> 根对象号" : nsId.ToString())}、Pop 0x{popRc:X}");

                ulong* fregs = stackalloc ulong[31];
                SvcCtx fctx = new() { Num = 0x21, Thread = 0x1234, Regs = fregs, Tlr = _tlr, Obj = FakeNative.CookieOf(fs), ObjKind = (uint)ObjKind.Session };
                long ftotal = 0;
                for (int i = 0; i < 1100; i++)
                {
                    pop.Write(_tlr);
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    FakeNative.Api->Svc(&fctx);
                    ftotal += i >= 100 ? GC.GetAllocatedBytesForCurrentThread() - a0 : 0;
                }
                _alloc.Add(("Horizon 域消息：friends 通知 Pop（队列空，域内对象）", ftotal / 1000.0));
                if (Environment.GetEnvironmentVariable("S2A_ALLOC_PROFILE") == "1")
                {
                    using AllocProfiler prof = new();
                    for (int i = 0; i < 200_000; i++) { pop.Write(_tlr); FakeNative.Api->Svc(&fctx); }
                    Console.Error.WriteLine($"分配来源 Horizon 域 Pop：{prof.Top(8)}");
                }
            }

            // ---- RomFS 大读不租中间数组（10-20 测试设备2 巫师3：一次大读在基类 Stream.Read(Span) 里租等长数组 OOM）：同 fs 数据存储路径
            //   镜像登记进 VirtualFileSystem → 每会话一个视图 → Read 32 MB；旧路径（镜像 → AsStream → AsStorage(true)）≥ 32 MB 作反证 ----
            {
                const int Big = 32 << 20;
                byte[] src = new byte[Big + 4096]; new Random(7).NextBytes(src);
                byte[] dst = new byte[Big];
                long Alloc(LibHac.Fs.IStorage sto) { long a0 = GC.GetAllocatedBytesForCurrentThread(); sto.Read(4096, dst).ThrowIfFailure(); return GC.GetAllocatedBytesForCurrentThread() - a0; }
                const ulong TestPid = 0x7fff0001;
                Host.Device.Configuration.VirtualFileSystem.SetRomFs(TestPid, new LibHac.Fs.MemoryStorage(src));
                long newA = Alloc(Host.Device.Configuration.VirtualFileSystem.GetRomFs(TestPid));
                bool same = dst.AsSpan().SequenceEqual(src.AsSpan(4096, Big));
                long oldA = Alloc(new LibHac.Fs.MemoryStorage(src).AsStream(System.IO.FileAccess.Read).AsStorage(true));
                Check($"RomFS 大读：32 MB 一次读 额外分配 < 64 KB（{newA} B）、数据一致；旧 AsStream 路径 ≥ 32 MB 作反证（{oldA >> 20} MB）", "是 / 一致 / 是", $"{(newA < 65536 ? "是" : "否")} / {(same ? "一致" : "不一致")} / {(oldA >= Big ? "是" : "否")}");
            }

            // ---- 系统语言缺省跟设备（维护者 10-20）：BCP 47 首选语言 → Switch SystemLanguage；期望按 Switch 语言表手写 ----
            {
                string[] tags = { "zh-Hans-CN", "zh-Hans-US", "zh-Hant-TW", "zh-HK", "zh-CN", "zh", "ja-JP", "ko-KR", "en-US", "en-GB", "en", "fr-CA", "fr-FR", "es-419", "es-ES", "pt-BR", "pt-PT", "de-DE", "xx-YY" };
                string got = string.Join(",", Array.ConvertAll(tags, t => SystemLocale.Map(t)?.ToString() ?? "-"));
                Check("系统语言映射：19 个首选语言标签", "SimplifiedChinese,SimplifiedChinese,TraditionalChinese,TraditionalChinese,SimplifiedChinese,SimplifiedChinese,Japanese,Korean,AmericanEnglish,BritishEnglish,AmericanEnglish,CanadianFrench,French,LatinAmericanSpanish,Spanish,BrazilianPortuguese,Portuguese,German,-", got);
                Ryujinx.HLE.HOS.SystemState.SystemLanguage a = SystemLocale.FromPreferred(new[] { "xx-YY", "zh-Hant-HK", "en-US" }, out _), b = SystemLocale.FromPreferred(Array.Empty<string>(), out _);
                Check("系统语言：跳过认不出的取下一个 / 列表空回美式英语", "TraditionalChinese / AmericanEnglish", $"{a} / {b}");
                string env = Environment.GetEnvironmentVariable("S2A_SYSTEM_LANGUAGE");
                Check("系统语言：显式 S2A_SYSTEM_LANGUAGE 优先，否则跟本机首选", string.IsNullOrEmpty(env) ? SystemLocale.FromPreferred(SystemLocale.Preferred(), out _).ToString() : env, Host.Language.ToString());
                // 游戏不支持系统语言时的退路（11-03 GB4 / FFR / JUMP FORCE 亚洲版只收韩 / 繁 / 简，上游取第一个 = 韩语）：系统首选依次 → 简中 → 繁中 → 英语，都不支持才取游戏首选
                var TL = typeof(Ryujinx.HLE.HOS.SystemState.TitleLanguage);
                string fb = string.Join(",", SystemLocale.TitleFallback(new[] { "en-US", "ja-JP", "xx-YY" }));
                Check("语言退路列表：en-US, ja-JP, 认不出的 → 首选依次 + 简中 / 繁中 / 英式英语（去重）", "AmericanEnglish,Japanese,SimplifiedChinese,TraditionalChinese,BritishEnglish", fb);
                int asia = (1 << 12) | (1 << 13) | (1 << 14), jpKo = (1 << 2) | (1 << 12), deOnly = 1 << 4;   // 韩 / 繁 / 简；日 / 韩；只有德语
                var pick = Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.IApplicationFunctions.PickFallbackLanguage;
                var list = SystemLocale.TitleFallback(new[] { "en-US" });
                Check("语言退路：亚洲版（韩 / 繁 / 简）→ 简中；日 / 韩 + 首选含日语 → 日语；只有德语 → 德语；不给退路 = 上游取第一个（韩）",
                    "SimplifiedChinese / Japanese / German / Korean",
                    $"{Enum.GetName(TL, pick(asia, list))} / {Enum.GetName(TL, pick(jpKo, SystemLocale.TitleFallback(new[] { "en-US", "ja-JP" })))} / {Enum.GetName(TL, pick(deOnly, list))} / {Enum.GetName(TL, pick(asia, null))}");
            }

            // ---- 主机 / 掌机档（维护者 10-20 晚改回缺省掌机）：apm GetPerformanceMode 由 Ryujinx 按 DockedMode 回答，掌机档 = 0（Normal）、主机档 = 1（Boost）----
            {
                Reply gapm = Call(sm, Msg.Cmif(1, Name8("apm")));
                uint apm = gapm.Move.Count == 1 ? gapm.Move[0] : 0;
                Reply pm = Call(apm, Msg.Cmif(1));
                bool dk = Environment.GetEnvironmentVariable("S2A_DOCKED") == "1";
                Check($"{(dk ? "主机" : "掌机")}档：Host.Docked / apm GetPerformanceMode", $"{dk} / 结果 0、{(dk ? 1 : 0)}", $"{Host.Docked} / 结果 {pm.Result}、{*(int*)pm.Data}");
            }

            // ---- 写跟踪：WriteUntracked 的 flush 按来源分（精确写信号紧跟的同区间 = GPU 自己写 → false；其余 = 回写 → true）----
            GuestMemory.WriteUntrackedHook = &RecordWriteUntracked;
            byte* wu = _game + 0x3000;
            Dispatcher.Instance.Memory.WriteUntracked((ulong)wu, new byte[4]);
            bool f1 = _lastFlush;
            Dispatcher.Instance.Memory.SignalMemoryTracking((ulong)wu, 4, write: true, precise: true);
            Dispatcher.Instance.Memory.WriteUntracked((ulong)wu, new byte[4]);
            bool f2 = _lastFlush;
            Dispatcher.Instance.Memory.WriteUntracked((ulong)wu, new byte[4]);
            bool f3 = _lastFlush;
            GuestMemory.WriteUntrackedHook = null;
            Check("WriteUntracked flush：回写 / 精确写信号后同区间 / 再一次", "True / False / True", $"{f1} / {f2} / {f3}");

            // 托管写的去向（帧率能耗线写跟踪）：Write<T> / Write(span) 已同步发过跟踪事件，内容交 WriteUntrackedHook 代写（flush = false，写跟踪不再当 CPU 写重报）；
            // GetRef 只能事先解保护（WriteHook）；Fill 经 GetWritableRegion(tracked: false) 直接写游戏内存，事先只经 WriteQuietHook（不触发 GPU 改过页先回写）
            GuestMemory.WriteHook = &RecordWrite;
            GuestMemory.WriteQuietHook = &RecordQuiet;
            GuestMemory.WriteUntrackedHook = &RecordUntracked;
            _writes.Clear();
            _quiet.Clear();
            _untracked.Clear();
            ulong wbase = (ulong)(_game + 0x3800);
            IVirtualMemoryManager gm = Dispatcher.Instance.Memory;
            new Span<byte>((void*)wbase, 0x600).Clear();
            gm.Write<uint>(wbase, 1);
            gm.Write(wbase + 0x10, new byte[] { 1, 2, 3, 4, 5, 6 });
            Dispatcher.Instance.Memory.GetRef<ulong>(wbase + 0x20) = 2;
            gm.Fill(wbase + 0x40, 0x30, 0xCC);
            GuestMemory.WriteHook = null;
            GuestMemory.WriteQuietHook = null;
            byte* wq = (byte*)wbase;
            Check("托管写去向：跟踪（WriteHook）/ 不跟踪（WriteQuietHook）/ 代写（WriteUntrackedHook，偏移 长度 flush）", "跟踪 +0x20 8；不跟踪 +0x40 48；代写 +0x0 4 False、+0x10 6 False",
                $"跟踪 {string.Join("、", _writes.ConvertAll(w => $"+0x{w.Va - wbase:X} {w.Size}"))}；不跟踪 {string.Join("、", _quiet.ConvertAll(w => $"+0x{w.Va - wbase:X} {w.Size}"))}；代写 {string.Join("、", _untracked.ConvertAll(w => $"+0x{w.Va - wbase:X} {w.Size} {w.Flush}"))}");
            Check("托管写内容：+0x0 / +0x15 / +0x20 / +0x40 / +0x6F / +0x70", "1 / 6 / 2 / CC / CC / 0",
                $"{*(uint*)wq} / {wq[0x15]} / {*(ulong*)(wq + 0x20)} / {wq[0x40]:X} / {wq[0x6F]:X} / {wq[0x70]:X}");

            // GPU 自己写游戏内存的副本区域（Ryujinx S2AUntrackedRegionPool，PhysicalMemory 不跟踪的 GetWritableRegion 用）：Dispose 时经 WriteUntracked 写回（flush = true，回写语义）；
            // 嵌套借出、不按借出次序归还、在另一条线程归还，写回内容各归各位
            {
                Ryujinx.Cpu.IVirtualMemoryManagerTracked gmt = Dispatcher.Instance.Memory;
                _untracked.Clear();
                WritableRegion ra = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, wbase + 0x100, new ReadOnlySpan<byte>((void*)(wbase + 0x100), 0x20));
                WritableRegion rb = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, wbase + 0x200, new ReadOnlySpan<byte>((void*)(wbase + 0x200), 0x20));
                ra.Memory.Span.Fill(0xA1);
                rb.Memory.Span.Fill(0xB2);
                byte* wp = (byte*)wbase;
                byte before = wp[0x100];
                ra.Dispose();
                WritableRegion rcc = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, wbase + 0x300, new ReadOnlySpan<byte>((void*)(wbase + 0x300), 0x20));
                rcc.Memory.Span.Fill(0xC3);
                System.Threading.Thread th = new(() => rb.Dispose());
                th.Start();
                th.Join();
                rcc.Dispose();
                Check("GPU 写回副本区域：写回前游戏内存不变 / 嵌套 乱序 跨线程归还后 +0x100 +0x200 +0x300 / 写回经代写（flush）", "0 / A1 B2 C3 / 3 次 True",
                    $"{before:X} / {wp[0x100]:X} {wp[0x200]:X} {wp[0x300]:X} / {_untracked.Count} 次 {_untracked.TrueForAll(w => w.Flush)}");

                // 并发借还（10-21 动森 Mac 局：前一版池在锁外按下标写槽，与扩容换数组交错，副本指针丢成空、Horizon 各服务线程起局即挂）：
                // 32 条线程同时起跑、每条同时借 48 块（池扩容与别的线程取槽交错），写各自的值、倒序归还、核内容
                int bad = 0, exc = 0;
                const int nt = 32, per = 48;
                // 各块单独一段内存：原先取 _game + 0x10000（_game 只有 64 KB，正好越过末尾），96 KB 写回全落在分配区外的本机堆上，
                //   踩坏 CoreCLR 自己的数据（10-21 门禁三次服务层单测 SIGSEGV 在 JIT 解析字符串常量，读到 0x7e7e… / 0xf8f8… = 这里的填充值）
                byte* basep = FakeNative.Alloc(nt * per * 0x40);
                System.Threading.Barrier bar = new(nt);
                System.Threading.Thread[] ths = new System.Threading.Thread[nt];
                for (int t = 0; t < nt; t++)
                {
                    int tid = t;
                    ths[t] = new System.Threading.Thread(() =>
                    {
                        WritableRegion[] held = new WritableRegion[per];
                        try
                        {
                            bar.SignalAndWait();
                            for (int i = 0; i < per; i++)
                            {
                                ulong va = (ulong)(basep + (tid * per + i) * 0x40);
                                held[i] = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, va, new ReadOnlySpan<byte>((void*)va, 0x40));
                                held[i].Memory.Span.Fill((byte)(tid * per + i));
                            }
                            for (int i = per - 1; i >= 0; i--)
                            {
                                held[i].Dispose();
                            }
                            for (int i = 0; i < per; i++)
                            {
                                byte* q = basep + (tid * per + i) * 0x40;
                                if (q[0] != (byte)(tid * per + i) || q[0x3F] != (byte)(tid * per + i))
                                {
                                    System.Threading.Interlocked.Increment(ref bad);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            System.Threading.Interlocked.Increment(ref exc);
                        }
                    });
                }
                foreach (System.Threading.Thread th2 in ths) th2.Start();
                foreach (System.Threading.Thread th2 in ths) th2.Join();
                FakeNative.Free(basep);
                Check("GPU 写回副本区域并发：32 线程同时各借 48 块，内容不符 / 异常", "0 / 0", $"{bad} / {exc}");

                // 稳态零分配：副本区域借还 + Write<uint> + 冗余检查写（每组）
                Span<byte> four = stackalloc byte[4];
                for (int i = 0; i < 20; i++)
                {
                    using (WritableRegion r0 = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, wbase + 0x400, new ReadOnlySpan<byte>((void*)(wbase + 0x400), 0x40))) r0.Memory.Span.Fill((byte)i);
                    gm.Write<uint>(wbase + 0x500, (uint)i);
                    gm.WriteWithRedundancyCheck(wbase + 0x510, four);
                }
                _untracked.Clear();
                _untracked.Capacity = 4000;
                long t0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++)
                {
                    four[0] = (byte)i;
                    using (WritableRegion r0 = Ryujinx.Cpu.S2AUntrackedRegionPool.Rent(gmt, wbase + 0x400, new ReadOnlySpan<byte>((void*)(wbase + 0x400), 0x40))) r0.Memory.Span.Fill((byte)i);
                    gm.Write<uint>(wbase + 0x500, (uint)i);
                    gm.WriteWithRedundancyCheck(wbase + 0x510, four);
                }
                _alloc.Add(("写跟踪代写：GPU 写回副本区域 0x40 + Write<uint> + 冗余检查写（每组）", (GC.GetAllocatedBytesForCurrentThread() - t0) / 1000.0));
            }

            // 冗余检查写（常量缓冲内联更新）：区间所在子页没有 GPU 改过的数据 → 照原版比内容，同值不写、回 false；
            // 有 → 不比（客体内存旧字节可能是回写前的），精确写信号 + 代写（flush = false）、回 true。修前同值一律回 false：GPU 侧那份不被更新
            {
                GuestMemory.GpuModifiedHook = &FakeGpuModified;
                _untracked.Clear();
                byte[] same = { 1, 2, 3, 4 };
                // 跟踪句柄观察事件种类：无 GPU 改过 → 普通写事件（句柄记脏，精确动作不调）；有 → 精确写（精确动作调、句柄不记脏）。
                // 修前两档都发精确写：句柄不改保护，写跟踪的子页一直观察中，每笔内联更新都付基线代价（10-21 暗黑3 GPU 模拟线程回归）
                Ryujinx.Memory.Tracking.RegionHandle hd = Dispatcher.Instance.Memory.BeginTracking(wbase & ~0xFFFUL, 0x1000, 0);
                int preciseN = 0;
                hd.RegisterPreciseAction((a, n, w) => { preciseN++; return true; });
                hd.Reprotect();
                _gpuMod = false; bool r1 = gm.WriteWithRedundancyCheck(wbase + 0x10, same);
                bool d1 = hd.Dirty;
                _reprot.Clear();
                GuestMemory.ReprotectHook = &RecordReprotect;
                _gpuMod = false; bool r2 = gm.WriteWithRedundancyCheck(wbase + 0x10, new byte[] { 9, 9, 9, 9 });
                GuestMemory.ReprotectHook = null;
                bool d2 = hd.Dirty; int p2 = preciseN;
                // 普通写事件发到游戏侧区域：Ryujinx 按游戏侧改保护（写跟踪放掉该子页）。修前发普通域：只改普通域保护，游戏侧不变
                Check("冗余检查写常态的改保护：游戏侧区域改成可读写", "1",
                    _reprot.FindAll(x => x.Guest && x.Perm == MemoryPermission.ReadAndWrite && x.Va <= wbase + 0x10 && wbase + 0x10 < x.Va + x.Size).Count.ToString());
                hd.Reprotect();
                _gpuMod = true; bool r3 = gm.WriteWithRedundancyCheck(wbase + 0x10, new byte[] { 9, 9, 9, 9 });
                bool d3 = hd.Dirty; int p3 = preciseN - p2;
                hd.Dispose();
                GuestMemory.GpuModifiedHook = null;
                GuestMemory.WriteUntrackedHook = null;
                Check("冗余检查写：无 GPU 改过同值 / 无 GPU 改过变了 / 有 GPU 改过同值；代写记录", "False / True / True；+0x10 4 False、+0x10 4 False",
                    $"{r1} / {r2} / {r3}；{string.Join("、", _untracked.ConvertAll(w => $"+0x{w.Va - wbase:X} {w.Size} {w.Flush}"))}");
                Check("冗余检查写的跟踪事件：同值不发 / 无 GPU 改过发普通写（句柄脏、精确动作次数）/ 有 GPU 改过发精确写（句柄脏、精确动作次数）",
                    "不脏 / 脏 0 次 / 不脏 1 次", $"{(d1 ? "脏" : "不脏")} / {(d2 ? "脏" : "不脏")} {p2} 次 / {(d3 ? "脏" : "不脏")} {p3} 次");
            }

            // ---- 确定性档：C# 往游戏内存的写一律经 s2a_det_host_write（Write / WriteUntracked / GetWritableRegion 写回 / Fill），内容照写 ----
            {
                FakeNative.DetOn = 1;
                GuestMemory.InitDet();
                FakeNative.DetLog.Clear();
                ulong db = (ulong)(_game + 0x3A00);
                IVirtualMemoryManager dm = Dispatcher.Instance.Memory;
                dm.Write<uint>(db, 0x11223344);
                dm.Write(db + 0x10, new byte[] { 1, 2, 3, 4, 5, 6 });
                Dispatcher.Instance.Memory.WriteUntracked(db + 0x20, new byte[] { 7, 7, 7, 7 });
                using (WritableRegion wr = dm.GetWritableRegion(db + 0x30, 8))
                {
                    wr.Memory.Span.Fill(9);
                }
                dm.Fill(db + 0x40, 16, 0xAB);
                FakeNative.DetOn = 0;
                GuestMemory.InitDet();
                string log = string.Join("、", FakeNative.DetLog.ConvertAll(w => $"+0x{w.Va - db:X} {w.Len}"));
                byte* dp = (byte*)db;
                bool content = *(uint*)dp == 0x11223344 && dp[0x15] == 6 && dp[0x23] == 7 && dp[0x37] == 9 && dp[0x4F] == 0xAB;
                Check("确定性档：C# 写游戏内存经 s2a_det_host_write", "+0x0 4、+0x10 6、+0x20 4、+0x30 8、+0x40 16；内容正确", $"{log}；内容{(content ? "正确" : "不对")}");
            }

            // ---- 测试层接口：自动玩家键并入手柄、声音峰值 ----
            Ryujinx.HLE.HOS.Services.Hid.GamepadInput gi = default;
            InputPump.Merge(ref gi, (1u << 0) | (1u << 16) | (1u << 21));
            Check("自动玩家键 A + 左摇杆左 + 右摇杆上 → 手柄状态", "A 按下、左摇杆 X -32767、右摇杆 Y 32767", $"{((gi.Buttons & Ryujinx.HLE.HOS.Services.Hid.ControllerKeys.A) != 0 ? "A 按下" : "A 未按")}、左摇杆 X {gi.LStick.Dx}、右摇杆 Y {gi.RStick.Dy}");

            AudioPeakDriver.Mark();
            byte[] pcm = new byte[8];
            BitConverter.TryWriteBytes(pcm.AsSpan(2), (short)-16384);
            AudioPeakDriver.Observe(new Ryujinx.Audio.Common.AudioBuffer { Data = pcm, DataSize = 8 }, Ryujinx.Audio.Common.SampleFormat.PcmInt16);
            Check("声音峰值（PCM16 样本 -16384）", "0.500", AudioPeakDriver.CurrentPeak.ToString("F3"));
            // 游戏缓冲（audout）样本不再整块复制进 Data：峰值经 SampleReader 从游戏内存分块拉
            {
                ulong pa = (ulong)FakeNative.Alloc(0x4000);
                Dispatcher.Instance.Memory.Write(pa, new byte[8]);
                Dispatcher.Instance.Memory.Write<short>(pa + 4, -24576);
                var capture = new CaptureSession(Dispatcher.Instance.Memory, Ryujinx.Audio.Common.SampleFormat.PcmInt16, 2, 1 << 16);
                var gb = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = pa, DataSize = 8 };
                capture.RegisterBuffer(gb);
                AudioPeakDriver.Observe(gb, Ryujinx.Audio.Common.SampleFormat.PcmInt16);
                Check("声音峰值：游戏缓冲（样本在游戏内存，PCM16 -24576）", "0.750", AudioPeakDriver.CurrentPeak.ToString("F3"));
            }

            // ---- 会话关闭 ----
            int open0 = Dispatcher.Instance.OpenSessionCount;
            FakeNative.HandleClose(nv);
            Check("游戏 CloseHandle(nvdrv 会话)", $"C# 会话数 {open0 - 1}", $"C# 会话数 {Dispatcher.Instance.OpenSessionCount}");

            // 错误小程序在域会话里拉起（10-28 双人成行建房闪退）+ 域会话错误应答格式（ErrorAppletTest.cs）
            ErrorAppletInDomain(sm);
            // 本地联机按需自动开（LdnRelayTest.cs）：会托管全进程的局域网中继（0.0.0.0:单测临时端口，见 Main 开头 S2AHooks.LdnPort），放在最后一批
            LdnAutoLan(sm);
            // 前后台 / 焦点 / 底座 / 睡眠转给游戏（LifecycleTest.cs，缺口扫描族 1 / 2）
            LifecycleMessages(sm);
            // 手柄小程序非手柄设置模式结束时把焦点还给游戏（ControllerAppletFocusTest.cs，Let's Get Fit 腕带说明后卡死）
            ControllerAppletFocus();
            // 手柄设置小程序同参数重开 = 掌机格不收 → 改接双 Joy-Con（ControllerAppletHandheldRejectTest.cs，瓦力欧制造每帧重开）
            ControllerAppletHandheldReject();

            // 放最后：旧框架服务进程的堆在各自服务线程起来时才申请（ServerLoop 开头），前面的用例给足时间
            DramLayout();
        }

        // 声音输出不按游戏缓冲长度建托管数组（修干净清单 #13）：游戏一块 32 MB 缓冲走 登记 + 排队，额外托管分配 < 64 KB；
        // 普通缓冲每次 0 B。只用改前也有的接口（RegisterBuffer / QueueBuffer / 苹果会话），把被测文件换回旧版同样能跑（反证）
        private static void AudioOutputBoundedAlloc()
        {
            const int Big = 32 << 20;
            IVirtualMemoryManager mem = Dispatcher.Instance.Memory;
            byte* big = FakeNative.Alloc(Big);
            new Span<byte>(big, Big).Fill(0x11);
            var output = Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction.Output;
            var pcm16 = Ryujinx.Audio.Common.SampleFormat.PcmInt16;

            // 登记 + 排队一块，量本线程额外托管分配
            static long Alloc(Ryujinx.Audio.Integration.IHardwareDeviceSession s, Ryujinx.Audio.Common.AudioBuffer b)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                bool ok = s.RegisterBuffer(b);
                s.QueueBuffer(b);
                long a = GC.GetAllocatedBytesForCurrentThread() - a0;
                return ok ? a : -1;
            }

            // 普通缓冲（20 ms）稳态每次：预热 100 次后量 1000 次
            static double Steady(Ryujinx.Audio.Integration.IHardwareDeviceSession s, ulong va, ulong size)
            {
                var b = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = va, DataSize = size };
                long total = 0;

                for (int i = 0; i < 1100; i++)
                {
                    b.DataSize = size;
                    long a = Alloc(s, b);
                    total += i >= 100 ? a : 0;
                }

                return total / 1000.0;
            }

            // ① 基类登记（哑设备 = 不经兼容层的输出会话）：原 new byte[DataSize] 整块复制
            {
                var dummy = new DummyHardwareDeviceDriver();
                var s = dummy.OpenDeviceSession(output, mem, pcm16, 48000, 2);
                long a = Alloc(s, new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)big, DataSize = Big });
                Check($"声音输出 ① 基类登记：游戏一块 32 MB 缓冲 登记 + 排队，额外托管分配 < 64 KB（{a >> 10} KB）", "是", a >= 0 && a < 64 << 10 ? "是" : "否");
                _alloc.Add(("声音输出 ① 基类登记 + 排队，20 ms 立体声缓冲每次", Steady(s, (ulong)big, 3840)));
            }

            // ② 兼容层降混 6 → 2（只认立体声的驱动）：原整块复制 + 降混新数组 + ToArray
            {
                var compat = new Ryujinx.Audio.Backends.CompatLayer.CompatLayerHardwareDeviceDriver(new StereoOnlyDriver());
                var s = compat.OpenDeviceSession(output, mem, pcm16, 48000, 6);
                long a = Alloc(s, new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)big, DataSize = Big });
                Check($"声音输出 ② 兼容层降混 6 → 2：游戏一块 32 MB 缓冲 登记 + 排队，额外托管分配 < 64 KB（{a >> 10} KB）", "是", a >= 0 && a < 64 << 10 ? "是" : "否");
                _alloc.Add(("声音输出 ② 兼容层降混 6 → 2 登记 + 排队，20 ms 缓冲每次", Steady(s, (ulong)big, 11520)));
            }

            // ③ 苹果会话（产品后端，未 Start = 设备不消费）：原环形缓冲按排队量无上限扩容
            if (Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver.IsSupported)
            {
                using var drv = new Ryujinx.Audio.Backends.Apple.AppleHardwareDeviceDriver();

                // 游戏缓冲 32 MB 登记 + 排队
                using (var s = drv.OpenDeviceSession(output, mem, pcm16, 48000, 2))
                {
                    long a = Alloc(s, new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)big, DataSize = Big });
                    Check($"声音输出 ③ 苹果会话：游戏一块 32 MB 缓冲 登记 + 排队，额外托管分配 < 64 KB（{a >> 10} KB）", "是", a >= 0 && a < 64 << 10 ? "是" : "否");
                }

                // 渲染器式排队（样本在 Data）累计 32 MB 不消费：环形缓冲最多存 1 秒（48 kHz 立体声 PCM16 = 192,000 B），额外分配 < 512 KB
                using (var s = drv.OpenDeviceSession(output, null, pcm16, 48000, 2))
                {
                    byte[] block = new byte[1 << 20];
                    var bufs = new Ryujinx.Audio.Common.AudioBuffer[32];

                    for (int i = 0; i < bufs.Length; i++)
                    {
                        bufs[i] = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)i, Data = block, DataSize = (ulong)block.Length };
                    }

                    long a0 = GC.GetAllocatedBytesForCurrentThread();

                    foreach (var b in bufs)
                    {
                        s.QueueBuffer(b);
                    }

                    long a = GC.GetAllocatedBytesForCurrentThread() - a0;
                    Check($"声音输出 ③ 苹果会话：渲染器式排队累计 32 MB 不消费，额外托管分配 < 512 KB（{a >> 10} KB）", "是", a < 512 << 10 ? "是" : "否");
                }

                // ④ 只交一块就等它播完（10-24 #164 动森开场视频：AudioOut 交一块 1024 帧 = 21 ms < 预充下限 64 ms，等这块播完才交下一块）：
                //   2 秒内设备必须开播并播完这 1024 帧（兜底 100 ms + 看门狗 ≤ 250 ms + 播放 21 ms）；修前没人再查兜底，永不开播、播完 0 帧。
                //   真 CoreAudio：打不开设备就跳过（同上面苹果声音输出用例）
                {
                    string err = null; ulong played = 0;
                    var th = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            using var s1 = drv.OpenDeviceSession(output, mem, pcm16, 48000, 2);
                            s1.Start();
                            var b1 = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)big, DataSize = 1024 * 4 };
                            s1.RegisterBuffer(b1);
                            s1.QueueBuffer(b1);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < 2000 && s1.GetPlayedSampleCount() < 1024) System.Threading.Thread.Sleep(10);
                            played = s1.GetPlayedSampleCount();
                        }
                        catch (Exception e) { err = $"{e.GetType().Name} {e.Message}"; }
                    }) { IsBackground = true, Name = "单测·苹果声音只交一块" };
                    th.Start();
                    if (!th.Join(10000)) Check("声音输出 ④ 苹果会话只交一块 1024 帧：10 秒内跑完（真 CoreAudio）", "跑完", "超时");
                    else if (err != null) Console.Error.WriteLine($"声音输出 ④：打不开输出设备，跳过（{err}）");
                    else Check("声音输出 ④ 苹果会话只交一块 1024 帧（< 预充下限）、不再交：2 秒内播完的帧数", "≥ 1024", played >= 1024 ? "≥ 1024" : played.ToString());
                }
            }

            FakeNative.Free(big);
        }

        // 声音输出样本逐字节与旧实现一致（期望值由测试按旧代码的语义自算：整块快照拼接 / 旧降混公式 / 旧格式转换公式）
        private static void AudioOutputSamples()
        {
            IVirtualMemoryManager mem = Dispatcher.Instance.Memory;
            var pcm16 = Ryujinx.Audio.Common.SampleFormat.PcmInt16;
            byte* g = FakeNative.Alloc(1 << 20);
            uint seed = 12345;

            for (int i = 0; i < 1 << 20; i++)
            {
                seed = seed * 1103515245 + 12345;
                g[i] = (byte)(seed >> 16);
            }

            byte[] Guest(int off, int len) => new ReadOnlySpan<byte>(g + off, len).ToArray();

            // 按 odd 块长读空队列
            static byte[] Drain(Ryujinx.Audio.Backends.Common.AudioSampleQueue q, int chunk)
            {
                var all = new List<byte>();
                byte[] tmp = new byte[chunk];

                while (q.Length > 0)
                {
                    int n = q.Read(tmp);
                    all.AddRange(tmp.AsSpan(0, n).ToArray());
                }

                return all.ToArray();
            }

            // 基类：三块大小不齐的游戏缓冲，按 1000 B 一块读出 = 三块原样拼接
            {
                var cs = new CaptureSession(mem, pcm16, 2, 1 << 16);
                (int Off, int Len)[] parts = [(0, 3840), (5000, 1922), (9000, 7)];
                var expect = new List<byte>();

                foreach ((int off, int len) in parts)
                {
                    var b = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)(g + off), DataSize = (ulong)len };
                    cs.RegisterBuffer(b);
                    cs.QueueBuffer(b);
                    expect.AddRange(Guest(off, len));
                }

                byte[] got = Drain(cs.Q, 1000);
                Check("声音输出样本：基类三块（3840 / 1922 / 7 B）按 1000 B 读出", "5769 B、逐字节一致", $"{got.Length} B、{(got.AsSpan().SequenceEqual(expect.ToArray()) ? "逐字节一致" : "不一致")}");
            }

            // 停止 / 清空把缓冲交还游戏（UnregisterBuffer）后游戏改写，没播的部分仍是排队时的内容（旧实现登记时整块快照）
            {
                var cs = new CaptureSession(mem, pcm16, 2, 1 << 16);
                int off = 20000;
                byte[] before = Guest(off, 3840);
                var b = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)(g + off), DataSize = 3840 };
                cs.RegisterBuffer(b);
                cs.QueueBuffer(b);
                byte[] head = new byte[1000];
                cs.Q.Read(head);
                cs.UnregisterBuffer(b);
                new Span<byte>(g + off, 3840).Fill(0xEE);
                byte[] rest = Drain(cs.Q, 1000);
                byte[] got = [.. head, .. rest];
                Check("声音输出样本：读 1000 B 后交还缓冲、游戏改写，读出的仍是排队时内容", "3840 B、逐字节一致", $"{got.Length} B、{(got.AsSpan().SequenceEqual(before) ? "逐字节一致" : "不一致")}");
            }

            // 拷进来的样本（渲染器输出）超过上限：上限内原样、超出部分静音，长度不变（播放进度 / 释放时机不变）
            {
                var q = new Ryujinx.Audio.Backends.Common.AudioSampleQueue(4096);
                byte[] data = Guest(30000, 10000);
                q.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = data, DataSize = 10000 });
                int len0 = q.Length;
                byte[] got = Drain(q, 1000);
                bool ok = got.Length == 10000 && got.AsSpan(0, 4096).SequenceEqual(data.AsSpan(0, 4096)) && got.AsSpan(4096).IndexOfAnyExcept((byte)0) < 0;
                Check("声音输出样本：拷入 10000 B、上限 4096 B", "长度 10000、前 4096 B 一致、其余静音", ok && len0 == 10000 ? "长度 10000、前 4096 B 一致、其余静音" : $"长度 {len0} / 读出 {got.Length}、不符");
            }

            // 兼容层：降混 6 → 2 / 6 → 1 / 2 → 1、格式转换 PCM16 → Float / Int32 / Int24 / Int8（含降混 + 转换），两块不齐的缓冲按 1001 B 读
            (uint User, uint Real, Ryujinx.Audio.Common.SampleFormat Fmt)[] cases =
            [
                (6, 2, pcm16), (6, 1, pcm16), (2, 1, pcm16),
                (2, 2, Ryujinx.Audio.Common.SampleFormat.PcmFloat), (2, 2, Ryujinx.Audio.Common.SampleFormat.PcmInt32),
                (2, 2, Ryujinx.Audio.Common.SampleFormat.PcmInt24), (2, 2, Ryujinx.Audio.Common.SampleFormat.PcmInt8),
                (6, 2, Ryujinx.Audio.Common.SampleFormat.PcmFloat),
            ];
            var bad = new List<string>();

            foreach (var c in cases)
            {
                var drvC = new CaptureDriver(mem, c.Real, c.Fmt);
                var compat = new Ryujinx.Audio.Backends.CompatLayer.CompatLayerHardwareDeviceDriver(drvC);
                var s = compat.OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction.Output, mem, pcm16, 48000, c.User);
                var expect = new List<byte>();
                ulong sizes = 0;

                foreach ((int off, int len) in new[] { (40000, 11525), (60000, 3843) })
                {
                    var b = new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)(g + off), DataSize = (ulong)len };
                    s.RegisterBuffer(b);
                    s.QueueBuffer(b);
                    byte[] e = OldCompat(Guest(off, len), c.User, c.Real, c.Fmt);
                    expect.AddRange(e);
                    sizes += b.DataSize;
                }

                byte[] got = Drain(drvC.Last.Q, 1001);

                if (!got.AsSpan().SequenceEqual(expect.ToArray()) || sizes != (ulong)expect.Count)
                {
                    bad.Add($"{c.User}→{c.Real} {c.Fmt}：读出 {got.Length} / 期望 {expect.Count} B、DataSize 合计 {sizes}");
                }
            }

            Check($"声音输出样本：兼容层 {cases.Length} 种降混 / 转换组合，两块不齐的缓冲按 1001 B 读", "全部逐字节一致、DataSize = 输出字节数", bad.Count == 0 ? "全部逐字节一致、DataSize = 输出字节数" : string.Join("；", bad));
        }

        // 旧兼容层的语义（改前 CompatLayerHardwareDeviceSession + Downmixing 整块做法，按公式重写）：PCM16 降混（6 → 2 → 1 / 2 → 1），再转格式
        private static byte[] OldCompat(byte[] data, uint user, uint real, Ryujinx.Audio.Common.SampleFormat fmt)
        {
            short[] s = new short[data.Length / 2];
            Buffer.BlockCopy(data, 0, s, 0, s.Length * 2);

            if (user == 6 && real != 6)
            {
                const long one = 1 << 16, m3 = (int)(0.707f * (1 << 16)), m12 = (int)(0.251f * (1 << 16)), half = (int)(0.5f * (1 << 16));
                short[] st = new short[s.Length / 6 * 2];

                for (int i = 0; i < s.Length / 6; i++)
                {
                    long fl = s[i * 6], fr = s[i * 6 + 1], ce = s[i * 6 + 2], lfe = s[i * 6 + 3], bl = s[i * 6 + 4], br = s[i * 6 + 5];
                    st[i * 2] = (short)Math.Clamp((m3 * bl + m12 * lfe + m3 * ce + one * fl + half) >> 16, short.MinValue, short.MaxValue);
                    st[i * 2 + 1] = (short)Math.Clamp((m3 * br + m12 * lfe + m3 * ce + one * fr + half) >> 16, short.MinValue, short.MaxValue);
                }

                s = st;
            }

            if ((user == 6 || user == 2) && real == 1)
            {
                const long m6 = (int)(0.501f * (1 << 16));
                short[] mo = new short[s.Length / 2];

                for (int i = 0; i < mo.Length; i++)
                {
                    mo[i] = (short)Math.Clamp((s[i * 2] * m6 + s[i * 2 + 1] * m6) >> 16, short.MinValue, short.MaxValue);
                }

                s = mo;
            }

            var o = new List<byte>();

            foreach (short v in s)
            {
                switch (fmt)
                {
                    case Ryujinx.Audio.Common.SampleFormat.PcmInt16: o.AddRange(BitConverter.GetBytes(v)); break;
                    case Ryujinx.Audio.Common.SampleFormat.PcmInt8: o.Add((byte)(sbyte)(v >> 8)); break;
                    case Ryujinx.Audio.Common.SampleFormat.PcmInt24: o.Add(0); o.Add((byte)(v & 0xff)); o.Add((byte)(v >> 8)); break;
                    case Ryujinx.Audio.Common.SampleFormat.PcmInt32: o.AddRange(BitConverter.GetBytes(v << 16)); break;
                    case Ryujinx.Audio.Common.SampleFormat.PcmFloat: o.AddRange(BitConverter.GetBytes((float)v / short.MaxValue)); break;
                }
            }

            return o.ToArray();
        }

        // 设备缓冲泵（苹果会话的供数机制，巫师3 杂音根因修复）：排队即交设备、不等设备回调；交出的缓冲只含真实样本、不补静音。
        // 期望全部手算：48 kHz 立体声 PCM16 每帧 4 B，设备缓冲 20 ms = 960 帧 = 3840 B，32 个；游戏每块 1024 帧 = 4096 B（巫师3 实测块长）
        private static void AudioDevicePump()
        {
            byte[] Tone(int n, int seed)
            {
                byte[] b = new byte[n];

                for (int i = 0; i < n; i++)
                {
                    b[i] = (byte)(i * 7 + seed);
                }

                return b;
            }

            // ① 回调线程卡住（不还缓冲，= 托管回调被阻塞式 GC 挡住）时，游戏已排的 4 块全部已在设备上
            {
                var p = new FakePump(32, 3840, 4);
                var all = new List<byte>();

                for (int k = 0; k < 4; k++)
                {
                    byte[] d = Tone(4096, k);
                    all.AddRange(d);
                    p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { DataPointer = (ulong)k, Data = d, DataSize = 4096 });
                    p.Pump();
                }

                // 4 块 16384 B：每块排入时都把能交的交掉——3840 / 256（第 1 块）、3584+…；按排入顺序逐次手算：
                //   块 1 后：3840、256 → 交 2 个（4096 B）；块 2 后：3840、256；块 3、块 4 同 → 共 8 次、在途 16384 B
                string got = $"交设备 {p.Submits.Count} 次、在途 {p.InFlightBytes} B = {p.InFlightBytes / 4} 帧、未交 {p.Samples.Length} B";
                Check("设备缓冲泵 ① 回调卡住不还缓冲：游戏排 4 块 × 1024 帧，设备上已有", "交设备 8 次、在途 16384 B = 4096 帧、未交 0 B", got);

                byte[] sent = p.Sent.ToArray();
                bool same = sent.AsSpan().SequenceEqual(all.ToArray());
                bool whole = p.Submits.TrueForAll(x => x.Bytes % 4 == 0 && x.Bytes > 0);
                Check("设备缓冲泵 ② 交出的字节 = 排入的字节（不补静音、每次整帧）", "16384 B、逐字节一致、整帧", $"{sent.Length} B、{(same ? "逐字节一致" : "不一致")}、{(whole ? "整帧" : "有半帧或空缓冲")}");
            }

            // ③ 池满：排 40 × 3840 B，不还 → 交 32 个、余 8 × 3840 = 30720 B；还 1 个（第 0 号，3840 B）→ 再交 1 个、余 26880 B
            {
                var p = new FakePump(32, 3840, 4);
                p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(40 * 3840, 1), DataSize = 40 * 3840 });
                p.Pump();
                string a = $"{p.Submits.Count} 个、余 {p.Samples.Length} B";
                int back = p.ReturnSlot(p.Submits[0].Slot);
                p.Pump();
                string b = $"还回 {back} B、再交后共 {p.Submits.Count} 个、余 {p.Samples.Length} B、第 33 个用的是还回的 {(p.Submits[32].Slot == p.Submits[0].Slot ? "同一号" : "别的号")}";
                int first = p.ReturnSlot(p.Submits[1].Slot);
                int again = p.ReturnSlot(p.Submits[1].Slot);
                Check("设备缓冲泵 ③ 池满：排 40 个缓冲的量、不还", "32 个、余 30720 B", a);
                Check("设备缓冲泵 ③ 还 1 个后", "还回 3840 B、再交后共 33 个、余 26880 B、第 33 个用的是还回的 同一号", b);
                Check("设备缓冲泵 ④ 同一缓冲连还两次（第二次不在途）", "3840 B、0 B", $"{first} B、{again} B");
            }

            // ⑤ 半帧不交：排 4097 B（1024 帧 + 1 B）→ 只交 4096 B，余 1 B
            {
                var p = new FakePump(32, 3840, 4);
                p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(4097, 3), DataSize = 4097 });
                p.Pump();
                Check("设备缓冲泵 ⑤ 排 4097 B（多半帧）", "交 4096 B、余 1 B", $"交 {p.InFlightBytes} B、余 {p.Samples.Length} B");
            }

            // ⑥ 起播预充（10-22 杂音）：下限 12288 B（3 块 4096）。在途凑够下限前 PrimedForStart 为 false（设备不该开播），
            //    凑够后为 true 并闩锁（之后设备把缓冲全还回、在途归零也保持 true，不重新 pre-roll）。
            //    旧行为 = 下限 0：第一笔提交就 primed（无 pre-roll，一开声就掐边）。
            {
                var p = new FakePump(32, 4096, 4, 12288);
                p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(4096, 0), DataSize = 4096 });
                p.Pump();
                bool after1 = p.PrimedForStart;   // 在途 4096 < 12288
                p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(4096, 1), DataSize = 4096 });
                p.Pump();
                bool after2 = p.PrimedForStart;   // 在途 8192 < 12288
                p.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(4096, 2), DataSize = 4096 });
                p.Pump();
                bool after3 = p.PrimedForStart;   // 在途 12288 ≥ 12288 → true
                Check("设备缓冲泵 ⑥ 起播预充：在途达下限前不开播、达到后开播",
                    "1 块 否、2 块 否、3 块 是",
                    $"1 块 {(after1 ? "是" : "否")}、2 块 {(after2 ? "是" : "否")}、3 块 {(after3 ? "是" : "否")}");

                // 闩锁：设备把 3 块全还回，在途归零，PrimedForStart 仍为 true（不重新 pre-roll）
                foreach (var (slot, _) in p.Submits.ToArray())
                {
                    p.ReturnSlot(slot);
                }
                Check("设备缓冲泵 ⑦ 预充闩锁：在途归零后仍保持已起播",
                    "在途 0 B、仍 primed",
                    $"在途 {p.InFlightBytes} B、{(p.PrimedForStart ? "仍 primed" : "回退 pre-roll")}");

                // 下限 0（旧行为对照）：第一笔提交就 primed，没有 pre-roll
                var old = new FakePump(32, 4096, 4);
                old.Samples.Enqueue(new Ryujinx.Audio.Common.AudioBuffer { Data = Tone(4096, 0), DataSize = 4096 });
                old.Pump();
                Check("设备缓冲泵 ⑧ 下限 0（旧行为）：第一笔就 primed、无 pre-roll",
                    "是", old.PrimedForStart ? "是" : "否");
            }
        }

        private sealed class FakePump : Ryujinx.Audio.Backends.Common.DeviceBufferPump
        {
            private readonly byte[][] _mem;
            public readonly List<(int Slot, int Bytes)> Submits = new();
            public readonly List<byte> Sent = new();

            public FakePump(int slots, int slotBytes, int bytesPerFrame, long primeFloorBytes = 0) : base(slots, slotBytes, bytesPerFrame, 1 << 20, primeFloorBytes)
            {
                _mem = new byte[slots][];

                for (int i = 0; i < slots; i++)
                {
                    _mem[i] = new byte[slotBytes];
                }
            }

            protected override Span<byte> SlotData(int slot) => _mem[slot];

            protected override void Submit(int slot, int bytes)
            {
                Submits.Add((slot, bytes));
                Sent.AddRange(_mem[slot].AsSpan(0, bytes).ToArray());
            }

            public int ReturnSlot(int slot) => Returned(slot);
        }

        // 记录型输出会话：排队的样本进 AudioSampleQueue（同苹果会话），测试按块读出
        private sealed class CaptureSession : Ryujinx.Audio.Backends.Common.HardwareDeviceSessionOutputBase
        {
            public readonly Ryujinx.Audio.Backends.Common.AudioSampleQueue Q;

            public CaptureSession(IVirtualMemoryManager mem, Ryujinx.Audio.Common.SampleFormat fmt, uint channels, int maxCopied) : base(mem, fmt, 48000, channels)
            {
                Q = new Ryujinx.Audio.Backends.Common.AudioSampleQueue(maxCopied);
            }

            public override void QueueBuffer(Ryujinx.Audio.Common.AudioBuffer buffer) => Q.Enqueue(buffer);
            public override void UnregisterBuffer(Ryujinx.Audio.Common.AudioBuffer buffer) => Q.Release(buffer);
            public override bool WasBufferFullyConsumed(Ryujinx.Audio.Common.AudioBuffer buffer) => true;
            public override void Dispose() { }
            public override void PrepareToClose() { }
            public override void SetVolume(float volume) { }
            public override float GetVolume() => 1f;
            public override void Start() { }
            public override void Stop() { }
            public override ulong GetPlayedSampleCount() => 0;
        }

        // 只认一种声道数 + 一种格式的记录型驱动，逼兼容层降混 / 转格式
        private sealed class CaptureDriver : Ryujinx.Audio.Integration.IHardwareDeviceDriver
        {
            private readonly IVirtualMemoryManager _mem;
            private readonly uint _channels;
            private readonly Ryujinx.Audio.Common.SampleFormat _fmt;
            private readonly System.Threading.ManualResetEvent _ev = new(false);
            public CaptureSession Last;

            public CaptureDriver(IVirtualMemoryManager mem, uint channels, Ryujinx.Audio.Common.SampleFormat fmt)
            {
                _mem = mem;
                _channels = channels;
                _fmt = fmt;
            }

            public static bool IsSupported => true;
            public float Volume { get; set; } = 1f;
            public Ryujinx.Audio.Integration.IHardwareDeviceSession OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, Ryujinx.Audio.Common.SampleFormat sampleFormat, uint sampleRate, uint channelCount)
                => Last = new CaptureSession(_mem, sampleFormat, channelCount, 1 << 20);
            public System.Threading.ManualResetEvent GetUpdateRequiredEvent() => _ev;
            public System.Threading.ManualResetEvent GetPauseEvent() => _ev;
            public bool SupportsDirection(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction) => direction == Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction.Output;
            public bool SupportsSampleRate(uint sampleRate) => true;
            public bool SupportsSampleFormat(Ryujinx.Audio.Common.SampleFormat sampleFormat) => sampleFormat == _fmt;
            public bool SupportsChannelCount(uint channelCount) => channelCount == _channels;
            public void Dispose() { }
        }

        // 只支持立体声的声音驱动（哑设备 + 不认 6 声道），逼兼容层走降混
        private sealed class StereoOnlyDriver : Ryujinx.Audio.Integration.IHardwareDeviceDriver
        {
            private readonly DummyHardwareDeviceDriver _d = new();

            public static bool IsSupported => true;
            public float Volume { get => _d.Volume; set => _d.Volume = value; }
            public Ryujinx.Audio.Integration.IHardwareDeviceSession OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, Ryujinx.Audio.Common.SampleFormat sampleFormat, uint sampleRate, uint channelCount)
                => _d.OpenDeviceSession(direction, memoryManager, sampleFormat, sampleRate, channelCount);
            public System.Threading.ManualResetEvent GetUpdateRequiredEvent() => _d.GetUpdateRequiredEvent();
            public System.Threading.ManualResetEvent GetPauseEvent() => _d.GetPauseEvent();
            public bool SupportsDirection(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction) => _d.SupportsDirection(direction);
            public bool SupportsSampleRate(uint sampleRate) => true;
            public bool SupportsSampleFormat(Ryujinx.Audio.Common.SampleFormat sampleFormat) => true;
            public bool SupportsChannelCount(uint channelCount) => channelCount is 1 or 2;
            public void Dispose() => _d.Dispose();
        }

        // 只支持立体声的声音驱动，会话排队时读出这块送到设备的数据（预分配，记录本身不分配），追加即算播完
        private sealed class CaptureStereoDriver : Ryujinx.Audio.Integration.IHardwareDeviceDriver
        {
            private readonly DummyHardwareDeviceDriver _d = new();
            public readonly short[] Last = new short[4096];
            public int LastSize;

            public static bool IsSupported => true;
            public float Volume { get; set; } = 1f;
            public Ryujinx.Audio.Integration.IHardwareDeviceSession OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, Ryujinx.Audio.Common.SampleFormat sampleFormat, uint sampleRate, uint channelCount)
                => new Session(this, memoryManager, sampleFormat, sampleRate, channelCount);
            public System.Threading.ManualResetEvent GetUpdateRequiredEvent() => _d.GetUpdateRequiredEvent();
            public System.Threading.ManualResetEvent GetPauseEvent() => _d.GetPauseEvent();
            public bool SupportsDirection(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction) => direction == Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction.Output;
            public bool SupportsSampleRate(uint sampleRate) => true;
            public bool SupportsSampleFormat(Ryujinx.Audio.Common.SampleFormat sampleFormat) => true;
            public bool SupportsChannelCount(uint channelCount) => channelCount is 1 or 2;
            public void Dispose() => _d.Dispose();

            private sealed class Session : Ryujinx.Audio.Backends.Common.HardwareDeviceSessionOutputBase
            {
                private readonly CaptureStereoDriver _drv;
                private ulong _played;

                public Session(CaptureStereoDriver drv, IVirtualMemoryManager mm, Ryujinx.Audio.Common.SampleFormat f, uint rate, uint ch) : base(mm, f, rate, ch) => _drv = drv;

                public override void QueueBuffer(Ryujinx.Audio.Common.AudioBuffer buffer)
                {
                    _drv.LastSize = (int)buffer.DataSize;
                    // 游戏缓冲的样本留在游戏内存、经 SampleReader 读（兼容层降混也在读时做）；渲染器式缓冲在 Data
                    Span<byte> dst = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_drv.Last.AsSpan());
                    int n = (int)Math.Min(buffer.DataSize, (ulong)dst.Length);
                    if (buffer.Data != null) buffer.Data.AsSpan(0, n).CopyTo(dst);
                    else buffer.SampleReader.ReadSamples(buffer, 0, dst[..n]);
                    _played += GetSampleCount(buffer);
                }

                public override bool WasBufferFullyConsumed(Ryujinx.Audio.Common.AudioBuffer buffer) => true;
                public override void SetVolume(float volume) { }
                public override float GetVolume() => 1f;
                public override ulong GetPlayedSampleCount() => _played;
                public override void Start() { }
                public override void Stop() { }
                public override void PrepareToClose() { }
                public override void Dispose() { }
            }
        }

        // 打开会话可设成抛异常的声音驱动（哑设备），测失败路径
        private sealed class FlakyDriver : Ryujinx.Audio.Integration.IHardwareDeviceDriver
        {
            private readonly DummyHardwareDeviceDriver _d = new();
            public bool Fail;

            public static bool IsSupported => true;
            public float Volume { get => _d.Volume; set => _d.Volume = value; }
            public Ryujinx.Audio.Integration.IHardwareDeviceSession OpenDeviceSession(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction, IVirtualMemoryManager memoryManager, Ryujinx.Audio.Common.SampleFormat sampleFormat, uint sampleRate, uint channelCount)
                => Fail ? throw new InvalidOperationException("测试：打开声音会话失败") : _d.OpenDeviceSession(direction, memoryManager, sampleFormat, sampleRate, channelCount);
            public System.Threading.ManualResetEvent GetUpdateRequiredEvent() => _d.GetUpdateRequiredEvent();
            public System.Threading.ManualResetEvent GetPauseEvent() => _d.GetPauseEvent();
            public bool SupportsDirection(Ryujinx.Audio.Integration.IHardwareDeviceDriver.Direction direction) => _d.SupportsDirection(direction);
            public bool SupportsSampleRate(uint sampleRate) => true;
            public bool SupportsSampleFormat(Ryujinx.Audio.Common.SampleFormat sampleFormat) => true;
            public bool SupportsChannelCount(uint channelCount) => true;
            public void Dispose() => _d.Dispose();
        }

        private sealed class NullEvent : Ryujinx.Audio.Integration.IWritableEvent
        {
            public void Signal() { }
            public void Clear() { }
        }

        // 存档提交并发用例用：只记建文件次数的假存档文件系统（其余操作一律成功、什么也不做）
        private sealed class FakeSaveFs : LibHac.FsSystem.ISaveDataFileSystem
        {
            public int Creates;
            protected override LibHac.Result DoCreateFile(ref readonly LibHac.Fs.Path path, long size, LibHac.Fs.Fsa.CreateFileOptions option) { System.Threading.Interlocked.Increment(ref Creates); return LibHac.Result.Success; }
            protected override LibHac.Result DoCleanDirectoryRecursively(ref readonly LibHac.Fs.Path path) => LibHac.Result.Success;
            protected override LibHac.Result DoCommit() => LibHac.Result.Success;
            protected override LibHac.Result DoCreateDirectory(ref readonly LibHac.Fs.Path path) => LibHac.Result.Success;
            protected override LibHac.Result DoDeleteDirectory(ref readonly LibHac.Fs.Path path) => LibHac.Result.Success;
            protected override LibHac.Result DoDeleteDirectoryRecursively(ref readonly LibHac.Fs.Path path) => LibHac.Result.Success;
            protected override LibHac.Result DoDeleteFile(ref readonly LibHac.Fs.Path path) => LibHac.Result.Success;
            protected override LibHac.Result DoGetEntryType(out LibHac.Fs.DirectoryEntryType entryType, ref readonly LibHac.Fs.Path path) { entryType = default; return LibHac.Result.Success; }
            protected override LibHac.Result DoOpenDirectory(ref LibHac.Common.UniqueRef<LibHac.Fs.Fsa.IDirectory> outDirectory, ref readonly LibHac.Fs.Path path, LibHac.Fs.Fsa.OpenDirectoryMode mode) => LibHac.Result.Success;
            protected override LibHac.Result DoOpenFile(ref LibHac.Common.UniqueRef<LibHac.Fs.Fsa.IFile> outFile, ref readonly LibHac.Fs.Path path, LibHac.Fs.OpenMode mode) => LibHac.Result.Success;
            protected override LibHac.Result DoRenameDirectory(ref readonly LibHac.Fs.Path currentPath, ref readonly LibHac.Fs.Path newPath) => LibHac.Result.Success;
            protected override LibHac.Result DoRenameFile(ref readonly LibHac.Fs.Path currentPath, ref readonly LibHac.Fs.Path newPath) => LibHac.Result.Success;
            public override LibHac.Result CommitExtraData(bool updateTimeStamp) => LibHac.Result.Success;
            public override bool IsSaveDataFileSystemCacheEnabled() => false;
            public override LibHac.Result ReadExtraData(out LibHac.Fs.SaveDataExtraData extraData) { extraData = default; return LibHac.Result.Success; }
            public override void RegisterExtraDataAccessorObserver(LibHac.FsSystem.ISaveDataExtraDataAccessorObserver observer, LibHac.Fs.SaveDataSpaceId spaceId, ulong saveDataId) { }
            public override LibHac.Result RollbackOnlyModified() => LibHac.Result.Success;
            public int ExtraWrites;
            public override LibHac.Result WriteExtraData(in LibHac.Fs.SaveDataExtraData extraData) { System.Threading.Interlocked.Increment(ref ExtraWrites); return LibHac.Result.Success; }
        }

        // 存档提交并发用例用：把宿主目录原语包一层，Block 为真时「克隆工作目录」先报到再停住，等 Release 才往下做（11-01 审计 P0-2）
        private sealed class BlockingTransaction : Ryujinx.HLE.FileSystem.IHostDirectoryTransaction
        {
            private readonly Ryujinx.HLE.FileSystem.IHostDirectoryTransaction _inner;
            public volatile bool Block;
            public readonly System.Threading.ManualResetEventSlim InClone = new(false), Release = new(false);
            public BlockingTransaction(Ryujinx.HLE.FileSystem.IHostDirectoryTransaction inner) => _inner = inner;
            public bool Supported(string directory) => _inner.Supported(directory);
            public int CloneTree(string source, string destination)
            {
                if (Block) { InClone.Set(); Release.Wait(10000); }
                return _inner.CloneTree(source, destination);
            }
            public int Exchange(string a, string b) => _inner.Exchange(a, b);
            public int WriteToken(string directory, ReadOnlySpan<byte> value) => _inner.WriteToken(directory, value);
            public int ReadToken(string directory, Span<byte> value, out int length) => _inner.ReadToken(directory, value, out length);
            public int SyncDirectory(string directory) => _inner.SyncDirectory(directory);
            public int OpenRead(string path, out Microsoft.Win32.SafeHandles.SafeFileHandle handle) => _inner.OpenRead(path, out handle);
        }
    }
}
