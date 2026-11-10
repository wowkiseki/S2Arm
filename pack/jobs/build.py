"""Mac preparation/launch adapter. All engine transformations stay in S2Arm."""
from __future__ import annotations
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shlex
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import uuid
import community
import host_update
import layout
from core import Failure, atomic, clone, contained, digest, read, tree_hash
from library import input_identity, require_game, selected_packages, source_path, stage_sources, validate_input
import milestones as ms
import toolchain

LANGUAGES = {
    "ja": "Japanese", "en-US": "AmericanEnglish", "fr": "French", "de": "German",
    "it": "Italian", "es": "Spanish", "zh-CN": "Chinese", "ko": "Korean", "nl": "Dutch",
    "pt": "Portuguese", "ru": "Russian", "zh-TW": "Taiwanese", "en-GB": "BritishEnglish",
    "fr-CA": "CanadianFrench", "es-419": "LatinAmericanSpanish", "zh-Hans": "SimplifiedChinese",
    "zh-Hant": "TraditionalChinese", "pt-BR": "BrazilianPortuguese",
}


def snapshot(c, root):
    """产物清单：每个文件的大小（v0.2.3 清理：原先逐个 sha256，几十 GB 的游戏资源要读一遍；校验一律只核大小，摘要没人用了。
    第二项留空串，旧清单的摘要照旧读得进来、不再比对）。软链由 clone_tree 拷的时候就拒了，这里不再重复"""
    result = {}
    for p in sorted(root.rglob("*")):
        if p.is_file() and not layout.os_junk(p.name):   # 访达 .DS_Store / ._ 等系统文件不进清单：出包期间会被写入 / 改动 / 删掉（#382）
            c.check()
            result[str(p.relative_to(root))] = [p.stat().st_size, ""]
    return result


ENGINE_SOURCES = ["runtime", "ios", "graphics", "translator", "services", "compat", "pack", "Makefile", "platform/build.mk", "macos",
                  "launchers/macos/build.mk", "tools/toolchain.env", "tools/toolchain.sh", "tools/addr_need.py"]


def source_fingerprint(c):
    """产物根的键里「引擎源码」这一项：发版应用的引擎副本是冻结的，engine.json 的 id（键里已有 engineBundleId）就代表它，不再整份哈希源码树
    （v0.2.3 清理：每次出包两遍 rglob + sha256 整个引擎源码）；源码树开发照旧哈希"""
    return None if c.r.get("engineBundle") else tree_hash(c.engine, ENGINE_SOURCES)


def validate_files(c, root, files, full=False):
    if not isinstance(files, dict) or not files:
        raise Failure("运行产物校验清单缺失。", code="runtime_invalid")
    # 判据同逐个 contained()：解析后在 root 里、本身是普通文件（不是软链）、大小对。所在目录只解析一次（11-07：运行包 6 万多个文件，
    #   逐个 realpath 要一百多万次 lstat，catalog 一次 9–15 秒）；文件本身 lstat 一次，不跟软链。
    root = Path(root).resolve()
    dirs = {}
    for name, expected in files.items():
        c.check()
        if layout.os_junk(Path(name).name):   # 旧清单里记进去的系统文件（#382 之前 snapshot 没过滤）：不核，访达随时会改它
            continue
        p = root / name
        if p.parent not in dirs:
            dirs[p.parent] = contained(root, p.parent) if p.parent != root else root
        p = dirs[p.parent] / p.name
        try:
            st = os.lstat(p)
        except OSError:
            st = None
        if st is None or not stat.S_ISREG(st.st_mode) or st.st_size != expected[0]:
            raise Failure("准备产物缺失或大小不符，请重新准备：" + name, code="runtime_invalid")
        if full and expected[1] and digest(p) != expected[1]:   # full 只在调用方明确要求时核摘要（v0.2.3 清理：缺省只核大小，#304 被改的文件就是靠大小查出来的）
            raise Failure("准备产物校验失败，请重新准备：" + name, code="runtime_invalid")


QUICK_FILES = ("main", "main.npdm")   # 游戏库列表快判也核的几个文件（主程序与程序描述）：在、是普通文件、大小同清单


def validate_runtime(c, runtime, full=False, files=True):
    """files=False：只看清单和入口（游戏库列表用），不逐文件读状态，只核主程序 / main.npdm 在且大小对（QUICK_FILES，审计 P2-212：
    原先清单没变、主程序被删也报就绪，点开始才报错）；真正启动时还会完整校验"""
    root = runtime.resolve()
    layout.require_in_location(c, root)
    manifest = read(root / "manifest.json", {})
    if manifest.get("schema") != 1 or manifest.get("engine") != "S2Arm" or manifest.get("target") != "macos-arm64":
        raise Failure("当前游戏没有完整的 S2Arm Mac 运行版本。", code="runtime_invalid")
    if not (root / "romfs").is_dir() or (root / "romfs").is_symlink():
        raise Failure("运行包缺少独立的 RomFS 游戏资源目录，请重新准备。", code="runtime_invalid")
    if files:
        validate_files(c, root, manifest.get("files"), full)
    elif isinstance(manifest.get("files"), dict):
        quick = {k: manifest["files"][k] for k in QUICK_FILES if k in manifest["files"]}
        if quick:
            validate_files(c, root, quick, full=False)
    if manifest.get("memoryRequirementsVersion") == 2:
        table = root / "addr_need.tsv"
        if not table.is_file() or manifest.get("memoryRequirementsSha256") != digest(table):
            raise Failure("这款游戏的内存配置已损坏，请重新打包后再启动。", code="runtime_invalid")
    return manifest


