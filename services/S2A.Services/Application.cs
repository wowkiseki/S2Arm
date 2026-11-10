using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.HLE;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.Horizon.Sdk.Arp;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ApplicationId = LibHac.Ncm.ApplicationId;
using Path = System.IO.Path;

namespace S2A.Services
{
    // 把原生执行的游戏登记进 Ryujinx（照 Ryujinx 加载器的元数据步骤，不建 CPU 进程）：
    // 进程表（am / ns / acc 取 NACP、程序号）、fs 访问控制（npdm）、RomFS（fs OpenDataStorageByCurrentProcess）、存档、arp。
    // 游戏数据目录（资源包解开后的布局）：exefs/main.npdm、romfs/（目录）、ctl/control.nacp。
    public static unsafe class Application
    {
        public static ulong ProgramId { get; private set; }

        // 多程序合集（一个应用里多支程序，程序号末位 = 序号，Ryujinx 真值 Nca.ProgramIndex）：序号 0 = 游戏数据根本身，
        //   序号 n ≥ 1 在根下 programs/<n>/（exefs/、romfs/、ctl/ 与根同结构）；DLC 与系统数据仍在根，各序号共用。原生模块目录同一约定（runtime/include/s2a_programs.h）
        internal const int MaxPrograms = 16;
        internal static string ProgramDir(string gameDir, int index) =>
            index == 0 ? gameDir : Path.Combine(gameDir, "programs", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // 包里几支程序：从序号 0 起连续有 exefs/main.npdm 的个数（同 Ryujinx RegisterProgramMapInfo 只数连续段）
        internal static int ProgramCount(string gameDir)
        {
            int n = 0;
            while (n < MaxPrograms && !string.IsNullOrEmpty(gameDir) && File.Exists(Path.Combine(ProgramDir(gameDir, n), "exefs", "main.npdm"))) n++;
            return n;
        }

        // 本进程登记的游戏数据根（ExecuteProgram 切程序前核包里有没有目标序号用）
        internal static string GameDir { get; set; }

        internal static void Reset()
        {
            ProgramId = 0;
            LazyHostFile.CloseAll();
        }

        public static void Register(Switch device, string gameDir, bool requireSuccess = false)
        {
            ulong pid = Native.ProcessId();
            GameDir = gameDir;
            int programIndex = Host.Channel.Index;   // 用户通道跨进程内重启存活：ExecuteProgram(n) 后新局按 n 取程序（Ryujinx PartitionFileSystemExtensions.TryLoad 按通道 Index 挑 Program / Control）
            string programDir = ProgramDir(gameDir, programIndex);

            byte[] npdmRaw = File.ReadAllBytes(Path.Combine(programDir, "exefs", "main.npdm"));
            byte[] npdmBytes = NpdmCompat.Normalize(npdmRaw);   // Switch 2 的 ACID 布局（NpdmCompat.cs）
            Ryujinx.HLE.S2AHooks.Switch2 = !ReferenceEquals(npdmBytes, npdmRaw);   // 换了布局 = Switch 2 程序（语义随机型变的命令按它分派）
            MetaLoader metaLoader = new();
            metaLoader.Load(npdmBytes).ThrowIfFailure();
            metaLoader.GetNpdm(out Npdm npdm).ThrowIfFailure();
            ProgramId = npdm.Aci.ProgramId.Value;
            if (requireSuccess && ProgramId != Native.ProgramId())
                throw new InvalidOperationException("Application NPDM and native process program IDs disagree");
            if ((int)(ProgramId & 0xF) != programIndex)
                Red.Report("多程序合集：程序号末位与用户通道序号不一致", $"程序号 {ProgramId:X16}，序号 {programIndex}，目录 {programDir}");
            // 应用号 = 程序号去掉末位（Ryujinx NcaExtensions.Load：多程序应用的存档按主程序号建）；单程序款末位本来就是 0
            ulong applicationId = ProgramId & ~0xFul;

            BlitStruct<ApplicationControlProperty> nacp = new(1);
            string nacpPath = Path.Combine(programDir, "ctl", "control.nacp");

            if (File.Exists(nacpPath))
            {
                byte[] raw = File.ReadAllBytes(nacpPath);
                raw.AsSpan(0, Math.Min(raw.Length, nacp.ByteSpan.Length)).CopyTo(nacp.ByteSpan);
            }
            else
            {
                Red.Report("游戏数据缺 ctl/control.nacp", programDir);
            }

            // Horizon 服务（arp 等）要先就绪
            if (requireSuccess) HorizonHost.WaitServicesReady(device.System.ServiceTable, System.TimeSpan.FromSeconds(30));
            else device.System.ServiceTable.WaitServicesReady();
            Native.Mark("服务：npdm / NACP 读、等 Horizon 服务就绪\0"u8);

            device.System.LibHacHorizonManager.InitializeApplicationClient(new ProgramId(ProgramId), in npdm);
            RegisterProgramMap(device, applicationId, ProgramCount(gameDir), requireSuccess);

            ref readonly Meta meta = ref npdm.Meta;
            ProcessResult result = new(metaLoader, nacp, false, false, null, pid, 0, meta.MainThreadPriority, meta.MainThreadStackSize, device.System.State.DesiredTitleLanguage);
            device.Processes.S2ARegisterApplication(result);

            // Ryujinx 加载器（FileSystemExtensions）同一步：LibHac 的 bcat 等按它找当前应用（漏了 bcat 建投递缓存存储回 0x147a，对拍查出）
            device.System.LibHacHorizonManager.ArpIReader.ApplicationId = new LibHac.ApplicationId(ProgramId);

            string romfs = Path.Combine(programDir, "romfs");

            IStorage imageStorage = RomFsIndex.OpenImage(programDir);

            if (imageStorage != null)
            {
                device.Configuration.VirtualFileSystem.SetRomFs(pid, imageStorage);
                Native.Mark("服务：RomFS 单文件镜像\0"u8);
            }
            else if (Directory.Exists(romfs))
            {
                // 出包时预建的索引（RomFsIndex）优先：免去起局枚举十几万文件；没有就按目录现建（慢，打一行提示）
                string index = Path.Combine(programDir, RomFsIndex.FileName);
                string overlay = Path.Combine(programDir, "ui_overlay", "romfs");   // 界面布局覆盖层（RomFsIndex.OverlayFiles）：同路径读覆盖层那份
                IStorage image;

                if (File.Exists(index))
                {
                    image = RomFsIndex.Open(romfs, index, overlay);
                }
                else
                {
                    Console.Error.WriteLine($"S2A RomFS：游戏数据里没有预建索引 {RomFsIndex.FileName}，按目录现建（慢；出包应生成）");
                    image = RomFsFromDirectory(romfs, overlay);
                }

                device.Configuration.VirtualFileSystem.SetRomFs(pid, image);   // 镜像本身按偏移读：各数据存储会话互不共享读位置（#883），大读也不租中间数组
                Native.Mark("服务：RomFS 目录表建\0"u8);
            }
            else
            {
                Red.Report("游戏数据缺 romfs/", programDir);
            }

            Aoc.Register(gameDir);   // DLC：aoc.tsv 里的全部登记为已拥有（有数据的挂 aoc/<idx>/，仅授权的挂载时报红）

            Saves.SelectUser(device);
            Result saveResult = ProcessLoaderHelper.EnsureSaveData(device, new ApplicationId(applicationId), nacp);
            if (requireSuccess) saveResult.ThrowIfFailure();
            Saves.Seed(device, applicationId);
            if (Saves.Dir() == null) SaveFolder.ReadIn(device, applicationId);   // 存档位置 SaveData：玩家放了新存档就读进来（SaveFolder.cs）；测试 / 导入种子局不读
            Native.Mark("服务：存档\0"u8);

            // Ryujinx 加载器原本做的显卡侧三步（FileSystemExtensions / ArmProcessContext）：着色器缓存按程序号、GPU 认得客体内存、宿主就绪
            // 着色器磁盘缓存：Ryujinx 原版由界面配置置位、缺省 false（不置 = 缓存整个关，不读不写，包里的预编缓存也加载不了）；
            // 要在 RegisterProcess 之前（ShaderCache 随 PhysicalMemory 构造时取路径）
            Ryujinx.Graphics.Gpu.GraphicsConfig.EnableShaderCache = true;
            Ryujinx.Graphics.Gpu.GraphicsConfig.TitleId = ProgramId.ToString("X16");
            device.Gpu.RegisterProcess(pid, Dispatcher.Instance.Memory);
            device.Gpu.HostInitalized.Set();
            Native.Mark("服务：显卡登记进程（着色器缓存）\0"u8);

            RegisterArp(device.System.ServiceTable.ArpWriter, pid,
                MemoryMarshal.Cast<byte, Ryujinx.Horizon.Sdk.Ns.ApplicationControlProperty>(nacp.ByteSpan)[0]);

            Native.Mark("服务：arp 登记\0"u8);
            Console.Error.WriteLine($"S2A 应用登记：程序号 {ProgramId:X16}、pid {pid}、{result.Name} {result.DisplayVersion}");
        }

        // 程序序号表（Ryujinx ProcessLoaderHelper.RegisterProgramMapInfo，每次加载都登记）：序号 i → 程序号 应用号 + i、主程序号 = 应用号。
        //   fs 按它答 GetProgramIndexForAccessLog 等；count 0（数据根没有 exefs/main.npdm）不登记，与上游 programCount <= 0 同
        internal static void RegisterProgramMap(Switch device, ulong applicationId, int count, bool requireSuccess)
        {
            if (count <= 0) return;
            ProgramIndexMapInfo[] map = new ProgramIndexMapInfo[count];
            for (int i = 0; i < count; i++)
            {
                map[i].ProgramId = new ProgramId(applicationId + (uint)i);
                map[i].MainProgramId = new ApplicationId(applicationId);
                map[i].ProgramIndex = (byte)i;
            }
            Result r = device.System.LibHacHorizonManager.NsClient.Fs.RegisterProgramIndexMapInfo(map);
            if (r.IsFailure())
            {
                if (requireSuccess) r.ThrowIfFailure();
                Red.Report("程序序号表登记失败", $"应用号 {applicationId:X16}，{count} 支，结果 {r.ToStringWithName()}");
            }
            else if (count > 1) Console.Error.WriteLine($"S2A 多程序合集：程序序号表登记 {count} 支（应用号 {applicationId:X16}，本局序号 {Host.Channel.Index}）");
        }

        // The registrar owns its allocated instance ID. Looking it up by pid
        // before the updater binds that pid only yields InvalidPid + default(0).
        private static void RegisterArp(IWriter writer, ulong pid,
            Ryujinx.Horizon.Sdk.Ns.ApplicationControlProperty control)
        {
            writer.AcquireRegistrar(out IRegistrar registrar).AbortOnFailure();
            registrar.SetApplicationControlProperty(control).AbortOnFailure();
            registrar.SetApplicationLaunchProperty(new ApplicationLaunchProperty
            {
                ApplicationId = new Ryujinx.Horizon.Sdk.Ncm.ApplicationId(ProgramId),
                Version = 0x00,
                Storage = Ryujinx.Horizon.Sdk.Ncm.StorageId.BuiltInUser,
                PatchStorage = Ryujinx.Horizon.Sdk.Ncm.StorageId.None,
                ApplicationKind = ApplicationKind.Application,
            }).AbortOnFailure();

            registrar.Issue(out ulong instanceId).AbortOnFailure();
            writer.AcquireApplicationProcessPropertyUpdater(out IUpdater updater, instanceId).AbortOnFailure();
            updater.SetApplicationProcessProperty(pid, new ApplicationProcessProperty { ProgramIndex = (byte)Host.Channel.Index }).AbortOnFailure();
        }

        // 解开的 RomFS 目录 → RomFS 镜像（游戏经 fs 读的是镜像格式）。目录表由 LibHac 生成；文件数据按需打开（句柄缓存），
        // 不把几万个文件同时打开，也不拷贝数据
        public static IStorage RomFsFromDirectory(string root, string overlayDir = null)
        {
            RomFsBuilder builder = new();
            string full = Path.GetFullPath(root);
            Dictionary<string, string> overlay = RomFsIndex.OverlayFiles(overlayDir);

            // 按相对路径序号序加入（与预建索引 RomFsIndex 同序）：LibHac 建出的镜像布局随加入顺序变，同序才逐字节相同
            List<string> files = new(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories));
            files.RemoveAll(RomFsIndex.IsHostJunk);
            files.Sort(StringComparer.Ordinal);

            foreach (string file in files)
            {
                string relPath = Path.GetRelativePath(full, file).Replace('\\', '/');
                string src = overlay != null && overlay.TryGetValue(relPath, out string over) ? over : file;
                // Android revisions can link to an immutable content pool.
                using FileStream content = File.OpenRead(src);
                builder.AddFile("/" + relPath, new LazyHostFile(src, content.Length));
            }

            return builder.Build();
        }
    }

