using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS;
using System;
using System.IO;
using System.Text;
using ApplicationId = LibHac.ApplicationId;
using Path = System.IO.Path;

namespace S2A.Services
{
    // 带存档起局（S2A_SAVE_DIR=<目录>，相对路径按 App 的 Documents / Mac 的家目录解析）：目录下按存档类型分子目录，
    //   device/   → 设备存档（DeviceSaveData）
    //   account/  → 当前用户的账号存档（SaveData）
    // 每个子目录的内容 = 游戏挂载存档后看到的根目录。起局时（游戏跑之前）经 LibHac 挂上对应存档、清空、整棵拷入、提交——
    // Ryujinx 数据根跨局复用，不清空会读到上一局的自动存档。没给 S2A_SAVE_DIR 时不动存档（沿用数据根里已有的）。
    // 不认识的子目录、该游戏没有的存档类型、拷入失败一律报红。
    //   user      → 文件：建档时的用户号（32 位十六进制，同 Ryujinx Profiles.json 的 user_id）。游戏把用户号记在存档里（动森岛民按它认人），
    //               对不上会当新玩家走开场；有这个文件就以它为当前用户（EnsureSaveData 之前，账号存档也按它建）
    public static class Saves
    {
        public static void SelectUser(Switch device)
        {
            string root = Dir();
            string file = root == null ? null : Path.Combine(root, "user");

            if (file == null || !File.Exists(file))
            {
                return;
            }

            string hex = File.ReadAllText(file).Trim();
            Ryujinx.HLE.HOS.Services.Account.Acc.UserId id;

            try
            {
                id = new Ryujinx.HLE.HOS.Services.Account.Acc.UserId(hex);
            }
            catch (ArgumentException)
            {
                Red.Report("S2A_SAVE_DIR/user 不是 32 位十六进制用户号", hex);
                return;
            }

            var accounts = device.System.AccountManager;

            if (accounts.LastOpenedUser.UserId.ToString() != id.ToString())
            {
                accounts.AddUser("Player", Ryujinx.Common.EmbeddedResources.Read("Ryujinx.HLE/HOS/Services/Account/Acc/DefaultUserImage.jpg"), id);
                accounts.OpenUser(id);
            }

            Console.Error.WriteLine($"S2A 带存档起局：当前用户 {accounts.LastOpenedUser.UserId}");
        }