def require_automatic_memory(c):
    if "vammLayout" in c.r:
        raise Failure("启动器版本过旧，请更新后重新打包这款游戏。", code="runtime_invalid")


def runtime_env(c, game, runtime):
    require_automatic_memory(c)
    # 游戏语言跟本机系统首选语言（维护者 11-07：不给玩家选）：请求不带 gameLanguage 就不设 S2A_SYSTEM_LANGUAGE，
    # 宿主 services/S2A.Services/Host.cs PickLanguage 读系统首选语言（游戏不支持的退简中 → 繁中 → 英语）。带了（开发 / 旧请求）照旧映射
    language = c.r.get("gameLanguage")
    if language is not None and language not in LANGUAGES:
        raise Failure("游戏语言代码无效。")
    env = c.env()
    env["S2A_IPHONE_VA"] = "0"   # 玩家起局不用 iPhone 地址模拟；开发 / 普查保留原生缺省（#1530）。
    env.pop("S2A_VAMM_LAYOUT", None)
    env.pop("S2A_PATCH_DIR", None)   # 补丁只在出包时读（随包 patch.tsv 在游戏数据目录里，宿主自己找）；玩家导入的在 <S2A_DOCS>/S2A/patches
    save = contained(layout.root_of(c, game["id"]), layout.path(c, game["id"], "saves"))
    # 存档位置（11-09 维护者）：这款游戏文件夹里的 SaveData/，原生存档格式，放进去下次起局就读、退出时写回（services/S2A.Services/SaveFolder.cs）。
    #   不叫 Saves：同一文件夹里已有运行层内部的 saves/，APFS / NTFS 不分大小写会撞名；ASCII 名避开非 ASCII 路径的坑
    save_folder = contained(layout.root_of(c, game["id"]), layout.path(c, game["id"], "SaveData"))
    cache = contained(layout.root_of(c, game["id"]), layout.path(c, game["id"], "shader-cache"))
    docs = contained(c.library, c.library / "temp" / game["id"])
    for p in (save, cache, docs):
        p.mkdir(parents=True, exist_ok=True)
    # S2A_SAVE_DIR is a test/import seed that clears saves at boot; launch() sets it
    # only for a save the player just imported, never for normal play. Persist the Ryujinx data root instead.
    env.update(S2A_GAME_DATA=str(runtime), S2A_RYU_DATA=str(save), S2A_DOCS=str(docs), S2A_SAVE_FOLDER=str(save_folder),
               NXGAL_SHADER_CACHE_DIR=str(cache / "shaders"), NXGAL_PSO_DEV_DIR=str(cache / "pso"),
               S2A_MACRO_DUMP=str(cache / "macros"),
               S2A_DOCKED="0" if c.r.get("docked") is False else "1",   # Mac 缺省主机模式（维护者 11-07）；只有请求明说 false 才掌机
               S2A_AUTOPLAY="0", S2A_GAME_NAME=game["name"], S2A_GAME_ID=game["id"],
               S2A_INPUT_GUIDE_PREFS=str(c.private / "input-guides" / (game["id"] + ".json")))
    if language is not None:
        env["S2A_SYSTEM_LANGUAGE"] = LANGUAGES[language]
    if c.r.get("devHud"):
        env["S2A_HUD"] = "1"   # S2Arm 诊断浮层：front_mac.m 绘制，hud.c 格式化
        if c.r.get("buildId"):
            env["S2A_BUILD_ID"] = c.r["buildId"]   # 浮层「包」一行：启动器 Info.plist 的 S2ABuildID
    if c.r.get("metalHud"):
        env.setdefault("MTL_HUD_ENABLED", "1")
    # Freeze exactly what the pre-launch card showed. Never rewrite another run's snapshot.
    config = c.r.get("launchInputConfig")
    if config is None:
        config = read(layout.path(c, game["id"], "input.json", create=False)) or read(c.private / "default-input.json")
    if config is not None:
        validate_input(config)
        path = c.private / "runtime-input" / game["id"] / (c.task + ".json")
        atomic(path, config)
        env["S2A_INPUT_CONFIG"] = str(path)
    if c.r.get("inputGuideHandled") is True:
        env["S2A_INPUT_GUIDE_SHOWN"] = "1"
    if c.r.get("uiLanguage") in ("zh-Hans", "en"):
        env["S2A_UI_LANGUAGE"] = c.r["uiLanguage"]
    return env


