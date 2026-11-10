using Ryujinx.HLE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace S2A.Services
{
    // 存档位置 SaveData（11-09 维护者定：每款游戏一个固定文件夹，里面就是原生存档格式；放进去就能用；Mac ↔ iPhone 原样拷就互通）。
    //   路径：Mac 由作业层给 S2A_SAVE_FOLDER = <存放位置>/games/<英文名>/SaveData（pack/jobs/build.py）；iOS 没给时取本款文档根 S2A_DOCS
    //   （多款容器选游戏后 = Documents/games/<游戏号>，runtime/src/launchctx.c）下的 SaveData，单款包没有 S2A_DOCS = Documents/SaveData。
    //   其余（安卓平台自管存档、Mac 上不经作业层的开发起局）不启用。
    //   起局（游戏开跑前，Application.Register）：存档位置的内容和 App 上次写入 / 读入时不一样 → 玩家放了新的 → 识别后整棵换掉当前存档；一样 → 不动。
    //   退出（收局，存档服务还在）：当前存档按固定结构写回 save/（账号存档，只有设备存档的游戏是设备存档）+ save_device/（两种都有时的设备存档）；
    //   这一局里存档位置被玩家动过就不写回（保住刚放进来的，下次起局读它）；起局时放进来的没认出也不写回（原样留给玩家改）。
    //   「一样不一样」= 存档位置里所有文件的相对路径、大小、修改时间的摘要（不逐字节算哈希），记在数据根的 savedata-state.txt；
    //   认不出的原因写数据根的 savedata-problem.txt（存档管理页读它给玩家看），读入成功就删掉。
    //   识别规则同电脑端 pack/jobs/save_formats.py（JKSV / Checkpoint / Ryujinx / yuzu / NX2Apple / 裸文件），两端都走这一份
    public static class SaveFolder
    {
        public const string StateFile = "savedata-state.txt", ProblemFile = "savedata-problem.txt";
        static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini", "edizon_save_metadata.json" };
        static readonly HashSet<string> RyuMeta = new() { "ExtraData0", "ExtraData1" };
        static string _atLaunch;   // 起局时存档位置的摘要：退出时对不上 = 这一局里被玩家动过
        static ulong _programId;
        static bool _keep;         // 起局时放进来的没认出：这一局不写回，原样留给玩家改（不然退出时被当前存档盖掉，玩家放错的东西就没了）

        public static string Folder()
        {
            if (Host.Configuration != null) return null;   // 平台自管存档（安卓）不走这条
            string dir = Environment.GetEnvironmentVariable("S2A_SAVE_FOLDER");
            if (!string.IsNullOrEmpty(dir)) return dir;
            if (!OperatingSystem.IsIOS()) return null;
            string docs = Environment.GetEnvironmentVariable("S2A_DOCS");
            return Path.Combine(string.IsNullOrEmpty(docs) ? Environment.GetFolderPath(Environment.SpecialFolder.Personal) : docs, "SaveData");
        }
        static string DataRoot() => Ryujinx.Common.Configuration.AppDataManager.BaseDirPath;

        // ---- 摘要 ----
        internal static List<string> Files(string dir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, f);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.') || Skip.Contains(p))) continue;
                list.Add(rel);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }
        internal static string Digest(string dir)
        {
            var files = Files(dir);
            if (files.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (string rel in files)
            {
                var fi = new FileInfo(Path.Combine(dir, rel));
                sb.Append(rel).Append('\t').Append(fi.Length).Append('\t').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        // ---- 识别：→ [(类型, 目录)]，类型 save / account / device；认不出 / 不是这款 → why 非空 ----
        static bool HasFiles(string d) => Files(d).Any(f => !RyuMeta.Contains(Path.GetFileName(f)));
        static List<string> Subdirs(string d) => Directory.EnumerateDirectories(d).Where(x => !Path.GetFileName(x).StartsWith('.')).OrderBy(x => x, StringComparer.Ordinal).ToList();
        static List<string> TopNames(string d) => Directory.EnumerateFileSystemEntries(d).Select(Path.GetFileName).Where(n => !n.StartsWith('.') && !Skip.Contains(n) && !RyuMeta.Contains(n)).ToList();
        static string Latest(string d) => Subdirs(d).OrderBy(x => Directory.GetLastWriteTimeUtc(x)).ThenBy(x => x, StringComparer.Ordinal).LastOrDefault() ?? d;
        static readonly Regex Hex16 = new("^[0-9a-fA-F]{16}$"), Hex32 = new("^[0-9a-fA-F]{32}$"), Checkpoint = new(@"^0x([0-9a-fA-F]{16})(?: .*)?$");

        // ownTop：这款游戏自己写出的存档顶层名字（当前存档已提交副本的顶层），只用来判「外面唯一一层文件夹是包装还是游戏自己的目录」
        internal static List<(string kind, string dir)> Detect(string folder, ulong programId, ICollection<string> ownTop, out string why)
        {
            why = null;
            string pid = programId.ToString("x16");
            var subs = Subdirs(folder);
            var names = subs.Select(Path.GetFileName).ToList();
            // ① 固定结构 / NX2Apple：save / save_device（旁边的 .work / .new 是未提交副本）
            if (names.Count > 0 && names.All(n => n is "save" or "save_device" || n.EndsWith(".work") || n.EndsWith(".new")) && names.Any(n => n is "save" or "save_device"))
            {
                var r = new List<(string, string)>();
                string save = Path.Combine(folder, "save"), dev = Path.Combine(folder, "save_device");
                if (Directory.Exists(save) && HasFiles(save)) r.Add(("save", save));
                if (Directory.Exists(dev) && HasFiles(dev)) r.Add(("device", dev));
                if (r.Count > 0) return r;
            }
            // ② Ryujinx 导出（…/<16 位编号>/0，同级 ExtraData0 开头 0x40 字节是存档属性：程序号 u64、用户号 16 字节、系统存档号 u64、类型 u8 在 0x20）
            var ryu = new List<(string kind, string dir)>();
            foreach (string d in Directory.EnumerateDirectories(folder, "0", SearchOption.AllDirectories))
            {
                string parent = Path.GetDirectoryName(d), meta = Path.Combine(parent, "ExtraData0");
                if (!Hex16.IsMatch(Path.GetFileName(parent)) || !File.Exists(meta) || new FileInfo(meta).Length < 0x40) continue;
                byte[] a = new byte[0x40]; using (var s = File.OpenRead(meta)) s.ReadExactly(a);
                ulong prog = BitConverter.ToUInt64(a, 0), staticId = BitConverter.ToUInt64(a, 0x18); byte type = a[0x20];
                if (prog == 0 || staticId != 0 || type is not (1 or 3)) continue;
                if (prog != programId) { why = $"这是另一款游戏的存档（程序号 {prog:x16}）"; continue; }
                ryu.Add((type == 1 ? "account" : "device", d));
            }
            if (ryu.Count > 0) { why = null; return ryu; }
            if (why != null) return null;
            // ③ yuzu：…/0000000000000000/<32 位用户号>/<程序号>/（用户号全 0 = 设备存档）
            foreach (string d in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
            {
                string user = Path.GetFileName(Path.GetDirectoryName(d)), zero = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(d)) ?? "");
                if (!Hex16.IsMatch(Path.GetFileName(d)) || !Hex32.IsMatch(user) || zero != "0000000000000000") continue;
                if (!Path.GetFileName(d).Equals(pid, StringComparison.OrdinalIgnoreCase)) { why = $"这是另一款游戏的存档（程序号 {Path.GetFileName(d).ToLowerInvariant()}）"; continue; }
                return [(user.Trim('0').Length == 0 ? "device" : "account", d)];
            }
            if (why != null) return null;
            // ④ Checkpoint：0x<程序号> <游戏名>/<时间戳>/，取最新一份
            foreach (string d in Directory.EnumerateDirectories(folder, "0x*", SearchOption.AllDirectories))
            {
                var m = Checkpoint.Match(Path.GetFileName(d)); if (!m.Success) continue;
                if (!m.Groups[1].Value.Equals(pid, StringComparison.OrdinalIgnoreCase)) { why = $"这是另一款游戏的存档（程序号 {m.Groups[1].Value.ToLowerInvariant()}）"; continue; }
                return [("save", Latest(d))];
            }
            if (why != null) return null;
            // ⑤ JKSV / 裸文件：路径里没有程序号。JKSV/<游戏名>/<用户名 - 时间>/：往下走到第一层有文件的目录，同层多份取最新；
            //   裸文件：外面只套着一层文件夹（拖进来的整个文件夹），且那层名字不是这款游戏自己存档里的顶层名字 → 剥掉；套着的 16 / 32 位十六进制目录也剥
            string cur = folder;
            for (int guard = 0; guard < 8; guard++)
            {
                var top = TopNames(cur);
                bool files = Directory.EnumerateFiles(cur).Any(f => { string n = Path.GetFileName(f); return !n.StartsWith('.') && !Skip.Contains(n); });
                var dirs = Subdirs(cur);
                if (files || dirs.Count == 0) break;
                if (dirs.Count == 1)
                {
                    string n = Path.GetFileName(dirs[0]);
                    if (n == "cheats") break;   // 只有金手指：下面判掉，不当包装层钻进去
                    if (n == "JKSV" || Hex16.IsMatch(n) || Hex32.IsMatch(n) || !ownTop.Contains(n)) { cur = dirs[0]; continue; }
                    break;
                }
                // 多个子目录：全是 JKSV 时间戳目录（「用户名 - 2026.11.09 @ 12.00.00」）→ 取最新；否则这层就是存档根
                if (dirs.All(x => Regex.IsMatch(Path.GetFileName(x), @" - \d{4}\.\d{2}\.\d{2} @ \d{2}\.\d{2}\.\d{2}$"))) { cur = Latest(cur); continue; }
                break;
            }
            if (TopNames(cur).Count == 1 && TopNames(cur)[0] == "cheats") { why = "放进来的是金手指（cheats），不是存档"; return null; }
            if (!HasFiles(cur)) { why = "存档位置里没有能用的存档文件"; return null; }
            return [("save", cur)];
        }

        // ---- 起局读入 ----
        public static void ReadIn(Switch device, ulong programId)
        {
            string folder = Folder();
            if (folder == null) return;
            _programId = programId; _keep = false;
            try
            {
                Directory.CreateDirectory(folder);   // 玩家第一次就能在访达 / 「文件」App 里看到这个文件夹
                string now = Digest(folder), state = Path.Combine(DataRoot(), StateFile);
                _atLaunch = now;
                string last = File.Exists(state) ? File.ReadAllText(state).Trim() : null;
                if (now == "" || now == last) return;   // 空的（还没写回过）/ 和上次一样：沿用当前存档
                var top = new HashSet<string>(OwnTop(device, programId));
                var src = Detect(folder, programId, top, out string why);
                if (src == null)
                {
                    _keep = true;
                    File.WriteAllText(Path.Combine(DataRoot(), ProblemFile), why);
                    Red.Report("存档位置：认不出放进来的存档，沿用当前存档", $"{why}（{folder}）");
                    return;
                }
                Saves.SeedFrom(device, programId, src);
                File.WriteAllText(state, now);
                File.Delete(Path.Combine(DataRoot(), ProblemFile));
                Console.Error.WriteLine($"S2A 存档位置：读入玩家放进来的存档 {string.Join("、", src.Select(x => x.kind + " ← " + Path.GetRelativePath(folder, x.dir)))}");
            }
            catch (InvalidOperationException) { throw; }   // 拷入 / 提交失败：本次起局失败（同 S2A_SAVE_DIR）
            catch (Exception e) { Red.Report("存档位置：读入失败，沿用当前存档", e.Message); }
        }
        static IEnumerable<string> OwnTop(Switch device, ulong programId)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"s2a_savedata_top_{Environment.ProcessId}");
            try { return Saves.Snapshot(device, programId, tmp) > 0 ? Subdirs(tmp).Where(d => Path.GetFileName(d) != "user").SelectMany(TopNames).ToList() : []; }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        }

        // ---- 退出写回 ----
        public static void WriteBack(Switch device)
        {
            string folder = Folder();
            if (folder == null || _programId == 0 || device == null) return;
            if (_keep) { Console.Error.WriteLine("S2A 存档位置：起局时放进来的没认出，不写回（原样留着，原因见存档管理页）"); return; }
            if (_atLaunch != null && Digest(folder) != _atLaunch)
            {
                Console.Error.WriteLine("S2A 存档位置：这一局里被玩家动过，不写回（下次起局读它）");
                return;
            }
            string snap = folder.TrimEnd(Path.DirectorySeparatorChar) + ".snap", next = folder.TrimEnd(Path.DirectorySeparatorChar) + ".next", old = folder.TrimEnd(Path.DirectorySeparatorChar) + ".old";
            try
            {
                foreach (string d in new[] { snap, next, old }) if (Directory.Exists(d)) Directory.Delete(d, true);
                if (Saves.Snapshot(device, _programId, snap) == 0) return;   // 这款没有存档：不动
                Directory.CreateDirectory(next);
                string acc = Path.Combine(snap, "account"), dev = Path.Combine(snap, "device");
                if (Directory.Exists(acc)) Directory.Move(acc, Path.Combine(next, "save"));
                if (Directory.Exists(dev)) Directory.Move(dev, Path.Combine(next, Directory.Exists(acc) || Directory.Exists(Path.Combine(next, "save")) ? "save_device" : "save"));
                if (Directory.Exists(folder)) Directory.Move(folder, old);
                Directory.Move(next, folder);
                if (Directory.Exists(old)) Directory.Delete(old, true);
                File.WriteAllText(Path.Combine(DataRoot(), StateFile), Digest(folder));
                Console.Error.WriteLine($"S2A 存档位置：当前存档写回 {folder}");
            }
            catch (Exception e)
            {
                Red.Report("存档位置：写回失败（当前存档不受影响）", e.Message);
                if (!Directory.Exists(folder) && Directory.Exists(old)) Directory.Move(old, folder);   // 换到一半失败：把原来的放回去
            }
            finally
            {
                foreach (string d in new[] { snap, next }) if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch (IOException) { }
            }
        }
    }
}