    // 只读、按需打开的宿主文件：读时经句柄缓存取 FileStream（缓存满了关最久没用的）。
    // 锁只管最近使用表与登记，打开文件和 pread 都在锁外（10-26 动森 Mac 满载 90 秒黑屏：全局锁包着阻塞的 pread，一次慢读让所有 RomFS 读排队）；
    // _busy = 正在锁外读这个文件的线程数，淘汰时跳过它们（句柄不会在读到一半时被关）
    sealed class LazyHostFile : IFile
    {
        private const int MaxOpen = 128;
        private static readonly object _lock = new();
        private static readonly LinkedList<LazyHostFile> _lru = new();

        /// <summary>单测钩子：锁外 pread / 打开文件之前调（参数 = 宿主路径），null = 不调。</summary>
        internal static Action<string> S2ATestBeforeRead, S2ATestBeforeOpen;

        internal static void CloseAll()
        {
            lock (_lock)
            {
                foreach (LazyHostFile file in _lru)
                {
                    if (file._busy != 0) throw new InvalidOperationException("RomFS 读取尚未返回，禁止关闭");
                    file._stream.Dispose();
                    file._stream = null;
                    file._node = null;
                }
                _lru.Clear();
            }
        }

        private readonly string _path;
        private readonly long _size;
        private FileStream _stream;
        private LinkedListNode<LazyHostFile> _node;
        private int _busy;