def launch(c):
    require_automatic_memory(c)
    with c.lock("execution"):
        game = c.game()
        from saves import recover
        recover(c, game, override=c.r.get("overrideRecovery") is True)   # 回退失败：玩家单独确认「仍要启动」才放行（同 iPhone，审计 18:00 P0-3）
        runtime = layout.image_mac(c, game["id"], create=False)
        c.begin("验证运行文件")
        manifest = validate_runtime(c, runtime)
        if manifest.get("gameId") != game["id"]:
            raise Failure("运行资源与所选游戏不匹配。", code="runtime_invalid")
        if manifest.get("memoryRequirementsVersion") != 2:
            raise Failure("这款游戏需要重新打包，完成后即可启动。", code="runtime_invalid")
        host, trial = host_update.choose(c, game["id"], runtime, manifest)   # 公共宿主：兼容就换上本版的（这局是试玩），否则用原来的
        executable = host_update.host_exe(c, host)
        if not os.access(executable, os.X_OK):
            raise Failure("S2Arm Mac 宿主不可执行。", code="permission_denied")
        if c.r.get("launchInputConfig") is not None:
            supported, _ = toolchain.input_interface(c, executable)
            if not supported:
                raise Failure("此运行包的宿主未确认支持映射，请重新打包游戏后再使用键位引导。", code="runtime_invalid")
        env = runtime_env(c, game, runtime)
        from saves import claim_seed, finish_seed
        seed = claim_seed(c, game["id"])
        if seed:
            env["S2A_SAVE_DIR"] = str(seed)   # 玩家导入过存档、这是第一次启动：运行时建好存档再灌入
        env.update(community.launch_env(c, game["id"]))   # 宽屏 / 60 帧宿主开关（社区资源面板，10-27）：关掉的才设，下次启动生效
        # 宿主菜单栏「切换游戏」（macos/front_mac.m）：收尾前在这里留标记，结果带 returnToLibrary，启动器回到游戏库。先删上一局残留
        marker = c.private / "runtime-return" / (c.task + ".flag")
        marker.parent.mkdir(parents=True, exist_ok=True)
        marker.unlink(missing_ok=True)
        env["S2A_RETURN_MARKER"] = str(marker)
        back = False
        try:
            c.begin("游戏运行中")
            log = c.logs / (c.task + "-game.log")
            started = time.monotonic()
            try:
                c.run([executable, runtime], log, env, cwd=c.library / "temp" / game["id"])
            except Failure:
                if trial:   # 新宿主试玩：TRIAL_OK_SECONDS 内异常退出 = 不认这款，下次退回原来的；玩过这么久才崩不怪宿主
                    host_update.settle(c, game["id"], host, ok=time.monotonic() - started >= host_update.TRIAL_OK_SECONDS)
                raise
            if trial:
                host_update.settle(c, game["id"], host, ok=True)
            back = marker.exists()
        finally:
            marker.unlink(missing_ok=True)
            if seed:
                finish_seed(c, game["id"], log)
            # Context.run waits for the host, including failure/cancellation.
            # Do not accumulate per-run snapshots after the process exits.
            if env.get("S2A_INPUT_CONFIG"):
                path = contained(c.private / "runtime-input", Path(env["S2A_INPUT_CONFIG"]))
                try:
                    path.unlink(missing_ok=True)
                except OSError:
                    c.emit("message", "本局映射快照清理失败，请检查应用数据目录权限。")
        return {"gameId": game["id"], "returnToLibrary": True} if back else {"gameId": game["id"]}


VAGUARD = "libs2a_vaguard.dylib"


def host_cache_ok(app, stamp):
    """缓存的 Mac 宿主能不能复用：可执行文件摘要对、地址窗口护栏库在且摘要对、两者签名都验得过（10-24 #100 作者：删掉副本里的护栏库仍判命中，
    返回缺库的 App，macOS 27 上起局即占不到窗口）。旧格式的 ready.json 没记护栏库摘要 → 不复用、重新构建"""
    executable = app / "Contents/MacOS/s2arm-host"
    guard = executable.parent / VAGUARD
    if not executable.is_file() or stamp.get("sha256") != digest(executable):
        return False
    if not guard.is_file() or not stamp.get("guard_sha256") or stamp.get("guard_sha256") != digest(guard):
        return False
    for target in (guard, app):
        if subprocess.run(["codesign", "--verify", "--strict", str(target)], capture_output=True).returncode != 0:
            return False
    return True


def compile_host(c, fingerprint, env, log_root):
    from embedded import artifact
    bundled = artifact(c, "S2ArmGame.app")
    if bundled:
        if read(c.engine / "engine.json", {}).get("memoryRequirementsVersion") != 2:
            raise Failure("请更新 S2Arm，再重新打包这款游戏。", code="runtime_invalid")
        c.emit("cache", "已校验内置 S2Arm Mac 宿主", cacheHit=True)
        ms.reuse(c, "host")
        return bundled
    host_root = c.private / "tools/mac-host" / fingerprint
    app = host_root / "S2ArmGame.app"
    executable = app / "Contents/MacOS/s2arm-host"
    stamp = read(host_root / "ready.json", {})
    if host_cache_ok(app, stamp):
        c.emit("cache", "已校验并复用 S2Arm Mac 宿主（含地址窗口护栏库）", cacheHit=True)
        ms.reuse(c, "host")
        return app
    c.run(["make", "-C", c.engine, "host", "S2A_CS=1", "S2A_MAC_FRONTEND=1",
           "BUILD=" + str(host_root / "build"), "DEVELOPER_DIR=" + env["DEVELOPER_DIR"]],
          log_root / "host.log", env)
    binary = host_root / "build/s2arm-host"
    if not binary.is_file():
        raise Failure("S2Arm Mac 宿主未生成。")
    executable.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(binary, executable)
    # 地址窗口护栏（宿主弱链接 @executable_path/libs2a_vaguard.dylib，macOS 27 上没有它窗口会被 malloc 占走、起不了局；issue #100 D01）
    guard = host_root / "build" / VAGUARD
    if not guard.is_file():
        raise Failure("S2Arm Mac 宿主缺地址窗口护栏库 libs2a_vaguard.dylib。")
    shutil.copy2(guard, executable.parent / guard.name)
    c.run(["codesign", "--force", "--sign", "-", executable.parent / guard.name], log_root / "sign.log", env)
    plist = dict(CFBundleIdentifier="org.s2arm.macos.game", CFBundleName="S2Arm Game",
                 CFBundleExecutable="s2arm-host", CFBundlePackageType="APPL", LSMinimumSystemVersion="15.0",
                 NSHighResolutionCapable=True, LSSupportsGameMode=True,
                 LSApplicationCategoryType="public.app-category.games")   # 游戏模式要系统靠类别认出是游戏（11-01 调研：Mac 全屏才进游戏模式）
    (app / "Contents/Info.plist").write_bytes(plistlib.dumps(plist))
    c.run(["codesign", "--force", "--sign", "-", app], log_root / "sign.log", env)
    atomic(host_root / "ready.json", dict(schema=1, sha256=digest(executable), guard_sha256=digest(executable.parent / VAGUARD)))
    return app


