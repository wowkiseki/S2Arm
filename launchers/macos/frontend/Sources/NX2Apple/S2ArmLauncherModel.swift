// S2Arm（不是上游文件）：S2Arm × NX2Apple 界面的状态，接 S2ArmAppleAdapter 的 S2ArmEngineProvider（设计稿 2.13 EngineProvider）。
// 只持有界面要画的状态、把动作转给适配层；作业怎么起、请求长什么样都在适配层。
import AppKit
import Foundation
import Combine
import NXModel
import S2ArmAppleAdapter

/// 一次出包（勾了几个平台就几个作业，按页面顺序排队，ExportPlatform.picked）：现在做到第几个、每个平台的结局。
struct ExportRun: Equatable {
    struct Outcome: Equatable {
        var state: JobOutcome.State
        /// 没成时的原因（作业层原句，显示经 loc.backend）。
        var message = ""
        var code: String?
        /// Mac：运行文件目录；iPhone：IPA 与资源包所在文件夹。
        var output: String?
        /// iPhone：IPA 文件。
        var ipa: String?
        /// 成品大小（字节）：iPhone = IPA，Mac = 运行版本。作业层量的，量不出为 nil。
        var appBytes: Int64?
        /// iPhone 资源包大小（字节）。
        var packBytes: Int64?
        /// 作业层给的下一步建议（玩家话术）。
        var suggestion = ""
        /// 这个平台用了多久（秒）。
        var seconds: Double = 0
        /// 作业层给的失败事实（阶段、退出码、原文，英文标签）；没有为 nil。
        var failureSummary: String?
    }
    let platforms: [ExportPlatform]
    /// 正在做的那个平台（platforms 的下标）。
    var index = 0
    var outcomes: [ExportPlatform: Outcome] = [:]
    /// 整次出包、当前平台开始的时刻。
    let started: Date
    var platformStarted: Date
    var current: ExportPlatform { platforms[min(index, platforms.count - 1)] }
    var failed: [ExportPlatform] { platforms.filter { outcomes[$0].map { $0.state != .completed } ?? false } }

    init(platforms: [ExportPlatform], now: Date = Date()) {
        self.platforms = platforms; started = now; platformStarted = now
    }
}

/// 出包没全成（有平台失败）：作业页按平台写结局，整次记失败。
struct ExportIncomplete: Error {}

/// 出包页底栏「为什么还不能出包」。
enum ExportBlocker: Equatable {
    case noPlatform, game(String), keys, checking, environment, busy
    /// 这款已在出包或排队（同一款再点一次 / 控制口重发）：原先静默当成功、什么也不做，控制口回 ok 却没建任务
    case alreadyQueued
}

/// 正在跑 / 刚结束的一个作业（一局、出包、准备运行、装到 iPhone、导入、一键处理环境、换游戏库）。同一时刻只有一个（同旧启动器「执行位」互斥）。
struct JobSession: Equatable {
    enum Kind: String { case play, export, prepare, install, installRemove, installRestore, importGames = "import", downloadMetal, downloadIosPlatform, installDotnet, setupLibrary = "setup",
                        keys, delete, diagnostics, moveGame, clearCache }
    enum Phase: Equatable { case running, stopping, finished(JobOutcome.State) }
    let kind: Kind
    let gameID: String?
    let name: String
    var phase: Phase = .running
    var stage = ""
    var message = ""
    /// 最近的作业事件（阶段 / 消息），最新在后。
    var log: [String] = []
    /// 第几步、本阶段计数、剩余时间（JobProgress，从作业事件来）。
    var progress = JobProgress()
    /// 做完可以在访达里显示的东西。
    var reveal: String?
    /// 出包：平台队列与各平台结局（别的作业为 nil）。
    var export: ExportRun?
    /// 出包前的磁盘预算（作业层 space 事件，check_pack_space）；没有为 nil。
    var space: PackSpace?
    /// 正在跑的那个作业层任务号（最近一个事件的 taskId；出包一个平台一个任务）。控制口 GET /state 的 executionTask 报它（原先恒为空，s2armctl next 判不出「本游戏正在运行」）
    var taskID: String?
}

/// 排队的一次出包。
struct QueuedExport {
    let game: GameSummary
    let platforms: [ExportPlatform]
    let target: [String: Any]?
    let spaceOverride: Bool
    /// 存放位置（游戏库结构 v3）：nil = 这款现在的位置 / 上次用的位置（后端定）
    var location: String? = nil
}

/// 这次出包要占多少、盘上还有多少（只取游戏库所在的卷）。
struct PackSpace: Equatable {
    let need: Int64?
    let free: Int64?
    let basis: String

    init?(_ value: Any?) {
        guard let v = value as? [String: Any], let volumes = v["volumes"] as? [[String: Any]],
              let volume = volumes.first(where: { $0["role"] as? String == "library" }) ?? volumes.first else { return nil }
        need = (volume["needBytes"] as? NSNumber)?.int64Value
        free = (volume["freeBytes"] as? NSNumber)?.int64Value
        basis = volume["basis"] as? String ?? ""
    }
}

