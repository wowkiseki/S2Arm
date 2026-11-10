// S2Arm（不是上游文件）：一款游戏的存档弹窗（saveStatus / saveBackup / saveInspect / saveRestore，见 S2ArmLauncherModel+Saves）。
// 打开就读当前存档；「备份到…」存成玩家选位置的 .s2asave；「选择备份…」先检查备份、给预览，确认覆盖后才恢复（覆盖前作业层自动留一份当前存档）。
// 作业的进度、结果和失败原因都留在弹窗里，不跳作业页。作业由弹窗自己的 SaveSheetState 发起，不占模型的 session；
// 游戏在跑或别的任务在用游戏库时作业层拿不到执行锁、原话报错，界面同时按 model.busy 先把按钮置灰。
import AppKit
import SwiftUI
import UniformTypeIdentifiers
import NXCore
import NXModel
import NXLauncher
import S2ArmAppleAdapter

/// 弹窗里正在跑的存档作业、它的进度，和最近一次的结果 / 失败。
@MainActor
final class SaveSheetState: ObservableObject {
    enum Op: String { case status, backup, inspect, restore }

    @Published private(set) var running: Op?
    @Published private(set) var progress = JobProgress()
    @Published private(set) var status: SaveStatusInfo?
    @Published private(set) var preview: SavePreviewInfo?
    /// 最近一次做成的结果（一句已翻好的话）与它是不是警告。
    @Published private(set) var result: (text: String, warns: Bool)?
    /// 最近一次失败：作业层原话，或界面自己的一句（都已按界面语言给出）。
    @Published private(set) var failure: String?
    private var work: Task<Void, Never>?

    var busy: Bool { running != nil }

    /// 起一个作业：清掉上一次的结果与失败，事件转回主线程按顺序进进度。
    func run(_ op: Op, failed: @escaping (Error) -> String, _ body: @escaping @MainActor (EventSink) async throws -> Void) {
        guard running == nil else { return }
        running = op; progress = JobProgress(); result = nil; failure = nil
        let sink = EventSink { [weak self] event in
            DispatchQueue.main.async { if event.ev != "heartbeat" { self?.progress.ingest(event) } }
        }
        work = Task { [weak self] in
            do { try await body(sink) } catch is CancellationError {
            } catch let f as JobFailure where f.state == .cancelled {
            } catch { self?.failure = failed(error) }
            self?.running = nil; self?.work = nil
        }
    }

    /// 停掉读状态 / 备份 / 检查（恢复不停：作业层自己做完或回退）。
    func stop() { if running != .restore { work?.cancel() } }

    func setStatus(_ s: SaveStatusInfo) { status = s }
    func setPreview(_ p: SavePreviewInfo?) { preview = p }
    func setResult(_ text: String, warns: Bool = false) { result = (text, warns) }
}

struct SaveManageSheet: View {
    @ObservedObject var model: S2ArmLauncherModel
    @EnvironmentObject var loc: Localizer
    @Environment(\.dismiss) private var dismiss
    let gameID: String
    /// 拖到游戏库这款卡片上的存档（LauncherView+Library）：打开就先检查它，同拖进本窗口
    let source: URL?
    @StateObject private var state = SaveSheetState()
    @State private var confirming = false
    @State private var dropTargeted = false

    init(model: S2ArmLauncherModel, gameID: String, source: URL? = nil) {
        self.model = model
        self.gameID = gameID
        self.source = source
    }

    private static let backupType = UTType(exportedAs: "org.s2arm.save-backup", conformingTo: .zip)
    private var game: GameSummary? { model.games.first { $0.id == gameID } }
    private var gameName: String { game?.name ?? gameID }
    /// 别的作业（一局、出包、导入……）在跑，或弹窗自己在跑：存档动作先不让点。
    private var blocked: Bool { model.busy || state.busy }