def romfs_list_path():
    # romfs_list.py 两个落点：出包布局里随 Jobs 一起带（build_app.py 拷进 Resources/Jobs，同 redact.py 的规矩，
    # 出包后 Jobs 的 parents[2] 是 Contents、没有 gfx，11-07 首跑即 input_missing）；源码树里在仓库根 graphics/nvn/tools。
    here = Path(__file__).resolve()
    for candidate in (here.parent / "romfs_list.py", here.parents[2] / "graphics/nvn/tools/romfs_list.py"):
        if candidate.is_file():
            return candidate
    raise Failure("RomFS 清单实现缺失：romfs_list.py 不在 Jobs 目录，也不在仓库 graphics/nvn/tools。")


def clone_tree(c, source, destination, *, materialize_romfs=False):
    """→ True = 每个文件都是真克隆（和源同属一个克隆族，不另占盘）；有一个退成普通复制就 False"""
    # 统一盘点也用于普通目录克隆；不修改模块搜索路径，避免和作业模块重名。
    spec = importlib.util.spec_from_file_location("_s2arm_clone_romfs_list", romfs_list_path())
    romfs_list = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(romfs_list)
    tree = romfs_list.scan(source)
    error = next(iter(tree.errors.values()), None)
    if error is None:
        error = next((e.lstat_error for e in tree.entries if e.lstat_error is not None), None)
    if error is not None:
        raise Failure("准备产物目录读取失败：" + str(error)) from error
    entries = sorted(Path(e.path) for e in tree.entries)
    del tree
    romfs = None
    for p in entries:
        if not p.is_symlink():
            continue
        if materialize_romfs and p == source / "romfs":
            # 仅展开 roots.sh 定义的本款共享存储链接；发布包不依赖构建缓存。
            game = source.name.split("@", 1)[0]
            link = os.readlink(p)
            prefix = f"../.romfs_store/{game}/"
            identity = link.removeprefix(prefix)
            if not link.startswith(prefix) or not identity or identity.startswith(".") or "/" in identity:
                raise Failure("RomFS 链接不属于本款共享存储。")
            expected = source.parent.resolve() / ".romfs_store" / game / identity
            if not expected.is_dir() or expected.resolve() != expected.absolute():
                raise Failure("RomFS 共享存储缺失或包含符号链接。")
            romfs = expected
        else:
            raise Failure("输入目录包含符号链接。")
    if materialize_romfs and not (source / "romfs").is_dir():
        raise Failure("准备产物缺少 RomFS 游戏资源目录。")
    # 空目录也要建：内置引擎的 Git 快照 gc 后 .git/refs/heads 等是空目录，少了它 Git 判仓库无效、往上找到外层仓库（10-23 走查：认到了用户主目录的仓库）
    for d in (p for p in entries if p.is_dir() and not p.is_symlink()):
        (destination / d.relative_to(source)).mkdir(parents=True, exist_ok=True)
    files = [p for p in entries if p.is_file()]
    total = sum(p.stat().st_size for p in files)
    done, cloned = 0, True
    for p in files:
        c.check()
        if p.is_symlink():
            raise Failure("输入目录包含符号链接。")
        dst = destination / p.relative_to(source)
        cloned = clone(p, dst, lambda n, t: c.progress(done + n, total)) and cloned
        shutil.copymode(p, dst)
        done += p.stat().st_size
    if romfs is not None:
        (destination / "romfs").mkdir(parents=True, exist_ok=True)
        cloned = clone_tree(c, romfs, destination / "romfs") and cloned
    return cloned




def shared_dir(c, game):
    """一款游戏各平台共用的准备目录 games/<英文名>/build/：解包（extract/ + extract-checkpoint.json）只此一份，Mac / iPhone 都从这里取（11-02 维护者：多平台导出平台无关阶段只做一次）"""
    return contained(layout.root_of(c, game["id"]), layout.build_dir(c, game["id"]))


def extract_valid(c, game):
    """共用解包目录能不能直接复用：快照里的所选包身份与现在相同、文件逐个对得上（只看大小与类型，不读内容）"""
    base = shared_dir(c, game)
    # 快照连同所选包的身份一起记（原先解包目录在按 inputIdentity 分的 <键>/ 下；挪到共用目录后换了所选包 / DLC 不能还认旧解包）
    checkpoint = read(base / "extract-checkpoint.json", {})
    try:
        if checkpoint.get("inputIdentity") != input_identity(game):
            raise Failure("所选包变了")
        validate_files(c, base / "extract", checkpoint.get("files"))
        return True
    except (Failure, OSError, ValueError, AttributeError, KeyError):
        return False