        // 诊断（缺省关）：S2A_ROMFS_READ_MBPS=<MB/s> 时每次 RomFS 读完按「字节 ÷ 吞吐」睡在读线程上，模拟卡带读速，只作复现手段（11-05 假面骑士重复加载同一 AssetBundle），不得变成产品行为、不许用它修崩溃（维护者 11-05）
        private static readonly double ReadDelayBytesPerSec = double.TryParse(Environment.GetEnvironmentVariable("S2A_ROMFS_READ_MBPS"), out double mbps) && mbps > 0 ? mbps * 1024 * 1024 : 0;
        private static int _readDelayNoted;

        public LazyHostFile(string path, long size)
        {
            _path = path;
            _size = size;
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            FileStream stream = null;

            lock (_lock)
            {
                if (_stream != null)
                {
                    Touch();
                    _busy++;
                    stream = _stream;
                }
            }

            if (stream == null)
            {
                // 打开（含 stat）也在锁外：10-26 巫师3 Mac 满载局 nx-84 卡在 FileStream 构造的 FStat 里、拿着锁，游戏主线程等锁整局冻住
                S2ATestBeforeOpen?.Invoke(_path);
                FileStream opened = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);

                // 长度来自目录枚举或预建索引：文件被换过（索引过期）就对不上，读出的数据会错位。
                //   回失败、不按索引长度补零当完整（审计 #1534 P1-6）；不缓存这个句柄，下次读再核
                if (opened.Length != _size)
                {
                    Red.Report("RomFS 文件长度与索引不符（romfs.s2aidx 过期？），本次读回失败", $"{_path}：索引 {_size}，实际 {opened.Length}");
                    opened.Dispose();
                    bytesRead = 0;
                    return ResultFs.HostFileDataCorrupted.Value;
                }

                lock (_lock)
                {
                    if (_stream == null)
                    {
                        EvictOne();
                        _stream = opened;
                        _node = _lru.AddFirst(this);
                        opened = null;
                    }
                    else
                    {
                        Touch();   // 别的线程先打开了：用它那份，自己这份关掉
                    }

                    _busy++;
                    stream = _stream;
                }

                opened?.Dispose();
            }