@MainActor
public final class S2ArmLauncherModel: ObservableObject {
    @Published private(set) var games: [GameSummary] = []
    @Published private(set) var loading = false
    /// 游戏库读回来过（成功或失败）：之前库是空的只说明还没读完，不能显示「还没有游戏」。
    @Published private(set) var libraryLoaded = false
    @Published private(set) var libraryError: String?
    /// 库页上的列表是上次读到的快照（启动时先显示的，或这次刷新没成）：库页要标明，并在刷新失败时给重试（审计 P1-148）。
    @Published private(set) var showingSnapshot = false
    /// 刷新没成、库页仍显示上次的列表：这时的失败原因（没能刷新提示用；第一次就没读成、列表是空的走欢迎页的报错）。
    var staleLibraryError: String? { showingSnapshot && !games.isEmpty ? libraryError : nil }
    @Published var selectedID: String?
    /// 出包平台的勾选没能写盘。
    @Published private(set) var choiceError: String?
    @Published private(set) var session: JobSession?
    /// 出包页开着的那一款（nil = 游戏库）。
    @Published var exportFor: String?
    /// 存放位置（游戏库结构 v3）：出包页下拉框的各项、上次用的位置号
    @Published var locations: [StorageLocation] = []
    @Published var lastLocation = ""
    /// 出包页记住的平台勾选（平台 id）。
    @Published private(set) var exportChoice: [String] = []
    /// 动作没能开始的原因（缺东西、没选游戏库等），显示在主按钮下面。只属于出错的那一款（导入等不针对某款的为 nil）：
    /// 换选别的游戏不显示上一款的报错（11-07 界面验收：丝之歌缺原件的红字挂到了 Hades 下面）。
    @Published private(set) var actionError: String?
    private(set) var actionErrorGameID: String?
    func actionError(for gameID: String?) -> String? { actionErrorGameID == nil || actionErrorGameID == gameID ? actionError : nil }
    private func failAction(_ text: String?, game: String?) { actionErrorGameID = game; actionError = text }
    // 环境检查
    @Published private(set) var checks: [ToolCheck] = []
    @Published private(set) var checking = false
    @Published private(set) var environmentError: String?
    @Published var showEnvironment = false
    /// 添加游戏 / 放密钥弹窗（ImportSheet）：这两个作业的进度和失败原因都留在弹窗里，不换页。
    @Published var updateState: UpdateState = .idle
    var updateReady: [String: Any]?
    @Published var showAddGames = false
    @Published var showKeys = false
    /// 选好了但缺密钥（作业层 keys_missing）：等放入密钥后接着导入。解包好的游戏不要密钥，不经过这里。
    @Published private(set) var pendingImport: [URL] = []
    /// 留在原页显示的作业没成的原因（换作业、关弹窗时清掉）。
    @Published private(set) var inlineFailure: (kind: JobSession.Kind, message: String)?
    /// 欢迎页上排着队的一键下载（作业同一时刻只有一个）。
    @Published private(set) var repairQueue: [ToolCheck] = []
    /// 新手流程刚导入的那款：欢迎页留着，右下角「一键出包」（点了就回到正常的游戏库）。
    @Published private(set) var welcomeGameID: String?
    /// 这份密钥读不了选的游戏：放密钥弹窗提示换一份。
    @Published private(set) var keysRejected = false
    /// 刚装好的那款（装机弹窗显示完成页）。
    @Published var installDone: String?
    /// 最近一次手机作业（装到 iPhone / 从手机删 / 装回上一版）的结局，玩家还没回去看：顶部胶囊「已装好 · 查看结果」靠它（#308：关掉装机弹窗后找不回结果）。
    /// 新的手机作业开始、或打开这款的装机弹窗时清掉。
    @Published var phoneResult: (gameID: String, name: String, ok: Bool)?
    static let phoneKinds: Set<JobSession.Kind> = [.install, .installRemove, .installRestore]
    /// 连着的手机、可选机型（出包页用）；玩家选的机型（productType）。
    @Published private(set) var connectedDevices: [[String: Any]] = []
    @Published private(set) var targetModels: [[String: Any]] = []
    @Published private(set) var targetOS: [Int] = []
    @Published var chosenModel: String?
    /// 连着多台 iPhone / iPad 时玩家在出包页选的那台（设备号，记住到下次；#272）。按机型优化和装到设备都用它
    @Published var chosenDevice: String? = UserDefaults.standard.string(forKey: S2ArmLauncherModel.exportDeviceKey) {
        didSet { UserDefaults.standard.set(chosenDevice, forKey: Self.exportDeviceKey) }
    }
    static let exportDeviceKey = "S2AExportDevice"
    /// 排队等着出的；运行页被收起（后台继续）；本次打开以来每款最近一次的出包结果；正在查看的结果。
    @Published private(set) var exportQueue: [QueuedExport] = []
    @Published var sessionHidden = false
    @Published private(set) var finishedExports: [String: JobSession] = [:]
    @Published var viewingResult: JobSession?
    var showsWelcome: Bool { games.isEmpty || welcomeGameID != nil }
    var welcomeGame: GameSummary? { games.first { $0.id == welcomeGameID } }
    // 装到 iPhone 前的清单
    @Published private(set) var installReadiness: InstallReadiness?
    @Published private(set) var customizing = false
    @Published private(set) var customizeError: String?
    @Published private(set) var installChecking = false
    @Published private(set) var installError: String?
    private(set) var installGameID: String?
    // 释放空间（spaceSurvey / spaceFree，FreeSpaceView）：只看一款
    @Published private(set) var space: GameSpace?
    @Published private(set) var spaceLoading = false
    @Published private(set) var spaceFreeing = false
    @Published private(set) var spaceError: String?
    /// 上一次释放了多少（字节）；换一款或重新打开时清掉。
    @Published private(set) var spaceFreed: Int64?
    private var spaceGameID: String?
    // 存储总览（storageOverview，FreeSpaceView 的「总览」）
    @Published private(set) var overview: StorageOverview?
    @Published private(set) var overviewLoading = false
    @Published private(set) var overviewError: String?

    let provider: S2ArmEngineProvider
    private var running: RunningGame?
    /// iCloud 存档同步进行中（开局前 / 退出后）：不重复开局
    @Published var cloudSyncing = false
    private var work: Task<Void, Never>?
    /// 导入后排着等准备的游戏（同旧启动器：放进来就自动准备，维护者 10-23 不让玩家选）。
    private static let logLimit = 200

    public init(client: JobClient) {
        provider = S2ArmEngineProvider(client: client)
        exportChoice = provider.settings.exportTargets
    }

    var selected: GameSummary? { games.first { $0.id == selectedID } ?? games.first }
    /// 有作业在跑（一局、准备、出包、装机……）：别的动作先不让点。
    var busy: Bool { if let session, case .finished = session.phase { return false }; return session != nil }

    public func refresh() {
        guard !loading else { return }
        // 启动时先显示上次的列表，不等作业；能不能玩在真正启动时还会完整校验
        if !libraryLoaded, games.isEmpty, let known = provider.library.lastKnownGames(), !known.isEmpty {
            games = known
            selectedID = known.first?.id
            libraryLoaded = true
            showingSnapshot = true
        }
        loading = true
        provider.library.load { [weak self] result in
            guard let self else { return }
            self.loading = false
            self.libraryLoaded = true
            switch result {
            case .success(let games):
                self.games = games; self.libraryError = nil; self.showingSnapshot = false
                if self.selectedID == nil || !games.contains(where: { $0.id == self.selectedID }) { self.selectedID = games.first?.id }
            case .failure(let error):
                self.libraryError = error.message
                self.showingSnapshot = !self.games.isEmpty   // 列表没换：还是上次的
            }
        }
    }

    // ---- environment ----

    enum EnvironmentState { case unknown, ready, readyMac, missing }
    /// ready = 准备、启动、打 iPhone 包都齐；readyMac = Mac 上准备/启动齐了、但打 iPhone 包还缺（如 iOS 平台组件只挡 ipa）。
    var environmentState: EnvironmentState {
        if checks.isEmpty { return .unknown }
        guard provider.environment.ready(for: "prepare") else { return .missing }
        return checks.contains { !$0.ok && $0.required(for: "ipa") } ? .readyMac : .ready
    }

    func checkEnvironment() {
        guard !checking else { return }
        checking = true; environmentError = nil
        Task {
            do { checks = try await provider.environment.check() }
            catch { environmentError = error.localizedDescription }
            checking = false
        }
    }