def reader_program(c, where):
    """读取器（nx_unpack）的可直接执行的程序，给要自己调它的脚本（ns2_dump 解 ZBIC 压缩的 NSO：S2A_NX_UNPACK）：
    Windows 发版预编件 dll 旁边就有 nx_unpack.exe；Mac 是 dll，包一层 dotnet 壳（同 prepare / ipa.py）"""
    dll = Path(toolchain.reader(c))
    if os.name == "nt":
        return dll.with_suffix(".exe")
    shim = where / "nx_unpack"
    shim.write_text("#!/bin/sh\nexec dotnet " + shlex.quote(str(dll)) + ' "$@"\n'); shim.chmod(0o755)
    return shim


def extract_nup(c, game, packages, extraction, env, logs):
    """Switch 2 NUP 容器（导入时 library.nup_inspect 记 format=nup）→ 共用解包目录：pack/ns2_nup.py --to-extract（展开 + pack/ns2_dump.py 整理，
    与一键出包 pack/oneclick/oneclick.sh / pack/orch/oneclick_win.py 同两步、同目录布局，共享解包阶段 pack/stages/unpack.sh 也走它）。
    产物与 nx_unpack 解包同形（exefs/ romfs/ ctl/ aoc/ unpack.tsv），往后的 NPDM / NACP / 程序位数 / 追加内容检查与快照同 Switch 1 原件。不要 prod.keys。
    一个 NUP 自带本体 + 更新 + DLC，整个容器一起展开：同一款放过几个 NUP（如新旧两版）时用选中的最新更新所在的那个（没有更新用本体所在的），
    别的 NUP 里选中的 DLC 这次不带，发黄字 aoc_missing（同仅授权 DLC 的口径，不静默）"""
    nups = [p for p in packages if p.get("format") == "nup"]
    main = next((p for p in nups if p["type"] == "update"), None) or next(p for p in nups if p["type"] == "base")
    source = source_path(c, main)
    left = [p["titleId"] for p in packages if p["type"] == "dlc" and source_path(c, p) != source]
    if left:
        c.diagnostic("追加内容 %d 份在另一个 NUP 里，包里不含（%s）：同一款只留一个包含全部内容的 NUP" % (len(left), "、".join(left[:5])),
                     code="aoc_missing", severity="warning")
    # --name：没有控制分区时 ns2_dump 拿它当标题，用游戏库里的名字（导入时同样取自文件名）；S2A_NX_UNPACK：ns2_dump 解 ZBIC 压缩的 NSO 用
    c.run([sys.executable, "-X", "utf8", c.engine / "pack/ns2_nup.py", "--to-extract", source, extraction, "--name", game["name"]],
          logs / "ns2_nup.log", dict(env, S2A_NX_UNPACK=str(reader_program(c, logs))))


EXTRACTED_FORMATS = ("dump", "extract", "loose")


def extract_folder(c, game, packages, extraction, env, logs):
    """已解包的游戏文件夹（导入时 library.extract_inspect 记 format）→ 共用解包目录，不要 prod.keys：
    extract 本来就是解包产物，克隆过来；dump（Switch 2 导出）交 pack/ns2_dump.py 整理（同 NUP）；loose（exefs + romfs 平铺）软链成 base/ 再走同一条路。"""
    main = next(p for p in packages if p.get("format") in EXTRACTED_FORMATS)
    source, fmt = source_path(c, main), main["format"]
    if fmt == "extract":
        c.run([sys.executable, c.engine / "tools/clonedir.py", source, extraction], logs / "extract.log", env)
        return
    with tempfile.TemporaryDirectory(prefix="loose-", dir=c.private) as stage:
        if fmt == "loose":
            os.symlink(source, Path(stage) / "base")
        c.run([sys.executable, "-X", "utf8", c.engine / "pack/ns2_dump.py", stage if fmt == "loose" else source, extraction, "--name", game["name"]],
              logs / "ns2_dump.log", dict(env, S2A_NX_UNPACK=str(reader_program(c, logs))))