        public static string Dir()
        {
            // Platform-owned persistent state is not a seed import. In particular,
            // BootSpec.saveRoot must never trigger the destructive Apple seed path.
            if (Host.Configuration != null) return null;
            string dir = Environment.GetEnvironmentVariable("S2A_SAVE_DIR");

            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            return Path.IsPathRooted(dir) ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), dir);
        }

        public static void Seed(Switch device, ulong programId)
        {
            string root = Dir();

            if (root == null)
            {
                return;
            }

            if (!Directory.Exists(root))
            {
                Red.Report("S2A_SAVE_DIR 目录不存在", root);
                return;
            }

            var sources = new System.Collections.Generic.List<(string kind, string dir)>();
            foreach (string sub in Directory.EnumerateDirectories(root))
            {
                string kind = Path.GetFileName(sub);
                if (kind is "device" or "account") sources.Add((kind, sub));
                else Red.Report("S2A_SAVE_DIR 下不认识的存档类型目录（只认 device / account；用户号放 user 文件）", sub);
            }
            SeedFrom(device, programId, sources);
        }

        // 种入内核（S2A_SAVE_DIR 与存档位置 SaveData 共用，见 SaveFolder.cs）：每项 (类型, 目录)，目录内容 = 游戏挂载存档后看到的根目录。
        //   类型 account / device；save = 有账号存档写账号存档，这款只有设备存档就写设备存档（NX2Apple 引擎的 save/ 规矩，存档位置也用它）。
        //   清空 → 整棵拷入 → 提交；拷入或提交失败抛 InvalidOperationException（本次起局失败，已提交的旧档原样）
        internal static void SeedFrom(Switch device, ulong programId, System.Collections.Generic.IEnumerable<(string kind, string dir)> sources)
        {
            HorizonClient hc = device.System.LibHacHorizonManager.RyujinxClient;
            ApplicationId app = new(programId);

            foreach ((string kind0, string sub) in sources)
            {
                string kind = kind0;
                Result r;
                var u = device.System.AccountManager.LastOpenedUser.UserId;
                // 走 Shim 里有实现的重载（Ncm.ApplicationId + Fs.UserId → MountSaveDataImpl，类型 Account，同 Ryujinx IFileSystemProxy.OpenSaveDataFileSystem 的路子）；
                //   LibHac.Fs.SaveData 里收 Account.Uid 的两个重载是 throw NotImplementedException（0.21.0-alpha.133），10-20 塞尔达带账号存档起局 S2A_Init 当场失败
                Result Account() => LibHac.Fs.Shim.SaveData.MountSaveData(hc.Fs, "s2asave"u8, new LibHac.Ncm.ApplicationId(programId), new LibHac.Fs.UserId((ulong)u.High, (ulong)u.Low));

                if (kind == "device") r = hc.Fs.MountDeviceSaveData("s2asave"u8, app);
                else if (kind == "account") r = Account();
                else
                {
                    r = Account(); kind = "account";
                    if (r.IsFailure()) { r = hc.Fs.MountDeviceSaveData("s2asave"u8, app); kind = "device"; }
                }

                if (r.IsFailure())
                {
                    Red.Report($"带存档起局：挂 {kind} 存档失败（该游戏没有这类存档？）", r.ToStringWithName());
                    continue;
                }

                // 清空后拷入或提交失败：不提交（已提交的旧档原样留着），本次起局失败（审计 P2-176：原先只报红照样开局）
                try
                {
                    r = hc.Fs.CleanDirectoryRecursively("s2asave:/"u8);

                    if (r.IsSuccess())
                    {
                        r = CopyIn(hc, sub, "s2asave:", out int files, out long bytes);

                        if (r.IsSuccess())
                        {
                            r = hc.Fs.Commit("s2asave"u8);
                        }

                        if (r.IsSuccess())
                        {
                            Console.Error.WriteLine($"S2A 带存档起局：{kind} ← {sub}（{files} 个文件，{bytes} 字节）");
                        }
                    }

                    if (r.IsFailure())
                    {
                        throw new InvalidOperationException($"带存档起局：写入 {kind} 存档失败：{r.ToStringWithName()}");
                    }
                }
                finally
                {
                    hc.Fs.Unmount("s2asave"u8);
                }
            }
        }

        // 存档点副本（录手柄，见 ios/vpad.m）：把本游戏此刻已提交的存档整份拷到 dir，布局同 S2A_SAVE_DIR（account/ device/ + user），回放局直接拿来带存档起局。
        //   读 Ryujinx 数据根里目录存档的已提交副本 bis/user/save/<存档号>/0（Commit 返回后即是提交态），不再挂载游戏正开着的存档。
        //   账号存档只取当前用户的。失败报红不抛（录制照常）。返回拷了几类存档
        //   整份先拷进同级 <dir>.tmp、连 user 文件都写完才改名成 dir（审计 #859：原先边拷边写最终目录，中途抛错留下半份 .saveN，
        //   tools/padrec_cut.py 只看目录在不在就整棵拿去回放）；任何失败删掉临时目录、也不留 dir（旧的同名副本不是这一刻的存档），返回 0
        public static int Snapshot(Switch device, ulong programId, string dir)
        {
            string tmp = dir.TrimEnd(Path.DirectorySeparatorChar) + ".tmp";

            try
            {
                HorizonClient hc = device.System.LibHacHorizonManager.RyujinxClient;
                var u = device.System.AccountManager.LastOpenedUser.UserId;
                var user = new LibHac.Fs.UserId((ulong)u.High, (ulong)u.Low);
                SaveDataFilter filter = SaveDataFilter.Make(programId: programId, saveType: default, userId: default, saveDataId: default, index: default);
                using UniqueRef<SaveDataIterator> it = new();
                Result r = hc.Fs.OpenSaveDataIterator(ref it.Ref, SaveDataSpaceId.User, in filter);

                if (r.IsFailure())
                {
                    Red.Report("存档点副本：读存档索引失败", r.ToStringWithName());
                    Discard(tmp, dir);
                    return 0;
                }

                SaveDataIterator iterator = it.Get;

                return SnapshotFrom((out long n, Span<SaveDataInfo> infos) => iterator.ReadSaveDataInfo(out n, infos), user, u.ToString(), dir);
            }
            catch (Exception e)
            {
                Red.Report("存档点副本：拷贝失败", e.Message);
                Discard(tmp, dir);
                return 0;
            }
        }

        internal delegate Result ReadSaveDataInfos(out long count, Span<SaveDataInfo> infos);

        // 按存档索引逐条拷进临时目录，索引读完、全部拷完才换成 dir（单测直接喂索引）。索引读失败同拷贝失败：报红、删临时目录与同名旧副本、返回 0
        //   （11-01 全量审计 P1-16：原先读失败与「没有更多条目」同样 break，照样写 user、换掉旧副本，正式目录里是空的或半份存档）
        internal static int SnapshotFrom(ReadSaveDataInfos read, LibHac.Fs.UserId user, string userText, string dir)
        {
            int kinds = 0;
            string tmp = dir.TrimEnd(Path.DirectorySeparatorChar) + ".tmp";

            try
            {
                Span<SaveDataInfo> infos = stackalloc SaveDataInfo[8];

                if (Directory.Exists(tmp))
                {
                    Directory.Delete(tmp, true);
                }

                Directory.CreateDirectory(tmp);

                while (true)
                {
                    Result r = read(out long n, infos);

                    if (r.IsFailure())
                    {
                        Red.Report("存档点副本：读存档索引失败", r.ToStringWithName());
                        Discard(tmp, dir);
                        return 0;
                    }

                    if (n == 0)
                    {
                        break;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        SaveDataInfo info = infos[i];
                        string kind = info.Type == SaveDataType.Account && info.UserId == user ? "account" : info.Type == SaveDataType.Device ? "device" : null;

                        if (kind == null)
                        {
                            continue;
                        }

                        string save = Path.Combine(Ryujinx.Common.Configuration.AppDataManager.BaseDirPath, "bis", "user", "save", info.SaveDataId.ToString("x16"));
                        string src = Path.Combine(save, "0");

                        // 缺一类已提交目录：整次副本失败、旧副本也不留（审计 P2-22：原先 continue 后照样换上只含另一类的副本）
                        if (!Directory.Exists(src))
                        {
                            Red.Report($"存档点副本：{kind} 存档 {info.SaveDataId:x16} 没有已提交目录", src);
                            Discard(tmp, dir);
                            return 0;
                        }

                        string dst = Path.Combine(tmp, kind);

                        if (Directory.Exists(dst))
                        {
                            Directory.Delete(dst, true);
                        }

                        // 在存档事务的锁里整树克隆已提交版本（11-01 18:00 全量审计 P1-51：原先锁外逐文件拷，可拼出两次提交、大档卡住这次保存）
                        string why = Ryujinx.HLE.FileSystem.S2ASaveTransaction.CloneCommitted(save, dst);

                        if (why != null)
                        {
                            Red.Report($"存档点副本：{kind} 存档 {info.SaveDataId:x16} {why}", src);
                            Discard(tmp, dir);
                            return 0;
                        }

                        Console.Error.WriteLine($"S2A 存档点副本：{kind} → {Path.Combine(dir, kind)}（克隆已提交版本）");
                        kinds++;
                    }
                }

                // 一种存档都没有（这款没有存档）：不换上只有 user 的空副本（11-01 18:00 全量审计 P2-22）
                if (kinds == 0)
                {
                    Discard(tmp, dir);
                    return 0;
                }

                File.WriteAllText(Path.Combine(tmp, "user"), userText);

                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                Directory.Move(tmp, dir);
            }
            catch (Exception e)
            {
                Red.Report("存档点副本：拷贝失败", e.Message);
                Discard(tmp, dir);
                kinds = 0;
            }

            return kinds;
        }

        // 存档点副本没做成：临时目录与同名旧副本都删掉（回放局见不到 .saveN 就不带存档，不会拿半份或上一次的去回放）
        private static void Discard(string tmp, string dir)
        {
            try
            {
                if (Directory.Exists(tmp))
                {
                    Directory.Delete(tmp, true);
                }

                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception e)
            {
                Red.Report("存档点副本：清理没做成的副本失败", e.Message);
            }
        }

        // 种子拷入：只收普通文件和目录，碰到链接整次失败（审计 P2-194，同 SaveTransfer.Scan 的「不跟链接」判断）；
        //   按 1 MB 块写，不整文件进内存（审计 P2-176）
        private static Result CopyIn(HorizonClient hc, string hostDir, string savePath, out int files, out long bytes)
        {
            files = 0;
            bytes = 0;

            foreach (string d in Directory.EnumerateDirectories(hostDir))
            {
                NoLink(d);
                string p = savePath + "/" + Path.GetFileName(d);
                Result r = hc.Fs.CreateDirectory(U8(p));

                if (r.IsFailure())
                {
                    return r;
                }

                r = CopyIn(hc, d, p, out int f, out long b);
                files += f;
                bytes += b;

                if (r.IsFailure())
                {
                    return r;
                }
            }

            byte[] chunk = new byte[1 << 20];

            foreach (string f in Directory.EnumerateFiles(hostDir))
            {
                NoLink(f);
                using FileStream src = new(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
                long length = src.Length;
                byte[] p = U8(savePath + "/" + Path.GetFileName(f));
                Result r = hc.Fs.CreateFile(p, length);

                if (r.IsFailure())
                {
                    return r;
                }

                r = hc.Fs.OpenFile(out FileHandle h, p, OpenMode.Write);

                if (r.IsFailure())
                {
                    return r;
                }

                try
                {
                    long offset = 0;

                    while (offset < length)
                    {
                        int n = src.Read(chunk, 0, (int)Math.Min(chunk.Length, length - offset));

                        if (n == 0)
                        {
                            throw new IOException($"带存档起局：种子文件读到一半变短（{offset}/{length} 字节）：{f}");
                        }

                        r = hc.Fs.WriteFile(h, offset, chunk.AsSpan(0, n), WriteOption.None);

                        if (r.IsFailure())
                        {
                            return r;
                        }

                        offset += n;
                    }

                    r = hc.Fs.FlushFile(h);
                }
                finally
                {
                    hc.Fs.CloseFile(h);
                }

                if (r.IsFailure())
                {
                    return r;
                }

                files++;
                bytes += length;
            }

            return Result.Success;
        }

        private static void NoLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"带存档起局：种子里有链接，拒绝拷入：{path}");
            }
        }

        // LibHac 路径要 NUL 结尾的 UTF-8
        private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s + "\0");
    }
}