    /// 一键处理：Xcode 去 App Store / 打开 Xcode / 系统设置交给系统打开；Metal 组件、iOS 平台组件、.NET SDK 交作业，做完自动重新检查。
    func repair(_ check: ToolCheck) {
        guard let action = check.action else { return }
        switch provider.environment.repairStep(for: action) {
        case .open(let url):
            NSWorkspace.shared.open(url)
        case .job(let operation):
            if busy { if !repairQueue.contains(check) { repairQueue.append(check) }; return }
            inlineFailure = nil
            showEnvironment = false
            start(JobSession.Kind(rawValue: operation) ?? .installDotnet, game: nil, name: check.name) { [weak self] sink in
                let list = try await self?.provider.environment.repair(operation, events: sink)
                if let list { self?.checks = list }
                return nil
            }
        case .chooseLibrary:
            break   // 选目录面板由环境检查页开（面板文案走界面语言），选好交 changeLibrary
        case .recheck:
            checkEnvironment()   // 组件已下载、只是没注册就绪：重查一遍，不再下一遍 8 GB
        case .unavailable:
            environmentError = check.message
        }
    }

    /// 换游戏库：setup 作业（作业层拦非 APFS、云盘同步目录、非空的生文件夹）→ 写回偏好、重读游戏库、重新检查环境。
    /// 作业跑在作业页，没成的原因（如选的还在云盘里）在作业页显示；成了作业页可在访达中显示新游戏库。
    func changeLibrary(to url: URL, move: Bool = false) {
        guard !busy else { return }
        showEnvironment = false
        failAction(nil, game: nil)
        start(.setupLibrary, game: nil, name: url.lastPathComponent) { [weak self] sink in
            guard let self else { return nil }
            let library = try await self.provider.library.change(to: url, moveFrom: move ? self.provider.client.environment.library : nil, events: sink)
            self.games = []; self.selectedID = nil; self.libraryLoaded = false
            self.checks = (try? await self.provider.environment.check()) ?? self.checks
            return library
        }
    }

    // ---- free space ----

    /// 读这款的占用（spaceSurvey）。换了一款先清掉上一款的。
    func clearSpaceFreed() { spaceFreed = nil }

    func loadOverview() {
        guard !overviewLoading else { return }
        overviewLoading = true; overviewError = nil
        Task {
            do { overview = try await provider.space.overview() }
            catch { overviewError = error.localizedDescription }
            overviewLoading = false
        }
    }

    func loadSpace(_ gameID: String) {
        if spaceGameID != gameID { spaceGameID = gameID; space = nil; spaceFreed = nil }
        spaceError = nil
        guard !spaceLoading else { return }
        spaceLoading = true
        Task {
            do {
                let list = try await provider.space.survey(gameId: gameID)
                if spaceGameID == gameID { space = list.first { $0.gameId == gameID } }
            } catch {
                if spaceGameID == gameID { spaceError = error.localizedDescription }
            }
            spaceLoading = false
        }
    }

    /// 删勾选的部件（spaceFree，直接删、不进废纸篓）。有作业在跑不让点；作业层拒绝（别的任务在用游戏库）显示它的原话。删完重读占用与游戏库。
    func freeSpace(_ gameID: String, parts: Set<String>) {
        guard !busy, !spaceFreeing, !parts.isEmpty, spaceGameID == gameID else { return }
        spaceFreeing = true; spaceError = nil; spaceFreed = nil
        // 按作业层部件顺序交
        let ordered = (space?.parts.map(\.id) ?? []).filter { parts.contains($0) }
        Task {
            do {
                let freed = try await provider.space.free(gameId: gameID, parts: ordered)
                if spaceGameID == gameID { spaceFreed = freed.freedBytes }
            } catch {
                if spaceGameID == gameID { spaceError = error.localizedDescription }
            }
            spaceFreeing = false
            loadSpace(gameID)
            refresh()   // 删了 Mac 运行版本，这款要重新准备
            if overview != nil { loadOverview() }
        }
    }

    /// 这件事的前提没齐：打开环境检查页（还没查过就查一遍），返回 false。
    private func environmentAllows(_ scopes: [String], ignoring: Set<String> = []) -> Bool {
        if checks.isEmpty, environmentError != nil { return true }   // 环境检查本身失败不挡：作业自己校验并给真实原因
        if scopes.allSatisfy({ provider.environment.ready(for: $0, ignoring: ignoring) }) { return true }
        showEnvironment = true
        if checks.isEmpty { checkEnvironment() }
        return false
    }

    // ---- import ----

    enum ImportPage { case selection, keys, results }
    @Published private(set) var importPage: ImportPage?
    @Published private(set) var importSources: [ImportSource] = []
    @Published private(set) var importBatchFailure: String?
    private var importUsesResults = false

    var pendingSources: [ImportSource] { importSources.filter { !$0.succeeded } }
    var importedSourceCount: Int { importSources.filter(\.succeeded).count }

    func importGames(_ urls: [URL], partialResults: Bool) {
        guard !urls.isEmpty, !busy else { return }
        if partialResults {
            importSources = []
            importPage = nil
            pendingImport = []
            if !showKeys { showAddGames = true }
        }
        submitImport(urls, partialResults: partialResults)
    }

    private func submitImport(_ urls: [URL], partialResults: Bool) {
        guard let first = urls.first, !busy else { return }
        importUsesResults = partialResults
        importBatchFailure = nil
        failAction(nil, game: nil)
        inlineFailure = nil
        start(.importGames, game: nil, name: first.lastPathComponent + (urls.count > 1 ? " +\(urls.count - 1)" : "")) { [weak self] sink in
            guard let self else { return nil }
            let wasEmpty = self.games.isEmpty
            do {
                let result = try await self.provider.importGames(GameInputs(paths: urls), partialResults: partialResults, events: sink)
                try Task.checkCancellation()
                if partialResults { self.mergeImportSources(result.sources, submitted: urls) }
                self.selectImportedGame(result.games.first, forWelcome: wasEmpty)
            } catch let failure as JobFailure where partialResults {
                try Task.checkCancellation()
                let rows = ImportSource.rows(in: failure.outcome.result)
                self.mergeImportSources(rows, submitted: urls)
                if failure.code == "keys_missing" {
                    self.pendingImport = self.pendingSources.isEmpty ? urls : self.pendingImportURLs
                    if wasEmpty && !self.showKeys && self.importedSourceCount == 0 {
                        // The welcome flow shows the selection and offers its existing keys step.
                        self.showAddGames = false
                        self.importPage = nil
                    } else {
                        self.importPage = .keys
                    }
                    return nil
                }
                if let imports = failure.outcome.result["imports"] as? [[String: Any]], !imports.isEmpty {
                    let games = await self.provider.importedGames(in: failure.outcome.result)
                    try Task.checkCancellation()
                    self.selectImportedGame(games.first, forWelcome: wasEmpty)
                }
                if !rows.contains(where: { $0.status == .failed && $0.code == failure.code }) {
                    self.importBatchFailure = failure.errorDescription
                }
                throw failure
            } catch {
                if partialResults && !Task.isCancelled { self.importBatchFailure = error.localizedDescription }
                throw error
            }
            return nil
        }
    }