def ensure_extract(c, game, packages, env, logs, valid=None):
    """共用解包目录：校验快照对得上就复用（打复用行），否则按所选包重解、核程序位数与 DLC、写快照。→ 解包目录。
    valid：调用方刚判过的 extract_valid（Mac 打包发计划前判，免得算两遍）"""
    base = shared_dir(c, game)
    base.mkdir(parents=True, exist_ok=True)
    extraction = base / "extract"
    identity = input_identity(game)
    if valid is None:
        valid = extract_valid(c, game)
    ms.reuse(c, "unpack", valid)   # 打 iPhone 包在这一步开始后才判：当场改标、重发计划
    ms.enter(c, "unpack")
    if not valid:
        if extraction.exists():
            from library import _rmtree
            _rmtree(extraction)   # 走统一删除：只读 / Windows 封存 / 删的同时有程序往里写系统文件（#369 外置盘 Errno 66）都在这一处处理
        paths = list(dict.fromkeys(source_path(c, p) for p in packages))
        if any(p.get("format") == "nup" for p in packages):
            extract_nup(c, game, packages, extraction, env, logs)
        elif any(p.get("format") in EXTRACTED_FORMATS for p in packages):
            extract_folder(c, game, packages, extraction, env, logs)
        else:
            c.run(["dotnet", toolchain.reader(c), c.keys, extraction, "--aoc-index", "--title-id=" + game["id"], *paths], logs / "extract.log", env)
        if not (extraction / "exefs/main.npdm").is_file() or not (extraction / "ctl/control.nacp").is_file():
            raise Failure("解包结果缺少 NPDM 或 NACP。")
        # 程序位数只走 nx_unpack --npdm-isa（全仓唯一判法，pack/nx_unpack/Inspect.cs NpdmIsa）：读不了 / unknown 退 3，同样拒
        isa_log = logs / "npdm_isa.log"
        c.run(["dotnet", toolchain.reader(c), "--npdm-isa", extraction / "exefs/main.npdm"], isa_log, env, ok_codes=(0, 3))
        isa = (isa_log.read_text(encoding="utf-8", errors="replace").strip().splitlines() or [""])[-1].strip()
        if c.last_rc != 0 or isa != "aarch64":
            raise Failure("合并后的游戏程序不是有效的 AArch64 NPDM（nx_unpack --npdm-isa：" + (isa or "无输出") + "）。")
        # 所选 DLC 有一行文件数 -1（nx_unpack：数据不在给的文件里 = 只有授权，或数据分区解不开）：本体照常出、不挡，
        #   但不能静默变成仅授权——同 oneclick / pack/stages/aoc.sh 与出包完成的口径发黄字 aoc_missing，完成页画「完成（有警告）」
        #   （审计 P1-89：原先整单失败、本体也不出，如暗黑2 卡带里 100 KB 的密钥代号 DLC；命令行 oneclick 不拦本体，两边口径不一）
        rows = (extraction / "aoc.tsv").read_text().splitlines() if (extraction / "aoc.tsv").is_file() else []
        lic = [row.split("\t")[1] for row in rows[1:] if row.strip() and int(row.split("\t")[3]) < 0]
        if lic:
            c.diagnostic("追加内容 %d 份只有授权或数据解不开，包里不含（%s）" % (len(lic), "、".join(lic[:5]) + (" 等" if len(lic) > 5 else "")), code="aoc_missing", severity="warning")
        atomic(base / "extract-checkpoint.json", {"inputIdentity": identity, "files": snapshot(c, extraction)})
    else:
        c.emit("cache", "已验证并复用解包产物", cacheHit=True)
    return extraction


def oneclick_kuan(c, gid):
    """一键出包给这款起的款名（pack/oneclick/oneclick.sh 同口径）：titles.tsv 里有用登记名，没有用 t<程序号小写>"""
    t = c.engine / "pack/oneclick/titles.tsv"
    for line in (t.read_text(encoding="utf-8").splitlines() if t.is_file() else []):
        f = line.split("\t")
        if len(f) >= 2 and not line.startswith("#") and f[0].upper() == gid.upper():
            return f[1].strip()
    return "t" + gid.lower()


def sibling_roots(c, game):
    """同一款在别的平台已有的现行产物根（新的在前）：iPhone games/<英文名>/build/ios/roots/*@现行、Mac games/<英文名>/build/<键>/roots/<程序号>@现行"""
    found = []
    for link in [*layout.ios_work(c, game["id"], create=False).glob("roots/*@现行"), *(p for p in layout.build_dir(c, game["id"], create=False).glob("*/roots/*@现行") if p.parent.parent.name != "ios")]:
        try:
            cur = contained(c.library, link.resolve())
        except (Failure, OSError):
            continue
        if cur.is_dir():
            found.append((cur.stat().st_mtime, cur))
    return [p for _, p in sorted(found, reverse=True)]


def seed_from_sibling(c, game, roots, kuan):
    """本平台还没有现行根、别的平台有：克隆它的现行根起步（不从解包目录重新播种）。平台无关阶段的键按内容算，与本树一致就跳过、不一致照常重做
    （兄弟根是旧提交出的、键不同 → 重做）。克隆走 tools/clonedir.py：同一 APFS 卷 clonefile，跨卷 / 卷不支持克隆（exFAT 等）退回普通拷贝，--report 打出走了哪条路。
    → 用了兄弟根 True；没有可用的兄弟根 False（调用方照常从解包目录播种）"""
    mine = roots.resolve() if roots.exists() else roots
    for cur in sibling_roots(c, game):
        if cur.parent == mine:
            continue
        roots.mkdir(parents=True, exist_ok=True)
        dst = roots / (kuan + "@sib-" + cur.name.split("@", 1)[-1])
        if dst.exists():
            shutil.rmtree(dst)
        log = c.logs / (c.task + "-sibling.log")
        log.parent.mkdir(parents=True, exist_ok=True)
        try:
            c.run([sys.executable, c.engine / "tools/clonedir.py", "--report", cur, dst], log, c.env())
        except Failure:
            # 目标卷不支持原子不覆盖发布（exFAT 等：clonedir 按设计失败关闭）：不用兄弟根，回到从共用解包目录播种（解包不重做），黄字说清走了哪条路
            shutil.rmtree(dst, ignore_errors=True)
            why = (log.read_text(encoding="utf-8", errors="replace").strip().splitlines() or ["原因未记"])[0]
            c.diagnostic(f"兄弟平台的产物根 {cur.name} 克隆不过来（{why}），改从解包目录准备，平台无关阶段这次要重做", code="sibling_clone_failed", severity="warning")
            return False
        route = (log.read_text(encoding="utf-8", errors="replace").strip().splitlines() or [""])[-1]
        link = roots / (kuan + "@现行")
        tmp = roots / ("." + kuan + "@现行.new")
        tmp.unlink(missing_ok=True); tmp.symlink_to(dst.name); tmp.replace(link)
        src = (cur / ".root_src").read_text().strip()[:9] if (cur / ".root_src").is_file() else "未知"
        c.emit("cache", f"复用兄弟平台的产物根 {cur.name}（提交 {src}；{route}）：平台无关阶段按键核对，键相同跳过、不同照常重做", cacheHit=True)
        return True
    return False


