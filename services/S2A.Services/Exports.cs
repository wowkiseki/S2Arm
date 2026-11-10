using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Audio.Integration;
using Ryujinx.Graphics.GAL;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace S2A.Services
{
    // NativeAOT 导出：原生层（runtime/）按 s2a_native.h 调这里。
    public static unsafe class Exports
    {
        // 显卡线（graphics/）登记渲染器工厂（在其程序集的模块初始化里设）；S2A_Init 用它建 Ryujinx 的显卡上下文
        public static Func<IRenderer> RendererFactory { get; set; }

        // 声音输出由平台装配登记；未登记时使用哑设备并报红。
        public static delegate*<IHardwareDeviceDriver> AudioFactory;

        // 相册截图 JPEG 编码器由平台装配登记（苹果：platform/apple/cs/capture，ImageIO）；HostConfiguration 给了编码器时以它为准（安卓）
        public static Ryujinx.HLE.UI.ScreenshotJpegEncoder ScreenshotEncoder { get; set; }

        // 显卡线登记：服务层与游戏登记都完成后调（起渲染循环），关停前调（停渲染循环）
        public static Action<Ryujinx.HLE.Switch, Ryujinx.Memory.Tracking.MemoryTracking> AfterBoot { get; set; }
        public static Action BeforeShutdown { get; set; }
        public static Action<Ryujinx.HLE.Switch> AfterShutdown { get; set; }
        // A session provider may call this only after it actually joins every guest
        // thread. Ordinary Shutdown has no such proof and does not invoke it.
        public static Action<Ryujinx.HLE.Switch> AfterGuestJoinedShutdown { get; set; }

        // A platform with an explicit session owner may own the complete shutdown
        // path. It must contain exceptions and publish failure to that owner.
        // Null preserves the existing Apple shutdown path below.
        public static Action ShutdownHandler { get; set; }

        // An explicit platform owner must contain cancellation callback failures
        // and publish them through its session status. Null keeps legacy behavior.
        public static Action CancelWaitsHandler { get; set; }

        // Asynchronous Horizon failures must reach the explicit session owner,
        // including failures after initialization has returned.
        public static Action<Exception> ServiceFailure { get; set; }

        private static CsApi* _api;
        private static readonly object _lifecycle = new();
        private enum SessionState { Empty, Active, Failed, ProcessExit }
        private enum ShutdownMode { ProcessExit, GuestJoined }
        private static SessionState _sessionState;
        private static SessionFailureOwner _sessionOwner;

        // This owner belongs only to the native C session entry. The explicit
        // HostConfiguration composition retains its own ServiceFailure owner.
        private sealed class SessionFailureOwner
        {
            private const int Initializing = 0, Active = 1, Retiring = 2, Failed = 3, Retired = 4, ProcessExit = 5;
            private const int NativeSessionFailed = 3; // s2a_session.h, not the managed SessionState enum.
            private int _phase;
            private ExceptionDispatchInfo _failure;
            private readonly ManualResetEventSlim _stopFinished = new(true);
            private bool _stopActive; // protected by _lifecycle
            private int _horizonStopAttempted, _horizonJoinCompleted;
            internal uint Generation { get; }
            internal bool Exiting => Volatile.Read(ref _phase) == ProcessExit;

            internal SessionFailureOwner()
            {
                if (Native.Imports.s2a_session_prepare() != 0) throw new InvalidOperationException("Native session control thread is unavailable");
                Generation = Native.Imports.s2a_session_generation();
            }

            internal void Fail(Exception error)
            {
                int phase = Volatile.Read(ref _phase);
                if (phase >= Failed || Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null) != null) return;
                for (;;)
                {
                    if (phase >= Failed) return;
                    int observed = Interlocked.CompareExchange(ref _phase, Failed, phase);
                    if (observed == phase) break;
                    phase = observed;
                }
                // No lifecycle/request lock, join, resource release or worker
                // allocation is allowed on this failing service thread.
                int result = Native.Imports.s2a_session_service_failed(Generation);
                Red.Report($"会话 {Generation} 服务失败，原生通知 rc={result}（1=普通运行只报红，禁止重启）", error.ToString(), allowFailFast: false);
            }

            internal void Activate()
            {
                if (Interlocked.CompareExchange(ref _phase, Active, Initializing) != Initializing)
                {
                    ThrowIfFailed();
                    throw new InvalidOperationException("Session stopped before initialization completed");
                }
                ThrowIfFailed();
            }

            internal void BeginRetirement()
            {
                if (Interlocked.CompareExchange(ref _phase, Retiring, Active) != Active)
                {
                    ThrowIfFailed();
                    throw new InvalidOperationException("Session cannot enter retirement");
                }
                ThrowIfFailed();
            }

            internal void ThrowIfFailed()
            {
                Volatile.Read(ref _failure)?.Throw();
                if (Volatile.Read(ref _phase) == Failed) throw new InvalidOperationException("Session failure is being published");
                if (Volatile.Read(ref _phase) == ProcessExit) throw new InvalidOperationException("Host process is exiting");
                // Native retirement timeout revokes this generation without
                // entering managed code on the controller thread.
                if (Native.Imports.s2a_host_session_state() == NativeSessionFailed) throw new OperationCanceledException("本代会话已失败，保留未退休资源");
            }

            internal void Retire()
            {
                if (Interlocked.CompareExchange(ref _phase, Retired, Retiring) != Retiring)
                {
                    ThrowIfFailed();
                    throw new InvalidOperationException("Session owner could not retire");
                }
            }

            internal void Exit()
            {
                Interlocked.Exchange(ref _phase, ProcessExit);
                Native.Imports.s2a_session_process_exit(Generation);
            }

            internal bool BeginStop()
            {
                if (_stopActive || Exiting) return false;
                _stopActive = true;
                _stopFinished.Reset();
                return true;
            }

            internal void FinishStop() => _stopFinished.Set();
            internal void WaitForStop() => _stopFinished.Wait();

            private void HorizonJoined() => Volatile.Write(ref _horizonJoinCompleted, 1);
            internal void StopHorizonStrict() => StopHorizon(rethrowFailure: true);

            internal void StopHorizon(bool rethrowFailure)
            {
                if (Interlocked.CompareExchange(ref _horizonStopAttempted, 1, 0) != 0)
                {
                    if (Volatile.Read(ref _horizonJoinCompleted) == 0)
                        throw new InvalidOperationException("本代 Horizon 停止/join 已尝试但未完成，禁止自动重试 join");
                    return;
                }
                HorizonHost.Stop(rethrowFailure, HorizonJoined);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "S2A_Init")]
        public static void Init()
        {
            try { Initialize(); }
            catch (Exception e) { Red.Report("S2A_Init 失败", e.ToString()); }
        }

        [UnmanagedCallersOnly(EntryPoint = "S2A_InitSession")]
        public static int InitSession()
        {
            lock (_lifecycle)
            {
                if (_sessionState != SessionState.Empty || Host.Device != null)
                {
                    Console.Error.WriteLine("【红】服务会话尚未完整清理，拒绝重新初始化");
                    return -1;
                }
                _sessionState = SessionState.Failed;
                try
                {
                    if (Native.Imports.s2a_session_rebuild_supported() == 0)
                    {
                        // The existing process-lifetime path owns failures;
                        // unsupported hosts must not prepare or retire a session.
                        InitializeCore(null, session: true);
                        _sessionState = SessionState.Active;
                        return 0;
                    }
                    var owner = new SessionFailureOwner();
                    _sessionOwner = owner;
                    Host.ReloadSessionEnvironment();
                    InitializeCore(null, session: true, owner.Fail);
                    owner.Activate();
                    _sessionState = SessionState.Active;
                    return 0;
                }
                catch (Exception e)
                {
                    _sessionOwner?.Fail(e);
                    Red.Report("S2A_InitSession 失败", e.ToString(), allowFailFast: false);
                    return -1;
                }
            }
        }

        // All hosts use the 1080 width baseline; explicit configuration requires valid surface dimensions.
        internal static void ApplyDisplayAspect(bool requireSurface)
        {
            if (Native.ScreenW == null || (requireSurface && Native.ScreenH == null))
            {
                if (requireSurface) throw new InvalidOperationException("Surface dimensions unavailable");
                return;
            }
            int width = Native.ScreenW();
            int height = requireSurface ? Native.ScreenH() : 1080;
            if (width <= 0 || height <= 0)
            {
                if (requireSurface) throw new InvalidOperationException("Surface dimensions unavailable");
                return;
            }
            Ryujinx.HLE.S2ADisplay.Aspect = width / 1080.0;
            // 比 16:9 高的屏（Duo 内屏 / iPad：原生层屏宽 1920、屏高 > 1080）：报给游戏的显示高按 屏高 ÷ 1080 加长（掌机 720 → 900），宽不变。
            //   旧入口不要求表面尺寸时也读屏高（原生层有就用）；读不到 / 不高于 1080 = 不加长（11-05 前高屏一律报原 16:9，第一层在高屏上不存在）
            int screenH = requireSurface ? height : Native.ScreenH != null ? Native.ScreenH() : 1080;
            Ryujinx.HLE.S2ADisplay.ScaleY = screenH > 1080 ? screenH / 1080.0 : 1.0;
        }

        // Platform compositions call this managed entry so initialization failures
        // remain observable. The legacy unmanaged Apple entry above retains its ABI.
        public static void Initialize(HostConfiguration configuration = null) => InitializeCore(configuration, session: false);

        private static void InitializeCore(HostConfiguration configuration, bool session, Action<Exception> sessionFailure = null)
        {
            if (!Native.Ready)
            {
                if (configuration != null) throw new InvalidOperationException("Native provider must be bound before service initialization");
                Native.UseLinked();
            }
            if (configuration != null)
            {
                if (RendererFactory == null || AfterBoot == null || BeforeShutdown == null || ServiceFailure == null)
                    throw new InvalidOperationException("Game renderer, worker, stop and failure callbacks are required");
                if (Native.GuestTick == null || Native.DetMode == null || Native.DetHostWrite == null)
                    throw new InvalidOperationException("Game clock and deterministic memory providers are required");
                if (!System.IO.File.Exists(System.IO.Path.Combine(configuration.GameDirectory, "exefs", "main.npdm")) ||
                    !System.IO.File.Exists(System.IO.Path.Combine(configuration.GameDirectory, "ctl", "control.nacp")) ||
                    !(System.IO.Directory.Exists(System.IO.Path.Combine(configuration.GameDirectory, "romfs")) ||
                      System.IO.File.Exists(System.IO.Path.Combine(configuration.GameDirectory, RomFsIndex.ImageName))))
                    throw new InvalidOperationException("Validated game data requires NPDM, complete NACP and RomFS");
                // Revision files may be content-pool symlinks. Validate the opened
                // content, not FileInfo.Length (the symlink text length on Unix).
                using var control = System.IO.File.OpenRead(System.IO.Path.Combine(configuration.GameDirectory, "ctl", "control.nacp"));
                if (control.Length != 0x4000)
                    throw new InvalidOperationException("Validated game data requires complete NACP (0x4000 bytes)");
            }

            Host.ValidateConfigurationTimeZone(configuration);

            Native.Mark("服务：C# 运行时起\0"u8);
            MemDiag.InstallOomDump();
            GuestMemory.InitDet();
            int nativeFailures = Native.SelfCheck();
            if ((configuration != null || session) && nativeFailures != 0)
                throw new InvalidOperationException("Native provider self-check failed: " + nativeFailures);

            // Validate explicit surface dimensions while preserving the shared 1080 width baseline.
            ApplyDisplayAspect(configuration != null);

            // 没有渲染器工厂也照常起服务链路（显卡出图报红），不让服务被显卡阻塞
            IRenderer renderer;

            if (RendererFactory == null)
            {
                if (session) throw new InvalidOperationException("S2A_InitSession：显卡线没有登记渲染器工厂");
                Red.Report("S2A_Init：显卡线没有登记渲染器工厂（Exports.RendererFactory），用桩渲染器（不出图）");
                renderer = new StubRenderer();
            }
            else
            {
                renderer = RendererFactory();
            }
            if ((configuration != null || session) && renderer == null)
                throw new InvalidOperationException("Game renderer factory returned no renderer");

            Native.Mark("服务：渲染器建\0"u8);

            string baseDir = configuration?.StateDirectory ?? Environment.GetEnvironmentVariable("S2A_RYU_DATA")
                ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "S2A", "ryujinx");

            var audio = new AudioPeakDriver(CreateAudioDriver());
            try
            {
                Host.Boot(renderer, audio, baseDir, configuration,
                    configuration != null ? ServiceFailure : sessionFailure);
            }
            catch (Host.InvalidSessionTimeZoneException error)
            {
                // Host validates the zone again before it installs the owner or
                // starts services. If a data link disappeared after preflight,
                // these two resources have not transferred to Switch.
                var failures = new System.Collections.Generic.List<Exception> { error };
                try { audio.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
                try { renderer.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
                if (failures.Count != 1) throw new AggregateException("Startup validation cleanup failed", failures);
                throw;
            }

            // 游戏数据目录（资源包解开后的布局：exefs/main.npdm、romfs/、ctl/control.nacp）
            string game = configuration?.GameDirectory ?? Environment.GetEnvironmentVariable("S2A_GAME_DATA");

            if (!string.IsNullOrEmpty(game))
            {
                Application.Register(Host.Device, game, requireSuccess: configuration != null);
            }
            else
            {
                if (session) throw new InvalidOperationException("S2A_InitSession：没给游戏数据目录（S2A_GAME_DATA）");
                Red.Report("S2A_Init：没给游戏数据目录（S2A_GAME_DATA），fs / am 拿不到游戏的 RomFS 与 NACP");
            }

            if (configuration != null || session)
            {
                RegisterChecked();
            }
            else Register();
            InputPump.Start(Host.Device);
            Cheats.Start();   // 金手指：$S2A_DOCS/cheats 有开着的条目才执行（缺省全关）
            AfterBoot?.Invoke(Host.Device, Dispatcher.Instance.Memory.Tracking);
            Native.Mark("服务：输入泵与渲染循环起\0"u8);
        }

        // 向原生层登记单一入口（单测可直接调）
        public static void Register()
        {
            int rc = RegisterCore();
            if (rc != 0) Red.Report("s2a_register_cs 失败（结构版本不符）", $"rc={rc} size={sizeof(CsApi)}");
        }

        public static void RegisterChecked()
        {
            int rc = RegisterCore();
            if (rc != 0) throw new InvalidOperationException("Native service callback registration failed: " + rc);
        }

        private static int RegisterCore()
        {
            if (Native.RegisterCs == null) throw new InvalidOperationException("Native service registration callback is absent");
            if (_api == null)
            {
                _api = (CsApi*)NativeMemory.AllocZeroed((nuint)sizeof(CsApi));
                _api->Size = (uint)sizeof(CsApi);
                _api->Svc = &Svc;
                _api->HandleClosed = &HandleClosed;
                _api->ThreadExit = &ThreadExit;
                _api->Shutdown = &Shutdown;
            }

            return Native.RegisterCs(_api);
        }

        // 分配拆账（S2A_ALLOC_TRACE）：本线程上次离开系统调用时的线程累计分配（0 = 还没进过）
        [ThreadStatic] private static long t_svcExit;

        [UnmanagedCallersOnly]
        private static int Svc(SvcCtx* ctx)
        {
            Dispatcher d = Dispatcher.Instance;

            if (d == null)
            {
                return SvcResult.Unhandled;
            }

            try
            {
                // 每帧分配拆账：0x21 在 Request 里按（服务, 命令）记；其余系统调用按号记，补全游戏线程进 C# 的分配
                if (Dispatcher.TraceAlloc)
                {
                    // 这条游戏线程上一次离开系统调用到这次进来之间的分配（直连回调：宽屏导出等；10-26 巫师3 R39 核对差额 65.6% 归不到线程）
                    long now = GC.GetAllocatedBytesForCurrentThread();
                    if (t_svcExit != 0 && now > t_svcExit) Dispatcher.AllocAccountBetween(now - t_svcExit);
                    try
                    {
                        if (ctx->Num == 0x21) return d.Svc(ctx);
                        uint num = ctx->Num;
                        long a0 = GC.GetAllocatedBytesForCurrentThread();
                        int r = d.Svc(ctx);
                        Dispatcher.AllocAccountSvc(num, GC.GetAllocatedBytesForCurrentThread() - a0);
                        return r;
                    }
                    finally { t_svcExit = GC.GetAllocatedBytesForCurrentThread(); }
                }

                return d.Svc(ctx);
            }
            catch (Exception e)
            {
                // 服务代码异常 = 我方或 Ryujinx 的缺陷：报红并给游戏一个失败结果，不回成功
                Red.Report($"服务调用异常 svc 0x{ctx->Num:X}", e is OutOfMemoryException ? $"{e}\n[S2A 内存] OOM 时：{MemDiag.Snapshot()}" : e.ToString());
                ctx->Regs[0] = Red.ResultUnknownCommand;
                return SvcResult.Handled;
            }
        }

        // 游戏关会话句柄后原生层回调：服务析构的异常同样报红，不让它穿出原生回调把进程 FailFast
        [UnmanagedCallersOnly]
        private static void HandleClosed(uint kind, ulong cookie)
        {
            try
            {
                Dispatcher.Instance?.HandleClosed(kind, cookie);
            }
            catch (Exception e)
            {
                Red.Report($"服务关会话异常（种类 {kind}）", e.ToString());
            }
        }

        [UnmanagedCallersOnly]
        private static void ThreadExit(uint thread)
        {
            Dispatcher.ResetThread();
            HorizonHost.ResetThread();
            t_svcExit = 0;
        }

        // 收局第一步（原生层 lifecycle.c 在等在途调用走完之前调）：让所有阻塞在服务等待里的调用立刻返回（bsd select / poll，
        // Ryujinx.Common.S2AShutdown），否则要等到游戏给的超时（10-21 暗黑2 29/50 局收局 bsd:u Select 没回）
        [UnmanagedCallersOnly(EntryPoint = "S2A_CancelWaits")]
        private static void CancelWaits()
        {
            if (CancelWaitsHandler != null) CancelWaitsHandler();
            else Ryujinx.Common.S2AShutdown.Request();
        }

        [UnmanagedCallersOnly(EntryPoint = "S2A_CancelSessionWaits")]
        public static int CancelSessionWaits(uint generation)
        {
            // Wait for InitSession/AfterBoot to finish before the native worker
            // requests graphics stop. This section never joins or disposes.
            lock (_lifecycle)
            {
                SessionFailureOwner owner = _sessionOwner;
                if (owner == null || owner.Generation != generation || owner.Exiting) return -1;
                try
                {
                    Ryujinx.Common.S2AShutdown.Request();
                    return 0;
                }
                catch (Exception error)
                {
                    owner.Fail(error);
                    Red.Report($"会话 {generation} 取消等待失败", error.ToString(), allowFailFast: false);
                    return -1;
                }
            }
        }

        // 直连图形层（hle_nvn.c pool_nvmap，按名取）：给 NVN 内存池的客体内存开 nvmap 句柄，nvnTextureGetNvRmSurfaceNVX 交给 NvRm 用
        [UnmanagedCallersOnly(EntryPoint = "S2A_NvMapFromGuest")]
        private static int NvMapFromGuest(ulong address, uint size) => Ryujinx.HLE.S2ADirect.NvMapFromGuest(address, size);

        [UnmanagedCallersOnly]
        private static void Shutdown(byte* why)
        {
            if (ShutdownHandler != null)
            {
                ShutdownHandler();
                return;
            }
            // Do this before taking _lifecycle: a timer shutdown can hold the
            // native request lock while this path joins Horizon threads.
            Volatile.Read(ref _sessionOwner)?.Exit();
            SessionFailureOwner owner;
            lock (_lifecycle)
            {
                owner = _sessionOwner;
                owner?.Exit();
            }
            // A failed stop owns live service objects until it returns. Wait
            // outside the lifecycle lock before ordinary process disposal.
            owner?.WaitForStop();
            lock (_lifecycle)
            {
                // 终局没有 guest join 证明；永远不能转回可重建的 Empty。
                _sessionState = SessionState.ProcessExit;
                ShutdownManaged(ShutdownMode.ProcessExit, Marshal.PtrToStringUTF8((nint)why));
            }
        }

        // Process exit must still attempt save/audio/service cleanup when a
        // graphics stop fails. Guest-joined retirement keeps its strict path.
        private static void ShutdownManaged(ShutdownMode mode, string why)
        {
            bool guestJoined = mode == ShutdownMode.GuestJoined;
            if (guestJoined) MemDiag.Stop();
            ShutdownStep(mode, "停止图形生产", () => BeforeShutdown?.Invoke());
            ShutdownStep(mode, "停止输入泵", InputPump.Stop);
            ShutdownStep(mode, "收起系统键盘", TextInput.CancelAll);   // 还开着的输入框按取消交回并收框，不留迟到的回复给下一局
            // 输入泵停止后不会再次复位音频事件；先唤醒 DSP，再等待服务/设备关停。
            ShutdownStep(mode, "唤醒音频处理", Host.ResumeAudioForShutdown);
            ShutdownStep(mode, "停止联机发现", Host.StopLanDiscovery);
            ShutdownStep(mode, "收局摘要", () =>
            {
                if (!guestJoined)
                {
                    Console.Error.WriteLine($"S2A 服务层关停：{why}");
                    // 普通退出没有 guest join 证明，保留在途请求诊断。
                    if (Dispatcher.InflightSummary() is string inflight)
                        Console.Error.WriteLine($"【红】收局时还在 C# 里没回的请求：{inflight}");
                }
                Console.Error.WriteLine(Red.Summary());
                Console.Error.WriteLine(Red.StubSummary());
                if (!guestJoined) Console.Error.WriteLine(Ryujinx.Common.S2AInputCaps.Summary());

                if (Dispatcher.TraceAlloc)
                {
                    Console.Error.WriteLine(Dispatcher.AllocSummary(all: true));
                }
            });

            // 存档位置 SaveData：当前存档写回（SaveFolder.cs）。要在停 Horizon / 关设备之前，读的是存档事务锁里克隆的已提交版本
            ShutdownStep(mode, "存档位置写回", () => SaveFolder.WriteBack(Host.Device));

            if (guestJoined)
            {
                Host.ShutdownSessionServices(_sessionOwner == null ? null : _sessionOwner.ThrowIfFailed,
                    _sessionOwner == null ? null : _sessionOwner.StopHorizonStrict);
            }
            else
            {
                ShutdownStep(mode, "停止内存诊断", MemDiag.Stop);
                ShutdownStep(mode, "停止 Horizon 服务", HorizonHost.Stop);
                ShutdownStep(mode, "关闭设备", () => Host.Device?.Dispose());
            }
            ShutdownStep(mode, "停止图形编码", () =>
            {
                // The strict caller retains the same Device until GPU disposal
                // and encoder retirement complete. ProcessExit never resets it.
                if (guestJoined) AfterGuestJoinedShutdown(Host.Device);
                else AfterShutdown?.Invoke(Host.Device);
            });
        }

        private static void ShutdownStep(ShutdownMode mode, string step, Action action)
        {
            if (mode == ShutdownMode.GuestJoined)
            {
                _sessionOwner?.ThrowIfFailed();
                action();
                _sessionOwner?.ThrowIfFailed();
                return;
            }
            try { action(); }
            catch (Exception error)
            {
                Red.Report($"普通退出：{step}失败，继续收尾", error.ToString(), allowFailFast: false);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "S2A_ShutdownSession")]
        public static int ShutdownSession()
        {
            lock (_lifecycle)
            {
                if (_sessionState != SessionState.Active) return -1;
                try
                {
                    // 只读生产 roster/reaper 与门计数；不能用 Device==null、park 或测试 token 代替。
                    if (Native.Imports.s2a_thread_session_joined() != 1 || Native.Imports.s2a_gate_inflight() != 0 || Dispatcher.InflightSummary() != null)
                        throw new InvalidOperationException("客体尚未全部 join 或仍有在途服务调用，禁止释放会话");
                    if (AfterGuestJoinedShutdown == null)
                        throw new InvalidOperationException("显卡线缺少 guest join 后的退休实现");

                    _sessionOwner?.BeginRetirement();
                    _sessionState = SessionState.Failed;
                    Ryujinx.Common.S2AShutdown.Request();
                    ShutdownManaged(ShutdownMode.GuestJoined, "进程内重启");
                    _sessionOwner?.ThrowIfFailed();
                    Ryujinx.Cpu.S2AUntrackedRegionPool.ResetSession();
                    _sessionOwner?.ThrowIfFailed();
                    Host.FinishShutdownSession();
                    _sessionOwner?.ThrowIfFailed();
                    _sessionOwner?.Retire();
                    _sessionOwner = null;
                    t_svcExit = 0;
                    _sessionState = SessionState.Empty;
                    return 0;
                }
                catch (Exception e)
                {
                    _sessionState = SessionState.Failed;
                    _sessionOwner?.Fail(e);
                    Console.Error.WriteLine($"【红】S2A_ShutdownSession 失败：{e}");
                    return -1;
                }
            }
        }

        // Only the native control thread calls this, after revoking guest
        // admission and draining guest calls. Failure is not permission to
        // dispose shared resources or reset the partially initialized session.
        [UnmanagedCallersOnly(EntryPoint = "S2A_StopFailedSession")]
        public static int StopFailedSession(uint generation)
        {
            SessionFailureOwner owner;
            lock (_lifecycle)
            {
                owner = _sessionOwner;
                if (owner == null || owner.Generation != generation || _sessionState == SessionState.ProcessExit || !owner.BeginStop()) return -1;
                _sessionState = SessionState.Failed;
            }
            try
            {
                Ryujinx.Common.S2AShutdown.Request();
                InputPump.Stop();
                TextInput.CancelAll();
                NativeTickSource.StopCatchUp();
                MemDiag.Stop();
                owner.StopHorizon(rethrowFailure: false);
                return 0;
            }
            catch (Exception error)
            {
                Red.Report($"会话 {generation} 失败后未能完全停止，保留资源", error.ToString(), allowFailFast: false);
                return -1;
            }
            finally { owner.FinishStop(); }
        }

        internal static IHardwareDeviceDriver CreateAudioDriver()
        {
            if (AudioFactory != null) return AudioFactory();
            Red.Report("声音输出工厂没有登记，使用哑设备", allowFailFast: false);
            return new DummyHardwareDeviceDriver();
        }
    }
}