    private var pendingImportURLs: [URL] { pendingSources.map { URL(fileURLWithPath: $0.path) } }

    private func selectImportedGame(_ game: GameInfo?, forWelcome: Bool) {
        guard let game else { return }
        selectedID = game.titleId
        if forWelcome { welcomeGameID = game.titleId }
    }

    private func mergeImportSources(_ rows: [ImportSource], submitted: [URL]) {
        guard !rows.isEmpty else { return }
        // A failed folder scan may expand to child sources on retry.
        let submittedPaths = Set(submitted.map(\.path))
        let returnedIDs = Set(rows.map(\.id))
        importSources.removeAll { !$0.succeeded && submittedPaths.contains($0.path) && !returnedIDs.contains($0.id) }
        var positions = Dictionary(uniqueKeysWithValues: importSources.enumerated().map { ($0.element.id, $0.offset) })
        for row in rows {
            if let index = positions[row.id] {
                if !importSources[index].succeeded { importSources[index] = row }
            } else {
                positions[row.id] = importSources.count
                importSources.append(row)
            }
        }
        if !pendingSources.isEmpty { importPage = .results }
    }

    func retryImport() {
        submitImport(pendingImportURLs, partialResults: true)
    }

    func backFromImportResults() {
        guard !busy else { return }
        if pendingSources.contains(where: \.needsKeys) {
            pendingImport = pendingImportURLs
            keysRejected = pendingSources.contains { $0.code == "keys_outdated" }
            importPage = .keys
        } else {
            importPage = .selection
        }
    }

    func cancelImport() {
        if let session, [.importGames, .keys].contains(session.kind) { stop() }
        closeImport()
        clearInlineFailure()
    }

    private func closeImport() {
        showAddGames = false
        showKeys = false
        importPage = nil
        importSources = []
        pendingImport = []
        importBatchFailure = nil
        keysRejected = false
    }

    // ---- export ----

    /// 勾了的平台，按页面顺序（也是排队顺序）。
    var exportTargets: [ExportPlatform] { ExportPlatform.picked(exportChoice) }

    /// 勾上 / 取消一个平台，当场记住（未开放的点不动）。
    func toggleExport(_ platform: ExportPlatform) {
        guard platform.released else { return }
        exportChoice = ExportPlatform.toggled(exportChoice, platform)
        do { try provider.settings.setExportTargets(exportChoice) } catch { choiceError = error.localizedDescription }
    }

    /// 出这些平台要环境检查里哪几类前提（出 iPhone 包缺 .NET SDK 不挡：适配层先一键装好再接着出）。
    private static func scopes(_ platforms: [ExportPlatform]) -> [String] {
        platforms.contains(.ios) ? ["prepare", "ipa"] : ["prepare"]
    }

    /// 现在不能出包的原因（nil = 能出）。顺序同旧启动器导出页：没勾平台 → 游戏缺文件 → 缺 keys → 环境缺组件 → 有任务在跑。
    func exportBlocker(_ game: GameSummary, platforms chosen: [ExportPlatform]? = nil) -> ExportBlocker? {
        let platforms = chosen ?? exportTargets
        if platforms.isEmpty { return .noPlatform }
        if !game.buildable { return .game(Self.requirementsText(game)) }
        if game.needsKeys, provider.library.keysReady == false { return .keys }
        if busy, session?.export == nil { return .busy }   // 别的出包在跑可以排队
        if checks.isEmpty {
            if checking { return .checking }
            return environmentError != nil ? nil : .environment   // 环境检查本身失败不挡：作业自己校验并给真实原因
        }
        if !Self.scopes(platforms).allSatisfy({ provider.environment.ready(for: $0, ignoring: ["installDotnet"]) }) { return .environment }
        return nil
    }

    /// 出包：勾了几个平台就几个作业（Mac = build、iPhone / iPad = ipa），按页面顺序一个接一个；解包等平台无关阶段作业层共用（游戏库/cache/<程序号>/extract）。
    /// 一个平台没成不挡后面的平台；取消就整次停。
    /// platforms 不给 = 出包页勾的；本机控制口（AI 操作）按命令给一个平台，deviceTarget 只用于 iPhone 出包（提示地址窗口）。
    @discardableResult
    func export(_ game: GameSummary, platforms chosen: [ExportPlatform]? = nil, deviceTarget: [String: Any]? = nil, spaceOverride: Bool = false, location: String? = nil) -> ExportBlocker? {
        if let blocker = exportBlocker(game, platforms: chosen) { return blocker }
        let item = QueuedExport(game: game, platforms: chosen ?? exportTargets, target: deviceTarget ?? currentDeviceTarget, spaceOverride: spaceOverride, location: location)
        exportFor = nil
        welcomeGameID = nil
        if busy {   // 正在出别的：排队，出完接着出（11-08 维护者：出好一个接着出下一个）
            if session?.gameID == game.id || exportQueue.contains(where: { $0.game.id == game.id }) { return .alreadyQueued }
            exportQueue.append(item)
            return nil
        }
        sessionHidden = false
        runExport(item)
        return nil
    }

    private func runExport(_ item: QueuedExport) {
        failAction(nil, game: nil)
        let game = item.game, platforms = item.platforms
        var session = JobSession(kind: .export, gameID: game.id, name: game.name)
        session.export = ExportRun(platforms: platforms)
        start(session) { [weak self] sink in
            for (k, platform) in platforms.enumerated() {
                guard let self, let target = platform.target else { return nil }
                try Task.checkCancellation()
                self.beginPlatform(k)
                do {
                    let job = BuildJob(gameId: game.id, target: target, deviceTarget: target == .iPhone ? item.target : nil, spaceOverride: item.spaceOverride, location: item.location)
                    let r = try await self.provider.build(job, events: sink)
                    self.endPlatform(platform, ExportRun.Outcome(state: .completed, output: r.outputPath, ipa: r.ipa,
                                                                 appBytes: ((r.raw["ipaBytes"] ?? r.raw["outputBytes"]) as? NSNumber)?.int64Value,
                                                                 packBytes: (r.raw["packBytes"] as? NSNumber)?.int64Value))
                } catch let failure as JobFailure {
                    self.endPlatform(platform, ExportRun.Outcome(state: failure.state, message: failure.errorDescription ?? "", code: failure.code,
                                                                 suggestion: failure.outcome.diagnostics.first?["suggestion"] as? String ?? "",
                                                                 failureSummary: failure.outcome.diagnostics.last(where: { ($0["severity"] as? String) == "error" })?["failureSummary"] as? String))
                    if failure.state == .cancelled { throw failure }
                } catch is CancellationError {
                    self.endPlatform(platform, ExportRun.Outcome(state: .cancelled))
                    throw CancellationError()
                } catch {
                    self.endPlatform(platform, ExportRun.Outcome(state: .failed, message: error.localizedDescription))
                }
            }
            if self?.session?.export?.failed.isEmpty == false { throw ExportIncomplete() }
            return nil
        }
    }