def prepare(c):
    with c.lock("execution"):
        c.begin("检查游戏输入")
        c.require_source()
        game = c.game()
        if not __import__("re").fullmatch(r"[0-9A-F]{16}", game["id"]):
            raise Failure("无效的游戏标识。")
        require_game(c, game)
        from library import apply_location, check_pack_space
        apply_location(c, game)   # 出包页选的存放位置（游戏库结构 v3）：在别处就先搬过去
        check_pack_space(c, game)
        packages = selected_packages(game)
        size_gb = sum(p.get("size", 0) for p in {p["sha256"]: p for p in packages}.values()) / 1e9
        # 先定这次的产物根（键 = 引擎源码、工具版本、所选包、字体、引擎号），再发计划：哪几步会复用缓存要在计划里先标出来（11-07）
        fingerprint = source_fingerprint(c)
        fonts = c.fonts_dir()
        if fonts is None:
            raise Failure("应用自带的共享字体缺失，请重新安装完整应用。")
        env = c.env()
        from embedded import artifact
        if c.r.get("engineBundle"):
            artifact(c, "Prepare")
        versions = {}
        for name, command in (("sdk", ["xcrun", "--sdk", "macosx", "--show-sdk-version"]),
                              ("metal", ["xcrun", "--sdk", "macosx", "metal", "--version"]),
                              ("dotnet", ["dotnet", "--list-runtimes"] if c.r.get("engineBundle") else ["dotnet", "--version"])):
            ok, version = toolchain.probe(c, command)
            if not ok:
                code = {"sdk": "xcode_unavailable", "metal": "metal_unavailable", "dotnet": "dotnet_unavailable"}[name]
                raise Failure("构建工具不可用：" + name, code=code)
            versions[name] = version
        identity = dict(schema=1, target="macos-arm64", source=fingerprint, tools=versions,
                        inputIdentity=input_identity(game), fonts=tree_hash(fonts, ["."]),
                        engineBundleId=read(c.engine / "engine.json", {}).get("id"))
        key = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()[:24]
        root = contained(layout.root_of(c, game["id"]), layout.build_dir(c, game["id"]) / key)
        root.mkdir(parents=True, exist_ok=True)
        # 会复用缓存的步：共用解包快照对得上 → 解包；现行镜像就是从这一份产物根（同样的代码、工具、输入）出的（game.json imageKey）→ 改写与准备资源、
        #   直连库（各阶段按键跳过）。直连库若其实要重出（跑局知识等变了，DirectWatcher 看到直连临时版本），当场改回冷步
        unpack_ok = extract_valid(c, game)
        warm = layout.game_info(c, game["id"]).get("imageKey") == key and (root / "roots" / (game["id"] + "@现行")).exists()
        ms.plan(c, size_gb, "build_dual", reused=(["unpack"] if unpack_ok else []) + (["refresh", "analyze", "direct"] if warm else []))
        logs = c.logs / c.task
        logs.mkdir(parents=True, exist_ok=True)
        env.update(S2A_ROOTS=str(root / "roots"), S2A_ROOTS_SHARED=str(root / "never-shared"),
                   S2A_ROOTS_PRIV=str(root / "roots-private"), S2A_FONTS=str(fonts))
        extraction = ensure_extract(c, game, packages, env, logs, valid=unpack_ok)
        # The memory table is collected from the repository table and the recent run logs only; the previously
        # installed table is not fed back (its values would never age out once the host stops needing them)
        if not (root / "roots" / (game["id"] + "@现行")).exists() and not seed_from_sibling(c, game, root / "roots", game["id"]):
            c.run(["bash", c.engine / "pack/seed_root.sh", game["id"], "import-" + key, extraction], logs / "seed.log", env)
        # 出包同一条流水线（10-26 维护者 / 维护者「iOS 和 macOS 不要这儿新那旧的」）：和一键出包 --target mac 一样调 pack/refresh.sh——改写 ‖ 系统数据 ‖ 宽屏布局 ‖
        #   追加内容 → 直连配置推导 → RomFS 索引 → 抽取 → 直连库 → 直连出成则通用 lazy（C.3：不翻译 / 编译 / 预建管线，首遇安全网兜底）。
        #   原先这里自己逐段调、只出通用全量：侍魂晓预建 13187 条管线 38 分钟、「备用快速版本」是通用函数体动态库，直连库一个都不出。
        #   10-30 起直连单轨（维护者）：直连做不成 refresh.sh 报红「直连单轨停在 <哪一步>」退 1，任务按它报红（tool_reason 取日志里的红行），不退成通用；
        #   11-07 兼容模式（只出通用）整条拆掉。tests/pack_entry_test.sh 防回退
        ms.enter(c, "refresh")
        env.update(S2A_PACK_PLAT="macos", S2A_WIDEN_AUTO_RUN="0", S2A_WIDEN_PKG="0")   # 宽屏自动出表按 iPhone 屏幕比例补画面、要占 Mac 局槽跑闭环；Mac 窗口按 16:9（同 --target mac）
        # refresh.sh 链上的追加内容 / 原件核对要原始包、密钥和 nx_unpack（同一键出包：pack/oneclick/oneclick.sh 设 S2A_GAME_SRC / S2A_KEYS）：
        #   原始包 = 这款选中的原件软链进临时目录（stage_sources；不设就去扫 ~/Downloads：玩家机上弹「访问下载文件夹」授权框，10-26 侍魂晓日志实测扫了）；
        #   nx_unpack = 应用自带的读取工具（toolchain.reader，dotnet 跑 dll）包一层可执行壳——不给就按源码现编，启动器只带运行时、没有 SDK，编不出（10-26 侍魂晓停在 aoc）
        src = root / "src"; shutil.rmtree(src, ignore_errors=True)
        stage_sources(c, packages, src)
        nxu = root / "nx_unpack"
        nxu.write_text("#!/bin/sh\nexec dotnet " + shlex.quote(str(toolchain.reader(c))) + ' "$@"\n'); nxu.chmod(0o755)
        env.update(S2A_GAME_SRC=str(src), S2A_KEYS=str(c.keys), S2A_NX_UNPACK=str(nxu))
        try:
            with ms.DirectWatcher(c, root / "roots", game["id"]):
                c.run(["bash", c.engine / "pack/refresh.sh", game["id"]], logs / "refresh.log", env)
        finally:
            shutil.rmtree(src, ignore_errors=True)
        current = (root / "roots" / (game["id"] + "@现行")).resolve()
        contained(root, current)
        shader_report = read(current / "shaders/shader_pack.json", {})
        reds = shader_report.get("red", {})
        if (current / "shaders/red.txt").exists() or any(reds.values()):
            raise Failure("着色器准备存在缺口，请查看构建日志；未替换上次成功版本。")
        if not (current / "shaders/direct/keys.tsv").is_file():   # 只有直连：refresh.sh 退 0 却没有直连库就是出包出错（11-07 拆通用模式）
            raise Failure("直连着色器库不完整。")
        ms.enter(c, "host")
        host = compile_host(c, fingerprint, env, logs)
        ms.enter(c, "install")
        # 镜像只留一份（游戏库结构双端统一）：新镜像先出到 image/mac.partial，校验过再换进来；出成功前旧镜像不动。宿主进 hosts/mac 多款共用
        image = contained(layout.root_of(c, game["id"]), layout.image_mac(c, game["id"]))
        temporary = image.with_name("mac.partial")
        if temporary.exists():
            shutil.rmtree(temporary)
        temporary.parent.mkdir(parents=True, exist_ok=True)
        temporary.mkdir()
        try:
            all_cloned = clone_tree(c, current, temporary, materialize_romfs=True)
            # Transient stage logs and source paths are not runtime dependencies.
            for p in temporary.glob("*.log"):
                p.unlink()
            for name in ("seed.txt", ".root_src", ".root_base"):
                (temporary / name).unlink(missing_ok=True)
            if not c.r.get("engineBundle") and fingerprint != source_fingerprint(c):   # 只有源码树开发会在出包中途改源码；发版引擎冻结
                raise Failure("构建期间 S2Arm 源码发生变化，请重试；旧运行版本已保留。")
            manifest = dict(identity, memoryRequirementsVersion=2,
                            memoryRequirementsSha256=digest(current / "addr_need.tsv"),
                            engine="S2Arm", gameId=game["id"], gameName=game["name"], compatibility="unverified",
                            files=snapshot(c, temporary), **(host_update.engine_compat(c) or {}))   # 只换宿主的判据（host_update.py）
            atomic(temporary / "manifest.json", manifest)   # v0.2.3 清理：不再紧接着全量复验——清单就是同一进程刚从这些文件量出来的
            sha = host_update.install_host(c, host)
            c.check()
            old = image.with_name("mac.old")
            if old.exists():
                shutil.rmtree(old)
            if image.exists():
                os.replace(image, old)
            os.replace(temporary, image)
            layout.update_info(c, game["id"], hostMac=sha, hostFailed=[], imageKey=key)
            # 出包实测账（library.record_pack_usage）：在清旧镜像之前量，新旧同在盘上才是这一单真占到的峰值
            from library import record_pack_usage
            record_pack_usage(c, game)
            # 新运行版本已换上：以下只是省盘 / 记账 / 刷新显示，出错只提示、不把这次出包判失败（v0.2.3 清理，#343）
            if old.exists():
                host_update.tidy(c, "删除上一版运行文件", shutil.rmtree, old)
            host_update.tidy(c, "清理不再用的旧运行层", host_update.prune, c)
        finally:
            if temporary.exists():
                shutil.rmtree(temporary)
        host_update.tidy(c, "记录出包里程碑用时", ms.record, c, size_gb)
        from library import _part_bytes, _rm
        # 解包和中间目录：运行版本整棵是克隆出来的（APFS 同卷，和产物根、解包同属一个克隆族）就保留——几乎不另占盘，下次出另一端的包不用重解（#329，
        #   同一款的数据在盘上只占一份）；有文件退成了普通复制（跨卷、非 APFS、Windows）运行版本已是独立的一份，照旧删掉解包省一份
        unpacked = layout.build_dir(c, game["id"], create=False)
        if unpacked.exists() and not all_cloned:
            host_update.tidy(c, "删除解包目录", _rm, c, layout.root_of(c, game["id"]), unpacked)
        import sizes
        host_update.tidy(c, "刷新游戏占用大小", sizes.refresh, c, game["id"])   # 每款各部分大小写回 game.json（游戏库只读不量）
        return dict(gameId=game["id"], outputPath=str(image), outputBytes=_part_bytes([image]), target="macos-arm64", compatibility="unverified")