    private func t(_ id: String) -> String { BackendText.launcherText(id, language: loc.lang == .zh ? "zh-Hans" : "en") }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text(t("save.title")).font(.manrope(26, .heavy)).tracking(-0.6)
                Text(gameName).font(.manrope(14)).foregroundStyle(.secondary).lineLimit(1)
                Text(t("save.hint")).font(.manrope(12.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true).padding(.top, 6)
                // 存档位置 SaveData：原生存档放进去下次起局就用（services SaveFolder.cs）；上次没读进来的原因写在这里
                Text(t("save.folder.note")).font(.manrope(12.5)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true).padding(.top, 4)
                if let game, let why = model.saveFolderProblem(game) {
                    Label { Text(t("save.folder.problem").replacingOccurrences(of: "{0}", with: why)).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: "exclamationmark.triangle.fill") }
                        .font(.manrope(12)).foregroundStyle(LauncherPalette.danger).padding(.top, 4)
                }
                if let game {
                    Button(t("save.folder.open")) { model.openSaveFolder(game) }
                        .buttonStyle(LauncherSecondaryButtonStyle()).padding(.top, 8)
                        .accessibilityIdentifier("save.folder.open")
                }
            }
            .padding(.horizontal, 30).padding(.top, 28).padding(.bottom, 16)
            feedback.padding(.horizontal, 30)
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    currentCard
                    backupCard
                    restoreCard
                }
                .padding(.horizontal, 30).padding(.top, 8).padding(.bottom, 24)
            }
            Divider().opacity(0.3)
            HStack(spacing: 14) {
                if model.busy {
                    Text(t("save.busy")).font(.manrope(12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                if state.busy, state.running != .restore {
                    Button(t("save.stop")) { state.stop() }.buttonStyle(LauncherSecondaryButtonStyle())
                }
                Button(t("save.done")) { dismiss() }
                    .buttonStyle(LauncherSecondaryButtonStyle()).keyboardShortcut(.cancelAction)
                    .disabled(state.running == .restore)
            }
            .padding(.horizontal, 30).padding(.vertical, 16)
        }
        .alert(t("save.confirmTitle"), isPresented: $confirming) {
            Button(t("save.confirm"), role: .destructive) { restore() }
            Button(t("save.cancel"), role: .cancel) {}
        } message: {
            Text(String(format: t("save.confirmMessage"), gameName))
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 12).strokeBorder(LauncherPalette.cream, lineWidth: 2).padding(8).allowsHitTesting(false)
            }
        }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted, perform: drop)
        .onAppear { refresh(); if let source, !blocked { inspect(source) } }
        .onDisappear { state.stop() }
        .interactiveDismissDisabled(state.running == .restore)
        .frame(width: 600, height: 640)
        .background(LauncherPalette.background)
        .font(.manrope(13))
        .preferredColorScheme(.dark)
        .tint(LauncherPalette.cream)
    }

    /// 正在跑：做什么 + 进度条；做完 / 失败：一句结果或原因。
    @ViewBuilder private var feedback: some View {
        if let op = state.running {
            SaveProgressLine(title: t("save.working." + op.rawValue), progress: state.progress).padding(.bottom, 14)
        } else if let failure = state.failure {
            Label { Text(failure).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: "exclamationmark.circle.fill") }
                .font(.manrope(12.5)).foregroundStyle(LauncherPalette.danger).padding(.bottom, 14)
        } else if let result = state.result {
            Label { Text(result.text).fixedSize(horizontal: false, vertical: true) } icon: {
                Image(systemName: result.warns ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
            }
            .font(.manrope(12.5, .semibold)).foregroundStyle(result.warns ? LauncherPalette.building : LauncherPalette.ready).padding(.bottom, 14)
        }
    }

    private var currentCard: some View {
        CardGroup(t("save.current")) {
            if let s = state.status {
                if s.hasSave {
                    CardRow(t("save.files"), dot: LauncherPalette.ready) {
                        Text("\(s.fileCount)").font(.manrope(13, .semibold)).monospacedDigit()
                    }
                    CardDivider()
                    CardRow(t("save.size")) {
                        Text(FreeSpaceView.size(s.totalBytes)).font(.manrope(13, .semibold)).monospacedDigit()
                    }
                } else {
                    CardRow(t("save.none"), dot: LauncherPalette.unbuilt) { EmptyView() }
                }
                if s.recovered {
                    CardDivider()
                    CardRow(t("save.recovered"), dot: LauncherPalette.building) { EmptyView() }
                }
                if !s.recoveryFailed.isEmpty {
                    CardDivider()
                    CardRow(t("save.recoveryFailed"), dot: LauncherPalette.danger) {
                        Button(t("save.reveal")) { reveal(s.recoveryFailed) }.buttonStyle(LauncherSecondaryButtonStyle())
                    }
                }
                if !s.previousBackup.isEmpty {
                    CardDivider()
                    CardRow(t("save.previous"), detail: t("save.previousDetail")) {
                        Button(t("save.reveal")) { reveal(s.previousBackup) }.buttonStyle(LauncherSecondaryButtonStyle())
                    }
                }
            } else {
                CardRow(t(state.running == .status ? "save.working.status" : "save.unknown")) {
                    Button(t("save.recheck")) { refresh() }.buttonStyle(LauncherSecondaryButtonStyle()).disabled(blocked)
                }
            }
        }
    }

    private var backupCard: some View {
        CardGroup(t("save.backup")) {
            CardRow(t("save.backupRow"), detail: t("save.backupDetail")) {
                Button(t("save.backupButton")) { backup() }.buttonStyle(LauncherSecondaryButtonStyle())
                    .disabled(blocked || state.status?.hasSave != true)
            }
        }
    }

    private var restoreCard: some View {
        CardGroup(t("save.restore"), footnote: t("save.restoreNote") + "\n" + t("save.dropHint")) {
            CardRow(t("save.chooseRow"), detail: state.preview?.source.lastPathComponent ?? t("save.chooseDetail")) {
                Button(t("save.choose")) { choose() }.buttonStyle(LauncherSecondaryButtonStyle()).disabled(blocked)
            }
            if let p = state.preview {
                CardDivider()
                CardRow(t("save.from")) { value(p.gameName.isEmpty ? gameName : p.gameName) }
                CardDivider()
                CardRow(t("save.version")) { value("\(p.gameVersion)") }
                if let created = p.createdAt {
                    CardDivider()
                    CardRow(t("save.created")) { value(created.formatted(date: .abbreviated, time: .shortened)) }
                }
                CardDivider()
                CardRow(t("save.files")) { value("\(p.fileCount)") }
                CardDivider()
                CardRow(t("save.size")) { value(FreeSpaceView.size(p.totalBytes)) }
                CardDivider()
                CardRow(t("save.verified"), detail: p.notice.isEmpty ? "" : loc.backend(p.notice),
                        dot: p.notice.isEmpty ? LauncherPalette.ready : LauncherPalette.building) {
                    Button { confirming = true } label: {
                        Text(t("save.restoreButton")).font(.manrope(13.5, .bold)).foregroundStyle(.black)
                            .padding(.horizontal, 18).padding(.vertical, 8)
                            .background(LauncherPalette.cream, in: Capsule()).contentShape(Capsule())
                    }
                    .buttonStyle(.plain).disabled(blocked).opacity(blocked ? 0.45 : 1)
                }
            }
        }
    }

    private func value(_ text: String) -> some View {
        Text(text).font(.manrope(13, .semibold)).monospacedDigit().lineLimit(1)
    }

    /// 失败原因：作业层原话按界面语言显示；读不全 / 起不来给界面自己的话。
    private func reason(_ error: Error) -> String {
        if error is SaveResultIncomplete { return t("save.incomplete") }
        if let f = error as? JobFailure {
            return f.state == .interrupted || (f.errorDescription ?? "").isEmpty ? t("save.interrupted") : loc.backend(f.errorDescription ?? "")
        }
        if let e = error as? JobClientError { return loc.backend(e.message) }
        return t("save.interrupted")
    }

    private func refresh() {
        state.run(.status, failed: reason) { [model, gameID, state] events in
            state.setStatus(try await model.saveStatus(gameID, events: events))
        }
    }

    /// 备份完重读一次状态（文件数可能变了）；失败时原存档不动，作业层原话留在弹窗里。
    private func backup() {
        let panel = NSSavePanel()
        panel.title = t("save.backupPanel")
        panel.allowedContentTypes = [Self.backupType]
        panel.canCreateDirectories = true
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyyMMdd-HHmm"
        let safe = gameName.components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: " ")
        panel.nameFieldStringValue = "\(safe) \(stamp.string(from: Date())).s2asave"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let done = t("save.backupDone")
        state.run(.backup, failed: reason) { [model, gameID, state] events in
            let path = try await model.saveBackup(gameID, to: url, events: events)
            state.setResult(String(format: done, URL(fileURLWithPath: path).lastPathComponent))
        }
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.title = t("save.choosePanel")
        panel.allowedContentTypes = [Self.backupType, .zip, .folder]
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        inspect(url)
    }

    private func inspect(_ url: URL) {
        state.setPreview(nil)
        state.run(.inspect, failed: reason) { [model, gameID, state] events in
            state.setPreview(try await model.saveInspect(gameID, source: url, events: events))
        }
    }

    /// 拖进来的第一项走和「选择」同一条路：先检查、看预览，确认后才覆盖。
    private func drop(_ providers: [NSItemProvider]) -> Bool {
        guard !blocked, let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url, url.isFileURL else { return }
            DispatchQueue.main.async { inspect(url) }
        }
        return true
    }

    /// 恢复成了清掉预览（同一份备份要再恢复得重新检查），再重读状态；事务记录没清完用作业层原话、按警告显示。
    private func restore() {
        guard let preview = state.preview else { return }
        let kept = t("save.restoreDone"), plain = t("save.restoreDoneNoOriginal")
        state.run(.restore, failed: reason) { [model, gameID, state, loc] events in
            let r = try await model.saveRestore(gameID, preview: preview, events: events)
            state.setPreview(nil)
            if let s = try? await model.saveStatus(gameID) { state.setStatus(s) }
            if !r.cleanupWarning.isEmpty { state.setResult(loc.backend(r.cleanupWarning), warns: true) }
            else { state.setResult(r.hadOriginal ? kept : plain) }
        }
    }

    private func reveal(_ path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }
}

/// 一行进度：在做什么 + 进度条 + 「百分比 · 已处理 / 总量 · 已用 · 还剩」（同 InlineProgress 的样式，读数来自作业事件）。
private struct SaveProgressLine: View {
    let title: String
    let progress: JobProgress
    @EnvironmentObject var loc: Localizer

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.manrope(13, .semibold))
            if let fraction = progress.fraction {
                ProgressView(value: fraction).progressViewStyle(.linear).tint(LauncherPalette.cream)
            } else {
                ProgressView().progressViewStyle(.linear).tint(LauncherPalette.cream)
            }
            TimelineView(.periodic(from: .now, by: 1)) { timeline in
                let parts = [progress.fraction.map { "\(Int($0 * 100))%" }, progress.unit == "%" ? "" : progress.countText,
                             progress.unit == "B" ? progress.rateText : "", progress.clock(at: timeline.date, left: loc.t("prog.eta"))]
                Text(parts.compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.manrope(12.5, .medium)).monospacedDigit().foregroundStyle(.secondary).lineLimit(1)
            }
        }
    }
}