    /// 连着的设备里用哪台：玩家选过且还连着 → 它；否则有线连着的第一台；否则第一台（#272）
    nonisolated static func pickDevice(_ connected: [[String: Any]], chosen: String?) -> [String: Any]? {
        if let chosen, let d = connected.first(where: { $0["id"] as? String == chosen }) { return d }
        return connected.first(where: { $0["wired"] as? Bool == true }) ?? connected.first
    }

    var selectedDevice: [String: Any]? { Self.pickDevice(connectedDevices, chosen: chosenDevice) }

    /// 出 iPhone 包的目标：连着的手机优先（多台时用选中的那台），其次玩家选的机型；都没有交给作业层按常见机型出。
    var currentDeviceTarget: [String: Any]? {
        if let device = selectedDevice { return device }
        guard let chosenModel, var model = targetModels.first(where: { $0["productType"] as? String == chosenModel }), let os = targetOS.first else { return nil }
        model["os"] = os
        return model
    }

    func refreshTargets() async {
        guard let probe = try? await provider.targetDevices() else { return }
        connectedDevices = probe["connected"] as? [[String: Any]] ?? []
        targetModels = probe["models"] as? [[String: Any]] ?? []
        targetOS = probe["osChoices"] as? [Int] ?? []
    }

    /// 换到第 k 个平台：进度、里程碑从头算（日志接着记）。
    private func beginPlatform(_ k: Int) {
        guard var session, var run = session.export else { return }
        run.index = k; run.platformStarted = Date()
        session.export = run; session.progress = JobProgress(); session.stage = ""
        self.session = session
    }

    private func endPlatform(_ platform: ExportPlatform, _ outcome: ExportRun.Outcome) {
        guard var session, var run = session.export else { return }
        var outcome = outcome
        outcome.seconds = Date().timeIntervalSince(run.platformStarted)
        run.outcomes[platform] = outcome
        session.export = run; session.progress.finish()
        self.session = session
    }