            try
            {
                S2ATestBeforeRead?.Invoke(_path);
                int n = (int)Math.Max(0, Math.Min(destination.Length, _size - offset));

                try
                {
                    // 一次 pread 可以读不满（中断、大块）：读到满或到文件尾为止
                    bytesRead = 0;

                    while (bytesRead < n)
                    {
                        int r = RandomAccess.Read(stream.SafeFileHandle, destination[(int)bytesRead..n], offset + bytesRead);

                        if (r == 0)
                        {
                            break;
                        }

                        bytesRead += r;
                    }
                }
                catch (IOException e) when (e.HResult == EFault)
                {
                    if (!ReadViaBounce(stream.SafeFileHandle, destination[..n], offset, out bytesRead))
                    {
                        return ResultFs.UnexpectedInLocalFileSystemE.Value;
                    }
                }

                // 长度在打开时核过，界内还读不满 = 文件在打开之后变了（审计 #1534 P1-6）：回失败，调用方不得补零当完整
                if (bytesRead != n)
                {
                    Red.Report("RomFS 文件短读：界内没读满，本次读回失败", $"{_path} 偏移 0x{offset:X}：要 {n}，读到 {bytesRead}");
                    return ResultFs.HostFileDataCorrupted.Value;
                }

                if (ReadDelayBytesPerSec > 0 && bytesRead > 0)
                {
                    if (System.Threading.Interlocked.Exchange(ref _readDelayNoted, 1) == 0)
                    {
                        Console.Error.WriteLine($"[S2A RomFS] 读限速已开：{ReadDelayBytesPerSec / 1048576:0.#} MB/s（S2A_ROMFS_READ_MBPS，诊断）");
                    }

                    System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(bytesRead / ReadDelayBytesPerSec));
                }