    /// 出包结果里的一份错误摘要（复制给别人看）：游戏、平台、原因、恢复码。
    func errorSummary(_ platform: String, outcome: ExportRun.Outcome, game: String) -> String {
        let g = games.first { $0.name == game }
        let info = Bundle.main.infoDictionary ?? [:]
        var chip = [CChar](repeating: 0, count: 128), size = 128
        sysctlbyname("machdep.cpu.brand_string", &chip, &size, nil, 0)
        let free = (try? URL(fileURLWithPath: NSHomeDirectory()).resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage ?? 0
        let bytes = { (n: Int64) in ByteCountFormatter.string(fromByteCount: n, countStyle: .file) }
        let device = currentDeviceTarget.map { ($0["model"] ?? $0["name"] ?? "") as? String ?? "" } ?? ""
        let log = (session?.log.suffix(30) ?? []).joined(separator: "\n").replacingOccurrences(of: NSHomeDirectory(), with: "~")
        // 分段拼：一个大数组字面量里混可选链，编译器类型检查会超时
        var lines: [String] = ["S2Arm 错误摘要", "", "【出错】", "平台：\(platform)", "原因：\(outcome.message)"]
        if let code = outcome.code { lines.append("错误码：" + code) }
        if !outcome.suggestion.isEmpty { lines.append("建议：" + outcome.suggestion) }
        lines += ["", "【游戏】", "名称：\(game)"]
        if let g {
            let formats = Set(g.packages.map { $0.format.isEmpty ? "NSP/XCI" : $0.format }).sorted().joined(separator: ",")
            lines += ["程序号：" + g.id, "版本：" + g.display]
            lines.append("本体 / 更新 / DLC：\(g.version("base") ?? "-") / \(g.version("update") ?? "-") / \(g.dlcCount)")
            lines += ["原包大小：" + bytes(g.totalBytes), "形态：" + formats]
        }
        let xcode = provider.client.environment.developerDir.replacingOccurrences(of: "/Contents/Developer", with: "")
        lines += ["", "【电脑】", "S2Arm：\(info["CFBundleShortVersionString"] ?? "?") (\(info["S2ABuildID"] ?? "?"))"]
        lines += ["系统：" + ProcessInfo.processInfo.operatingSystemVersionString, "芯片：" + String(cString: chip)]
        lines += ["内存：" + bytes(Int64(ProcessInfo.processInfo.physicalMemory)), "可用空间：" + bytes(free), "Xcode：" + xcode]
        if !device.isEmpty { lines.append("目标设备：" + device) }
        if let fs = outcome.failureSummary, !fs.isEmpty { lines += ["", "【失败详情】", fs] }
        lines += ["", "【最近日志】", log, "", "反馈：https://github.com/jhaizhou-ops/S2Arm/issues 或 X @happyjoejiang"]
        return lines.joined(separator: "\n")
    }

    /// 把最近一次失败作业的错误摘要放进剪贴板。正文由作业层生成（带任务号、阶段、错误码、真实日志）；没有作业记录才用 fallback。
    func copyErrorReport(platform: String?, gameID: String?, operations: [String]?, fallback: String) async {
        let info = Bundle.main.infoDictionary ?? [:]
        var extra: [String: Any] = ["app": info["CFBundleShortVersionString"] as? String ?? "", "build": info["S2ABuildID"] as? String ?? "",
                                    "xcode": provider.client.environment.developerDir.replacingOccurrences(of: "/Contents/Developer", with: ""),
                                    "checks": checks.filter { !$0.ok }.map { ["name": $0.name, "message": $0.message] }]
        if let platform { extra["platform"] = platform }
        if let device = currentDeviceTarget.flatMap({ ($0["model"] ?? $0["name"]) as? String }) { extra["device"] = device }
        let report = operations == nil ? nil : await provider.client.errorReport(gameID: gameID, operations: operations ?? [], extra: extra)
        let text = report ?? fallback
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
    }

    /// 装机前清单（装机页开着时每几秒调一次）。换了一款先清掉上一款的清单。
    func checkInstall(_ gameID: String) {
        if installGameID != gameID { installGameID = gameID; installReadiness = nil; installError = nil }
        guard !installChecking else { return }
        installChecking = true
        Task {
            do {
                let readiness = try await provider.installReadiness(gameId: gameID)
                if installGameID == gameID { installReadiness = readiness; installError = nil }
            } catch {
                if installGameID == gameID { installError = error.localizedDescription }
            }
            installChecking = false
        }
    }

    /// 能不能开始装：清单说可以，或这次清单读失败（不因探测失败把玩家关在外面，install 作业会给真实原因）。
    func installAllowed(_ gameID: String) -> Bool {
        installGameID == gameID && (installReadiness?.ready == true || (installReadiness == nil && installError != nil))
    }

    /// 在 Xcode 打开引导项目；玩家运行一次后，装机页每 5 秒的重查会自动打勾。
    func openSigningProject() {
        Task {
            do { NSWorkspace.shared.open(URL(fileURLWithPath: try await provider.signingProject())) }
            catch { installError = error.localizedDescription }
        }
    }

    /// 装机 / 删除会让手机上别的电脑装的游戏暂时不能玩（作业层 container_would_drop）：界面弹确认，确认了带 acceptDropped 重跑。
    /// ask = false（控制口发起）不弹，原样失败，由调用方带 acceptDropped 再发。
    struct DropConfirm { let kind: JobSession.Kind; let game: GameSummary; let message: String }
    @Published var dropConfirm: DropConfirm?

    func install(_ game: GameSummary, teamId: String? = nil, device: String? = nil, acceptDropped: Bool = false, ask: Bool = true) {
        guard !busy, installAllowed(game.id) else { return }
        failAction(nil, game: nil)
        // 连着多台时装到出包页选中的那台（和按机型优化的是同一台）；控制口明说了 device 的照它
        let device = device ?? (connectedDevices.count > 1 ? selectedDevice?["id"] as? String : nil)
        start(.install, game: game, name: game.name) { [weak self] sink in
            do { _ = try await self?.provider.install(gameId: game.id, teamId: teamId, device: device, acceptDropped: acceptDropped, events: sink) }
            catch let f as JobFailure where ask && f.code == "container_would_drop" { self?.dropConfirm = DropConfirm(kind: .install, game: game, message: f.errorDescription ?? ""); throw f }
            return nil
        }
    }

    /// 确认框点了「仍然继续」
    func confirmDrop(_ d: DropConfirm) {
        dropConfirm = nil; clearInlineFailure()
        if d.kind == .installRemove { removeFromPhone(d.game, acceptDropped: true) } else { install(d.game, acceptDropped: true) }
    }

    /// 装回上一版手机 S2Arm（只换宿主后新宿主不好用时）。
    func restorePhone(_ game: GameSummary) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.installRestore, game: game, name: game.name) { [weak self] sink in
            try await self?.provider.restorePhone(events: sink)
            return nil
        }
    }

    func removeFromPhone(_ game: GameSummary, acceptDropped: Bool = false, ask: Bool = true) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.installRemove, game: game, name: game.name) { [weak self] sink in
            do { _ = try await self?.provider.removeFromPhone(gameId: game.id, acceptDropped: acceptDropped, events: sink) }
            catch let f as JobFailure where ask && f.code == "container_would_drop" { self?.dropConfirm = DropConfirm(kind: .installRemove, game: game, message: f.errorDescription ?? ""); throw f }
            return nil
        }
    }

    private static func requirementsText(_ game: GameSummary) -> String {
        game.compatibility == "unsupported" ? "这款游戏暂不支持。" : "这款还缺文件：" + game.missing.joined(separator: "；")
    }

    // ---- play ----

    func play(_ game: GameSummary, uiLanguage: String?) {
        guard !busy, !cloudSyncing else { return }
        failAction(nil, game: nil)
        // 查过且启动前提没齐才拦；还没查完不拦（launch 作业自己校验运行文件）
        if !checks.isEmpty, !environmentAllows(["launch"]) { return }
        // 开了 iCloud 存档：先取回 / 处理冲突再开局（S2ArmLauncherModel+CloudSaves）；同步失败本地存档没动，照常开局、错误行写原因
        if cloudFolder != nil {
            cloudSyncing = true
            Task {
                let why = await cloudSync(game.id)
                cloudSyncing = false
                // 同步期间别的作业（导出 / 导入）可能已经开始：这时不开局，免得盖掉它的会话（11-09 审查）
                if busy { failAction(Self.cloudBusyAfterSync, game: game.id); return }
                startPlay(game, uiLanguage: uiLanguage)
                if let why { failAction(Self.cloudFailure(why), game: game.id) }
            }
            return
        }
        startPlay(game, uiLanguage: uiLanguage)
    }

    private func startPlay(_ game: GameSummary, uiLanguage: String?) {
        do {
            let sink = EventSink { [weak self] event in self?.ingest(event) }
            running = try provider.run(InstalledGame(gameId: game.id, name: game.name),
                                       settings: S2ArmAppleAdapter.Settings(uiLanguage: uiLanguage), events: sink)
            session = JobSession(kind: .play, gameID: game.id, name: game.name)
        } catch {
            failAction(error.localizedDescription, game: game.id)
        }
    }

    // ---- 放密钥 / 删除 / 诊断（本机控制口用；参数与作业同旧启动器）----

    /// 放密钥：作业页显示进度，完了重读游戏库（keysReady 跟着更新）。
    func importKeys(_ file: URL) {
        guard !busy else { return }
        failAction(nil, game: nil)
        inlineFailure = nil
        keysRejected = false
        if !showAddGames { showKeys = true }
        start(.keys, game: nil, name: file.lastPathComponent) { [weak self] sink in
            _ = try await self?.provider.importKeys(file, events: sink)
            return nil
        }
    }

    // ---- 存放位置（游戏库结构 v3）----

    /// 重读各存放位置（出包页打开时调；剩余空间是当时的）。
    func loadLocations() {
        Task { [weak self] in
            guard let self, let r = try? await self.provider.locations() else { return }
            self.locations = r.rows; self.lastLocation = r.last
        }
    }

    /// 出包页「存放位置」缺省选哪个：这款现在的位置 → 上次用的位置 → 默认位置。
    func defaultLocation(for game: GameSummary) -> String {
        if game.location.state == "ok" || game.location.state == "offline", !game.location.path.isEmpty { return game.location.path }
        return locations.first(where: { $0.id == lastLocation && $0.connected })?.path ?? locations.first(where: \.isDefault)?.path ?? ""
    }

    /// 移动到其他位置（整个文件夹，存档跟着走）。
    func moveGame(_ game: GameSummary, to folder: URL) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.moveGame, game: game, name: game.name) { [weak self] sink in
            try await self?.provider.moveGame(game.id, to: folder.path, events: sink)
            self?.loadLocations()
            return nil
        }
    }

    /// 清除缓存（出包缓存 + 运行时着色器缓存；镜像、存档不动）。
    func clearCache(_ game: GameSummary) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.clearCache, game: game, name: game.name) { [weak self] sink in
            try await self?.provider.clearCache(game.id, events: sink)
            return nil
        }
    }

    /// 「找不到游戏文件」→ 重新指定文件夹 / 从游戏库移除：很快，不进作业页，失败写在游戏库页上。
    func relinkGame(_ game: GameSummary, folder: URL) {
        Task { [weak self] in
            do { try await self?.provider.relinkGame(game.id, folder: folder.path); self?.refresh() }
            catch { self?.failAction(error.localizedDescription, game: game.id) }
        }
    }

    func forgetGame(_ game: GameSummary) {
        Task { [weak self] in
            do { try await self?.provider.forgetGame(game.id); if self?.selectedID == game.id { self?.selectedID = nil }; self?.refresh() }
            catch { self?.failAction(error.localizedDescription, game: game.id) }
        }
    }

    /// 在访达中选中这款的文件夹（catalog 给的 location.folder：按索引解析、可能在玩家另选的存放位置，不按游戏号拼）；文件夹不在就显示所在的存放位置；
    /// 两个都不在 → 系统提示框说明原因，不在访达里打开空窗口。
    func reveal(_ game: GameSummary) {
        guard let path = [game.location.folder, game.location.path].first(where: { !$0.isEmpty && FileManager.default.fileExists(atPath: $0) }) else {
            explainMissing("s2arm.reveal.folderMissing"); return
        }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    /// 存档位置（services/S2A.Services/SaveFolder.cs）：这款游戏文件夹里的 SaveData/，没有就建出来再在访达里打开（打开的是文件夹里面，玩家直接往里放）
    func openSaveFolder(_ game: GameSummary) {
        guard !game.location.folder.isEmpty, FileManager.default.fileExists(atPath: game.location.folder) else { explainMissing("s2arm.reveal.folderMissing"); return }
        let url = URL(fileURLWithPath: game.location.folder).appendingPathComponent("SaveData", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }
    /// 存档位置里放的存档上次起局没读进来的原因（服务层写在数据根 saves/savedata-problem.txt，读进来后删掉）；没有 = nil
    func saveFolderProblem(_ game: GameSummary) -> String? {
        guard !game.location.folder.isEmpty else { return nil }
        let file = URL(fileURLWithPath: game.location.folder).appendingPathComponent("saves/savedata-problem.txt")
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return nil }
        let why = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return why.isEmpty ? nil : why
    }

    /// 「在访达中显示 .ipa」：装机时组装、整个游戏库共用的那份（默认位置根 S2Arm.ipa）；不在 → 提示框说明，不打开空窗口
    func revealIpa(_ path: String) {
        guard !path.isEmpty, FileManager.default.fileExists(atPath: path) else { explainMissing("s2arm.reveal.ipaMissing"); return }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    private func explainMissing(_ key: String) {
        let alert = NSAlert()
        alert.messageText = Localizer().t(key)
        alert.runModal()
    }

    /// 删这款（IPA / 资源包 / 中间产物 / 缓存；存档与原始游戏文件保留）。
    func deleteGame(_ game: GameSummary) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.delete, game: game, name: game.name) { [weak self] sink in
            try await self?.provider.deleteGame(game.id, events: sink)
            if self?.selectedID == game.id { self?.selectedID = nil }
            return nil
        }
    }

    /// 改名字 / 换封面（nil 不动，空字符串恢复默认）：很快，不进作业页，失败写在出包页上。
    func customize(_ game: GameSummary, name: String? = nil, coverPath: String? = nil) {
        guard !busy, !customizing else { return }
        customizing = true; customizeError = nil
        Task {
            do { try await provider.customizeGame(game.id, name: name, coverPath: coverPath); refresh() }
            catch { customizeError = error.localizedDescription }
            customizing = false
        }
    }

    /// 导出 Mac 端诊断包（path）或拉回连着的 iPhone 上的诊断（path 为 nil）；做完可在访达里显示。
    func diagnostics(exportTo path: URL?) {
        guard !busy else { return }
        failAction(nil, game: nil)
        start(.diagnostics, game: nil, name: path?.lastPathComponent ?? "iPhone") { [weak self] sink in
            guard let self else { return nil }
            if let path { try await self.provider.exportDiagnostics(to: path, events: sink); return path.path }
            return try await self.provider.pullDiagnostics(events: sink)["outputPath"] as? String
        }
    }

    // ---- 允许 AI 操作 S2Arm（本机控制口，见 S2ArmControl）----

    /// 开关状态（--control / S2ARM_CONTROL=1 启动时恒为开）。
    @Published private(set) var aiControl = false
    @Published private(set) var aiControlError: String?
    private var control: S2ArmControl?

    /// 窗口出来时调：开关开着（或强制开）就起控制口。
    public func startControl() {
        aiControl = provider.settings.aiControl || ControlPort.forced
        if aiControl { openControl() }
    }

    /// 环境页的开关：记住选择，当场起 / 停控制口。
    func setAIControl(_ on: Bool) {
        aiControlError = nil
        do { try provider.settings.setAIControl(on) } catch { aiControlError = error.localizedDescription }
        aiControl = on || ControlPort.forced
        if aiControl { openControl() } else { control?.stop(); control = nil }
    }

    private func openControl() {
        guard control == nil else { return }
        do {
            control = try S2ArmControl(model: self) { [weak self] why in
                // 起不来 / control.json 写不上：不算已开，开关退回关，原因摆在环境页（审计 18:00 P2-35，同旧启动器）
                guard let self else { return }
                self.control = nil
                if !ControlPort.forced { try? self.provider.settings.setAIControl(false); self.aiControl = false }
                self.aiControlError = why
                ControlPort.reportStartFailure(privateRoot: self.provider.client.environment.privateRoot, why: why)
            }
        } catch {
            aiControlError = error.localizedDescription
            ControlPort.reportStartFailure(privateRoot: provider.client.environment.privateRoot, why: error.localizedDescription)
            if !ControlPort.forced { try? provider.settings.setAIControl(false); aiControl = false }
        }
    }

    /// 关掉界面上的提示 / 错误（同旧启动器「知道了」）。
    func dismissErrors() { failAction(nil, game: nil); environmentError = nil; aiControlError = nil; installError = nil }

    /// 密钥齐没齐（游戏库读回来的；nil = 还没读过）。
    var keysReady: Bool? { provider.library.keysReady }

    // ---- jobs ----

    /// 起一个作业：body 交适配层（事件进 sink），回来的路径做完后可在访达里显示。可停（stop = 取消这个 Task，适配层请作业停）。
    private func start(_ kind: JobSession.Kind, game: GameSummary?, name: String,
                       _ body: @escaping @MainActor (EventSink) async throws -> String?) {
        start(JobSession(kind: kind, gameID: game?.id, name: name), body)
    }

    private func start(_ fresh: JobSession, _ body: @escaping @MainActor (EventSink) async throws -> String?) {
        session = fresh
        if Self.phoneKinds.contains(fresh.kind) { phoneResult = nil }
        let sink = EventSink { [weak self] event in self?.ingest(event) }
        work = Task { [weak self] in
            do {
                let reveal = try await body(sink)
                self?.finish(.completed, message: "", reveal: reveal)
            } catch let failure as JobFailure {
                self?.finish(failure.state, message: failure.errorDescription ?? "")
            } catch is CancellationError {
                self?.finish(.cancelled, message: "")
            } catch is ExportIncomplete {
                self?.finish(.failed, message: "")
            } catch {
                self?.finish(.failed, message: error.localizedDescription)
            }
        }
    }

    private func ingest(_ event: EngineEvent) {
        guard var session = session, !isFinished(session) else { return }
        if let id = event.fields["taskId"] as? String, !id.isEmpty, session.taskID != id { session.taskID = id; self.session = session }
        if event.ev == "heartbeat" { return }
        if event.ev == "space", let space = PackSpace(event.fields["space"]) { session.space = space; self.session = session; return }
        if let stage = event.stage, !stage.isEmpty { session.stage = stage }
        session.progress.ingest(event)
        if !event.message.isEmpty {
            session.message = event.message
            let line = event.ev == "stage" ? "▸ " + event.message : event.message
            if session.log.last != line { session.log.append(line) }
            if session.log.count > Self.logLimit { session.log.removeFirst(session.log.count - Self.logLimit) }
        }
        self.session = session
        // 一局的结局只从事件来（run 是同步起的）；别的作业由 Task 结束收尾（中间可能有好几个作业，如先装 .NET 再出包）
        if session.kind == .play, event.isFinal {
            let state = JobOutcome.State(rawValue: event.fields["state"] as? String ?? "") ?? .interrupted
            finish(state, message: event.message)
            // 游戏里点了「切换游戏」（宿主菜单栏，pack/jobs/build.py launch 结果带 returnToLibrary）：不停在结果页，回到游戏库并切到前台，玩家直接点下一款
            if state == .completed, (event.fields["result"] as? [String: Any])?["returnToLibrary"] as? Bool == true {
                backToLibrary()
                NSApp.activate(ignoringOtherApps: true)
            }
        }
    }

    private func isFinished(_ s: JobSession) -> Bool { if case .finished = s.phase { return true }; return false }

    private func finish(_ state: JobOutcome.State, message: String, reveal: String? = nil) {
        guard var session = session, !isFinished(session) else { return }
        session.phase = .finished(state)
        if !message.isEmpty {
            session.message = message
            if session.log.last != message { session.log.append(message) }
        }
        session.reveal = reveal
        session.progress.finish()
        self.session = session
        running = nil; work = nil
        // 放密钥、添加游戏、欢迎页上的下载：结果留在原页（弹窗 / 欢迎页那一行），不换到日志页（11-08 维护者）；环境页发起的下载成了也直接回来
        if isInline(session) || (state == .completed && Self.repairKinds.contains(session.kind)) {
            self.session = nil
            inlineFailure = (state == .failed || state == .interrupted) ? (session.kind, session.message) : nil
        }
        if state == .completed && session.kind == .install { installDone = session.gameID }
        if Self.phoneKinds.contains(session.kind), let id = session.gameID { phoneResult = (id, session.name, state == .completed) }
        // 一局结束：开了 iCloud 存档就上传这局的进度（失败写在出包页错误行上，本地存档不动）
        if session.kind == .play, let id = session.gameID, cloudFolder != nil {
            cloudSyncing = true
            Task {
                let why = await cloudSync(id)
                cloudSyncing = false
                if let why { failAction(Self.cloudFailure(why), game: id) }
            }
        }
        if state == .completed && session.kind == .diagnostics, let reveal { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: reveal)]) }
        if session.kind == .export, let id = session.gameID {
            finishedExports[id] = session
            if state == .cancelled {
                exportQueue = []
            } else if !exportQueue.isEmpty {
                let next = exportQueue.removeFirst()
                self.session = nil
                runExport(next)
            }
        }
        if state == .completed && session.kind == .keys {
            if pendingImport.isEmpty {
                closeImport()
            } else {
                let urls = pendingImport
                pendingImport = []
                submitImport(urls, partialResults: true)
            }
        }
        if state == .completed && session.kind == .importGames && importUsesResults &&
            pendingImport.isEmpty && pendingSources.isEmpty {
            closeImport()
        }
        if Self.repairKinds.contains(session.kind) {
            if state == .completed, !repairQueue.isEmpty { repair(repairQueue.removeFirst()) } else if state != .completed { repairQueue = [] }
        }
        refresh()   // 运行文件 / 存档 / 游戏库状态可能变了
    }

    private static let repairKinds: Set<JobSession.Kind> = [.downloadMetal, .downloadIosPlatform, .installDotnet]

    /// 这个作业的进度留在原页（弹窗 / 欢迎页），不换到作业日志页。
    func isInline(_ s: JobSession) -> Bool {
        [.keys, .importGames, .install, .installRemove, .installRestore, .delete, .diagnostics].contains(s.kind) || (showsWelcome && Self.repairKinds.contains(s.kind))
    }

    /// 欢迎页的「一键出包」：出 iPhone / iPad。连着手机、环境齐就直接开始；否则进出包页，那里说清缺什么、提醒插手机、可以选机型（不会悄悄换目标）。
    func oneClickExport() {
        guard let game = welcomeGame else { return }
        Task {
            await refreshTargets()
            if connectedDevices.isEmpty || export(game, platforms: [.ios]) != nil {
                if !exportTargets.contains(.ios) { toggleExport(.ios) }   // 出包页缺省不勾（维护者 11-02），一键出包就是出 iPhone，替玩家勾上
                exportFor = game.id
                welcomeGameID = nil
            }
        }
    }

    func clearInlineFailure() { inlineFailure = nil }

    func stop() {
        guard var session = session, session.phase == .running else { return }
        session.phase = .stopping
        self.session = session
        if session.kind == .play, let running { provider.control(running, .stop) } else { work?.cancel() }
    }

    /// 回库：只有作业结束了才能关掉作业页。
    /// 顶栏「游戏库」：从任何页面回到游戏库；出包还在跑就留在后台，游戏库上的进度条可以回去。
    func goToLibrary() {
        exportFor = nil; viewingResult = nil; showEnvironment = false
        guard let current = session else { return }
        if isFinished(current) { session = nil } else { sessionHidden = true }
    }

    func backToLibrary() {
        if viewingResult != nil { viewingResult = nil; return }
        sessionHidden = false
        guard let current = session, isFinished(current) else { return }
        session = nil
    }
}