                return Result.Success;
            }
            finally
            {
                lock (_lock)
                {
                    _busy--;
                }
            }
        }

        // 目标是游戏内存（IStorage.Read 的 OutBuffer 直接是 GetWritableRegion(tracked) 的视图）：取区域时已解开写跟踪保护，但读盘期间页可能又被保护
        //   （空洞骑士 10-27 切房转场读 sharedassets*.assets.resS：pread 由内核往只读 / 不可读页写，不走缺页处理、直接 EFAULT「Bad address」，游戏收到错误后崩）。
        //   内核的写不能交给写跟踪：先读进宿主中转缓冲，再报一次写（解保护、GPU 改过的子页先回写，同 GetWritableRegion(tracked)），由 CPU 拷进去。
        //   给不出游戏地址（远端开着、目标不在本线程取过的区域里）就报不了写：不拷、回 false，读盘回失败（11-01 审计 P2-21：原先只报红照拷，
        //   往仍受保护的页写即宿主崩溃，写进去了跟踪器也不知道）
        private const int EFault = 14;
        private static long _bounceCount;

        private unsafe bool ReadViaBounce(SafeFileHandle handle, Span<byte> destination, long offset, out long bytesRead)
        {
            // 写钩子收游戏地址：远端连续块的目标视图是影子页、指针不是游戏地址，按取区域时的游戏地址换算（11-01 审计 P1-17）
            ulong host = (ulong)Unsafe.AsPointer(ref MemoryMarshal.GetReference(destination));
            bytesRead = 0;

            if (!GuestMemory.TryGuestAddress(host, (ulong)destination.Length, out ulong va))
            {
                Red.Report("读盘中转：目标不在本线程取过的游戏内存区域里，给不出游戏地址，报不了写、不拷，读盘回失败", $"{Path.GetFileName(_path)} 偏移 0x{offset:X}，宿主 0x{host:X} +0x{destination.Length:X}");
                return false;
            }

            long k = System.Threading.Interlocked.Increment(ref _bounceCount);

            if (k <= 8 || (k & (k - 1)) == 0)
            {
                Console.Error.WriteLine($"[fs] 读盘目标页写保护（EFAULT）改中转读：第 {k} 次，{Path.GetFileName(_path)} 偏移 0x{offset:X}，目标 0x{va:X} +0x{destination.Length:X}");
            }

            byte[] buf = ArrayPool<byte>.Shared.Rent(destination.Length);

            try
            {
                int got = 0;

                while (got < destination.Length)
                {
                    int r = RandomAccess.Read(handle, buf.AsSpan(got, destination.Length - got), offset + got);

                    if (r == 0)
                    {
                        break;
                    }

                    got += r;
                }

                if (GuestMemory.WriteHook != null)
                {
                    GuestMemory.WriteHook(va, (ulong)got);
                }

                buf.AsSpan(0, got).CopyTo(destination);
                bytesRead = got;

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        // 以下两个持 _lock 调
        private void Touch()
        {
            if (_node != _lru.First)
            {
                _lru.Remove(_node);
                _lru.AddFirst(_node);
            }
        }

        // 满了关最久没用、且没人正在读的那个（都在读就暂时超出上限）
        private static void EvictOne()
        {
            if (_lru.Count < MaxOpen)
            {
                return;
            }

            for (LinkedListNode<LazyHostFile> n = _lru.Last; n != null; n = n.Previous)
            {
                LazyHostFile old = n.Value;

                if (old._busy == 0)
                {
                    _lru.Remove(n);
                    old._stream.Dispose();
                    old._stream = null;
                    old._node = null;
                    return;
                }
            }
        }

        protected override Result DoGetSize(out long size)
        {
            size = _size;
            return Result.Success;
        }

        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => ResultFs.WriteUnpermitted.Value;
        protected override Result DoFlush() => Result.Success;
        protected override Result DoSetSize(long size) => ResultFs.WriteUnpermitted.Value;

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
            => ResultFs.NotImplemented.Value;
    }
}
