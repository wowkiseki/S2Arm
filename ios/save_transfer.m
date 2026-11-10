// 弱链接[ios]：S2A_SaveTransfer（C# 存档迁移入口；未链服务层时向用户报错）
// 弱链接[ios]：S2A_SaveTransferFree（释放 C# 存档迁移返回的字符串）
#import "save_transfer.h"
#import "s2a_l10n.h"
#include "save_gate.h"
#include "s2a_launch_ctx.h"
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <stdlib.h>
#include <string.h>

extern char *S2A_SaveTransfer(const char *request) __attribute__((weak_import));
extern void S2A_SaveTransferFree(char *response) __attribute__((weak_import));
void s2a_host_shutdown(const char *why);
static NSString *const ManageKey = @"S2ASaveManageOnOpen";

// 存档位置 SaveData（services/S2A.Services/SaveFolder.cs，11-09 维护者定）：本款文档根下的 SaveData（多款容器 Documents/games/<游戏号>/SaveData，
//   单款包 Documents/SaveData），里面是原生存档格式；放进去下次开始游戏就读，退出时写回最新进度。这里按同一规则算出「文件」App 里的路径给玩家看
//   （文件夹先建出来，「文件」App 里马上看得到），上次没读进来的原因（数据根 savedata-problem.txt，服务层写）接在后面
static NSString *save_location(NSString *root) {
    NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    const char *d = getenv("S2A_DOCS");
    NSString *folder = [(d && *d ? @(d) : documents) stringByAppendingPathComponent:@"SaveData"];
    [NSFileManager.defaultManager createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:nil];
    char *rd = realpath(documents.fileSystemRepresentation, NULL), *rf = realpath(folder.fileSystemRepresentation, NULL);
    NSString *base = rd ? @(rd) : documents, *full = rf ? @(rf) : folder; free(rd); free(rf);
    if (![full hasPrefix:[base stringByAppendingString:@"/"]]) return @"";
    NSString *path = [[[full substringFromIndex:base.length + 1] pathComponents] componentsJoinedByString:@" → "];
    NSString *name = NSBundle.mainBundle.infoDictionary[@"CFBundleDisplayName"] ?: @"S2Arm";
    NSString *text = [NSString stringWithFormat:S2AL(@"save.location"), name, path, name];
    NSString *why = root ? [[NSString stringWithContentsOfFile:[root stringByAppendingPathComponent:@"savedata-problem.txt"] encoding:NSUTF8StringEncoding error:nil]
                               stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet] : nil;
    return why.length ? [text stringByAppendingFormat:@"\n\n%@", [NSString stringWithFormat:S2AL(@"save.location.problem"), why]] : text;
}

static NSString *data_root(void) {
    const char *configured = getenv("S2A_RYU_DATA");
    NSString *root = configured ? [NSString stringWithUTF8String:configured] :
        [NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject stringByAppendingPathComponent:@"S2A/ryujinx"];
    // Foundation may shorten /private/var back to /var. Resolve an existing
    // ancestor with POSIX realpath, including before the first save is created.
    NSString *ancestor = root;
    NSMutableArray<NSString *> *suffix = [NSMutableArray new];
    while (![NSFileManager.defaultManager fileExistsAtPath:ancestor] && ancestor.length > 1) {
        [suffix insertObject:ancestor.lastPathComponent atIndex:0];
        ancestor = ancestor.stringByDeletingLastPathComponent;
    }
    char *resolved = realpath(ancestor.fileSystemRepresentation, NULL);
    if (!resolved) return nil;
    NSString *canonical = [NSFileManager.defaultManager stringWithFileSystemRepresentation:resolved length:strlen(resolved)];
    free(resolved);
    for (NSString *part in suffix) canonical = [canonical stringByAppendingPathComponent:part];
    return canonical;
}
static NSString *work_root(void) {
    return [[data_root() stringByDeletingLastPathComponent] stringByAppendingPathComponent:@"save-transfer"];
}
// 这款的存档身份：多款容器在本款随行目录 games/<游戏号>/save-identity.json（pack/ship_ext.py 放的），单款包在 App 包根。
//   原先一律读包根：容器里读不到，存档管理页报「缺少游戏身份」，容器玩家没有存档入口（审计 P2-170 壳那一半）
static NSDictionary *game_identity(void) {
    const s2a_launch_ctx *c = s2a_launch_ctx_container() ? s2a_launch_ctx_get() : NULL;
    NSURL *url = c && c->module_dir[0] ? [NSURL fileURLWithPath:[@(c->module_dir) stringByAppendingPathComponent:@"save-identity.json"]]
                                       : [NSBundle.mainBundle URLForResource:@"save-identity" withExtension:@"json"];
    NSData *data = url ? [NSData dataWithContentsOfURL:url] : nil;
    id game = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    return [game isKindOfClass:NSDictionary.class] ? game : nil;
}
// 服务层错误说明的语言（#316）：请求带上 App 语言，服务层（services/S2A.Services/SaveTransfer.cs T）按它出中文或英文；判据同 S2AL 字符串表
static NSString *save_language(void) { return s2a_l10n_zh() ? @"zh-Hans" : @"en"; }
// iCloud 存档同步（shell/Sources/S2ArmShellHost/CloudSaves.swift 经 s2a_shell.h 调）：本款作业公共字段 / 转发一次作业。强定义盖过 S2ArmShellBridge 的弱缺省
char *s2a_shell_save_context(void) {
    @autoreleasepool {
        NSString *root = data_root(); NSDictionary *game = game_identity();
        if (!root || !game || !S2A_SaveTransfer || !S2A_SaveTransferFree) return NULL;
        NSData *json = [NSJSONSerialization dataWithJSONObject:@{ @"game": game, @"dataRoot": root, @"workRoot": work_root(), @"language": save_language() } options:0 error:nil];
        return json ? strndup(json.bytes, json.length) : NULL;
    }
}
char *s2a_shell_save_request(const char *json) {
    if (!json || !S2A_SaveTransfer || !S2A_SaveTransferFree) return NULL;
    char *raw = S2A_SaveTransfer(json);
    char *copy = raw ? strdup(raw) : NULL;
    if (raw) S2A_SaveTransferFree(raw);
    return copy;
}
// 列不出事务目录时记下原因（审计 P2-9：原先读不到也当「有未完成事务」，恢复再失败时提示说的是事务，玩家无从下手）。
//   仍进存档页、仍先试恢复（服务层能读到就照常恢复）；恢复失败时提示写明是「读不到操作记录目录」及系统原因
static NSString *g_journal_error;
static BOOL recovery_pending(void) {
    g_journal_error = nil;
    if (!data_root()) return YES;
    NSString *directory = [work_root() stringByAppendingPathComponent:@"save-transactions"];
    if (![NSFileManager.defaultManager fileExistsAtPath:directory]) return NO;
    NSError *error = nil;
    NSArray *entries = [NSFileManager.defaultManager contentsOfDirectoryAtPath:directory error:&error];
    // Even an unreadable journal directory must enter management, never skip recovery.
    if (error) { g_journal_error = error.localizedDescription ?: S2AL(@"save.unknownReason"); return YES; }
    for (NSString *entry in entries) if ([entry.pathExtension isEqualToString:@"json"]) return YES;
    return NO;
}
int s2a_ios_save_management_requested(void) {
    @autoreleasepool {
        return [NSUserDefaults.standardUserDefaults boolForKey:ManageKey] || recovery_pending();
    }
}

@interface S2ASaveManager : UIViewController <UIDocumentPickerDelegate>
@property(copy) void (^startGame)(void);
@property(strong) NSDictionary *game;
@property(strong) UILabel *status;
@property(strong) UIButton *importButton, *exportButton, *backupButton, *startButton, *overrideButton;
@property(strong) UIActivityIndicatorView *spinner;
@property(strong) NSDictionary *preview;
@property(copy) NSString *importPath, *backupPath, *temporaryExportPath;
@property(weak) UIDocumentPickerViewController *exportPicker;
@property(copy) NSString *dataRoot, *workRoot;
@property(strong) dispatch_queue_t worker;
@property BOOL busy, hasSave, recoveryOverridden;
@property enum s2a_save_phase phase;
@end

@implementation S2ASaveManager
- (UIInterfaceOrientationMask)supportedInterfaceOrientations { return UIInterfaceOrientationMaskLandscape; }
- (UILabel *)label:(NSString *)text style:(UIFontTextStyle)style {
    UILabel *label = [UILabel new]; label.text = text; label.numberOfLines = 0;
    label.font = [UIFont preferredFontForTextStyle:style]; label.adjustsFontForContentSizeCategory = YES;
    return label;
}
- (UIButton *)button:(NSString *)title action:(SEL)action {
    UIButton *button = [UIButton buttonWithType:UIButtonTypeSystem];
    [button setTitle:title forState:UIControlStateNormal];
    button.titleLabel.font = [UIFont preferredFontForTextStyle:UIFontTextStyleHeadline];
    button.titleLabel.adjustsFontForContentSizeCategory = YES; button.titleLabel.numberOfLines = 0;
    UIButtonConfiguration *configuration = [UIButtonConfiguration plainButtonConfiguration];
    configuration.contentInsets = NSDirectionalEdgeInsetsMake(12, 16, 12, 16);
    button.configuration = configuration;
    [button.heightAnchor constraintGreaterThanOrEqualToConstant:44].active = YES;
    [button addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];
    return button;
}
- (void)viewDidLoad {
    [super viewDidLoad];
    self.view.backgroundColor = UIColor.systemBackgroundColor;
    self.worker = dispatch_queue_create("dev.s2arm.save-transfer", DISPATCH_QUEUE_SERIAL);
    self.phase = s2a_save_next(S2A_SAVE_CHECKING, recovery_pending() ? S2A_SAVE_EV_OPEN_TX : S2A_SAVE_EV_OPEN_NO_TX);
    UIScrollView *scroll = [UIScrollView new]; scroll.translatesAutoresizingMaskIntoConstraints = NO;
    [self.view addSubview:scroll];
    UIStackView *stack = [UIStackView new]; stack.axis = UILayoutConstraintAxisVertical;
    stack.spacing = 12; stack.translatesAutoresizingMaskIntoConstraints = NO; [scroll addSubview:stack];
    UILayoutGuide *safe = self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [scroll.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor], [scroll.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],
        [scroll.topAnchor constraintEqualToAnchor:safe.topAnchor], [scroll.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor],
        [stack.leadingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.leadingAnchor constant:24],
        [stack.trailingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.trailingAnchor constant:-24],
        [stack.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor constant:20],
        [stack.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor constant:-20],
        [stack.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor constant:-48]]];
    UILabel *title = [self label:S2AL(@"save.title") style:UIFontTextStyleTitle1];
    title.accessibilityTraits |= UIAccessibilityTraitHeader; [stack addArrangedSubview:title];
    [stack addArrangedSubview:[self label:S2AL(@"save.intro") style:UIFontTextStyleBody]];
    UILabel *where = [self label:@"" style:UIFontTextStyleFootnote]; where.textColor = UIColor.secondaryLabelColor; where.accessibilityIdentifier = @"save.location";
    self.status = [self label:S2AL(@"save.checking") style:UIFontTextStyleBody]; [stack addArrangedSubview:self.status];
    self.spinner = [[UIActivityIndicatorView alloc] initWithActivityIndicatorStyle:UIActivityIndicatorViewStyleMedium];
    self.spinner.hidesWhenStopped = YES; [stack addArrangedSubview:self.spinner];
    self.exportButton = [self button:S2AL(@"save.export") action:@selector(exportSave)];
    self.importButton = [self button:S2AL(@"save.import") action:@selector(importSave)];
    self.backupButton = [self button:S2AL(@"save.exportBackup") action:@selector(exportBackup)];
    self.startButton = [self button:S2AL(@"save.start") action:@selector(start)];
    // 恢复失败时才出现：单独确认后放开「开始游戏」（审计 P0-1：失败分支不放开，玩家这次安装就再也进不了游戏）
    self.overrideButton = [self button:S2AL(@"save.override") action:@selector(confirmOverride)];
    for (UIButton *button in @[self.exportButton, self.importButton, self.backupButton, self.startButton, self.overrideButton]) [stack addArrangedSubview:button];
    [stack addArrangedSubview:where];
    self.dataRoot = data_root();
    where.text = save_location(self.dataRoot);
    if (!self.dataRoot) {
        self.phase = s2a_save_next(self.phase, S2A_SAVE_EV_NO_ROOT);
        self.status.text = S2AL(@"save.noRoot");
        [self updateButtons]; return;
    }
    self.workRoot = [[self.dataRoot stringByDeletingLastPathComponent] stringByAppendingPathComponent:@"save-transfer"];
    NSDictionary *game = game_identity();
    if (!game || !S2A_SaveTransfer || !S2A_SaveTransferFree) {
        if (self.phase == S2A_SAVE_CHECKING) { [self blockRecovery:S2A_SAVE_EV_NO_SERVICE reason:S2AL(@"save.noService.recover")]; return; }
        self.status.text = S2AL(@"save.noService");
        [self updateButtons]; return;
    }
    self.game = game;
    [self request:@{ @"operation": @"recover" } completion:^(NSDictionary *result) {
        self.phase = s2a_save_next(self.phase, S2A_SAVE_EV_RECOVER_OK); [self refresh];
    }];
}
- (void)updateButtons {
    struct s2a_save_buttons b = s2a_save_buttons(self.phase, self.recoveryOverridden, self.game != nil, self.hasSave, self.busy);
    self.importButton.enabled = b.import; self.exportButton.enabled = b.export_;
    self.backupButton.enabled = !self.busy && self.backupPath.length;
    self.startButton.enabled = b.start; self.overrideButton.hidden = !b.override;
    if (self.busy) [self.spinner startAnimating]; else [self.spinner stopAnimating];
}
// 恢复失败：说清楚数据在哪、怎么拷走，并给「仍要开始游戏」（单独确认）。事务记录原样保留，下次打开照常先试恢复（新版本可能恢复得了）
- (void)blockRecovery:(enum s2a_save_event)event reason:(NSString *)reason {
    self.phase = s2a_save_next(self.phase, event); self.recoveryOverridden = NO; self.preview = nil;
    [self showError:[NSString stringWithFormat:S2AL(@"save.blocked"), reason, NSBundle.mainBundle.infoDictionary[@"CFBundleDisplayName"] ?: @"S2Arm"]];
}
- (void)confirmOverride {
    // 读不到存档位置（审计 18:00 P2-14）与恢复失败共用这个确认，但不冒充「有没做完的事务」：各说各的
    BOOL noRoot = self.phase == S2A_SAVE_NO_ROOT;
    UIAlertController *alert = [UIAlertController alertControllerWithTitle:S2AL(@"save.override.title") message:noRoot ? S2AL(@"save.override.noRoot") : S2AL(@"save.override.message") preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:S2AL(@"common.cancel") style:UIAlertActionStyleCancel handler:nil]];
    [alert addAction:[UIAlertAction actionWithTitle:noRoot ? S2AL(@"save.start") : S2AL(@"save.override.confirm") style:UIAlertActionStyleDestructive handler:^(UIAlertAction *action) {
        (void)action; self.recoveryOverridden = YES;
        self.status.text = noRoot ? S2AL(@"save.override.doneNoRoot") : S2AL(@"save.override.done"); [self updateButtons];
    }]];
    [self presentViewController:alert animated:YES completion:nil];
}
// 读屏播报分成功 / 出错（审计 P2-12：原先成功也走 showError，读屏把「备份已导出」听成和失败一样的播报）：出错的播报前加「出错：」
- (void)showError:(NSString *)message {
    self.status.text = message; [self updateButtons];
    UIAccessibilityPostNotification(UIAccessibilityAnnouncementNotification, [S2AL(@"save.errorPrefix") stringByAppendingString:message]);
}
- (void)showStatus:(NSString *)message {
    self.status.text = message; [self updateButtons];
    UIAccessibilityPostNotification(UIAccessibilityAnnouncementNotification, message);
}
// 成功 / 取消的结果：临时副本清理失败（warning）才算出错
- (void)showResult:(NSString *)message warning:(NSString *)warning {
    if (warning) [self showError:[NSString stringWithFormat:@"%@\n%@", message, warning]];
    else [self showStatus:message];
}
- (NSString *)removeTemporaryFile:(NSString *)path {
    if (!path.length) return nil;
    NSError *error = nil;
    if ([NSFileManager.defaultManager removeItemAtPath:path error:&error] ||
        ([error.domain isEqualToString:NSCocoaErrorDomain] && error.code == NSFileNoSuchFileError)) return nil;
    return [NSString stringWithFormat:S2AL(@"save.tempCleanupFailed"), path.lastPathComponent];
}
- (NSString *)clearImport {
    NSString *warning = [self removeTemporaryFile:self.importPath];
    if (!warning) self.importPath = nil;
    return warning;
}
- (NSString *)clearExport {
    NSString *warning = [self removeTemporaryFile:self.temporaryExportPath];
    if (!warning) self.temporaryExportPath = nil;
    return warning;
}
- (void)request:(NSDictionary *)operation completion:(void (^)(NSDictionary *))completion {
    // 上一项还没做完：说一声再返回（审计 P2-23：原先静默丢掉，玩家只看到原来那句「正在处理」）。按钮忙时本就禁用，走到这里的是界面外的入口
    if (self.busy) { [self showStatus:S2AL(@"save.busy")]; return; }
    self.busy = YES; self.status.text = S2AL(@"save.processing"); [self updateButtons];
    NSMutableDictionary *request = [operation mutableCopy];
    request[@"game"] = self.game; request[@"dataRoot"] = self.dataRoot; request[@"workRoot"] = self.workRoot; request[@"language"] = save_language();
    dispatch_async(self.worker, ^{
        @autoreleasepool {
            NSError *error = nil;
            NSData *json = [NSJSONSerialization dataWithJSONObject:request options:0 error:&error];
            NSDictionary *response = nil;
            if (json) {
                NSString *text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
                char *raw = S2A_SaveTransfer(text.UTF8String);
                if (raw) {
                    NSData *bytes = [[NSString stringWithUTF8String:raw] dataUsingEncoding:NSUTF8StringEncoding];
                    S2A_SaveTransferFree(raw);
                    if (bytes) response = [NSJSONSerialization JSONObjectWithData:bytes options:0 error:&error];
                }
            }
            dispatch_async(dispatch_get_main_queue(), ^{
                self.busy = NO;
                if (![response isKindOfClass:NSDictionary.class] || ![response[@"ok"] boolValue]) {
                    NSString *message = [response[@"error"] isKindOfClass:NSDictionary.class] ? response[@"error"][@"message"] : nil;
                    NSString *cleanup = nil;
                    if ([operation[@"operation"] isEqual:@"inspect"] || [operation[@"operation"] isEqual:@"restore"]) cleanup = [self clearImport];
                    message = message ?: S2AL(@"save.serviceInvalid");
                    if (cleanup) message = [NSString stringWithFormat:@"%@\n%@", message, cleanup];
                    // 恢复失败、或还原失败后留下了没做完的事务：不能只把按钮按灰就返回。还原失败但没留事务（已回滚）照常可开始
                    if ([operation[@"operation"] isEqual:@"recover"])
                        [self blockRecovery:S2A_SAVE_EV_RECOVER_FAIL reason:g_journal_error ? [NSString stringWithFormat:S2AL(@"save.journalUnreadable"), g_journal_error, message] : message];
                    else if ([operation[@"operation"] isEqual:@"restore"] && recovery_pending()) [self blockRecovery:S2A_SAVE_EV_RESTORE_FAIL_TX reason:message];
                    else [self showError:message];
                    return;
                }
                completion(response[@"result"]); [self updateButtons];
            });
        }
    });
}
- (void)refresh {
    [self request:@{ @"operation": @"status" } completion:^(NSDictionary *result) {
        self.hasSave = [result[@"hasSave"] boolValue];
        self.backupPath = [result[@"previousBackup"] isKindOfClass:NSString.class] ? result[@"previousBackup"] : nil;
        self.status.text = self.hasSave ? S2AL(@"save.status.hasSave") : S2AL(@"save.status.noSave");
    }];
}
- (void)start {
    if (!s2a_save_buttons(self.phase, self.recoveryOverridden, self.game != nil, self.hasSave, self.busy).start) return;
    // 玩家确认过「仍要开始」就不再先跑恢复（它刚失败过，失败分支不会走到 finishManagement）
    if (self.game && !self.recoveryOverridden) {
        [self request:@{ @"operation": @"recover" } completion:^(NSDictionary *result) {
            (void)result; [self finishManagement];
        }];
    } else [self finishManagement];
}
- (void)finishManagement {
    [NSUserDefaults.standardUserDefaults setBool:NO forKey:ManageKey];
    [NSUserDefaults.standardUserDefaults synchronize];
    self.startGame();
}
- (void)presentExport:(NSString *)path {
    UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForExportingURLs:@[[NSURL fileURLWithPath:path]] asCopy:YES];
    picker.directoryURL = [NSFileManager.defaultManager URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask].firstObject;
    picker.delegate = self; self.exportPicker = picker;
    [self presentViewController:picker animated:YES completion:nil];
}
- (void)exportSave {
    NSString *warning = [self clearExport];
    if (warning) { [self showError:warning]; return; }
    NSString *name = [NSString stringWithFormat:@"%@-%@.s2asave", self.game[@"id"], NSUUID.UUID.UUIDString];
    NSString *path = [[[self.dataRoot stringByDeletingLastPathComponent] stringByAppendingPathComponent:@"save-exports"] stringByAppendingPathComponent:name];
    NSError *error = nil;
    if (![NSFileManager.defaultManager createDirectoryAtPath:[path stringByDeletingLastPathComponent] withIntermediateDirectories:YES attributes:nil error:&error]) { [self showError:S2AL(@"save.exportDirFailed")]; return; }
    [self request:@{ @"operation": @"export", @"path": path } completion:^(NSDictionary *result) {
        self.temporaryExportPath = result[@"outputPath"];
        self.status.text = S2AL(@"save.exportReady"); [self presentExport:result[@"outputPath"]];
    }];
}
- (void)exportBackup { if (self.backupPath.length) [self presentExport:self.backupPath]; }
- (void)importSave {
    NSString *warning = [self clearImport];
    if (warning) { [self showError:warning]; return; }
    UIDocumentPickerViewController *picker = [[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeData] asCopy:YES];
    picker.directoryURL = [NSFileManager.defaultManager URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask].firstObject;
    picker.allowsMultipleSelection = NO; picker.delegate = self;
    [self presentViewController:picker animated:YES completion:nil];
}
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    if (controller == self.exportPicker) {
        self.exportPicker = nil;
        // A destination chosen inside our export folder becomes the user's file.
        if (self.temporaryExportPath.length &&
            [urls.firstObject.URLByResolvingSymlinksInPath isEqual:[NSURL fileURLWithPath:self.temporaryExportPath].URLByResolvingSymlinksInPath]) self.temporaryExportPath = nil;
        NSString *warning = [self clearExport];
        [self showResult:S2AL(@"save.exported") warning:warning];
        return;
    }
    NSURL *url = urls.firstObject; if (!url || self.busy) return;
    self.busy = YES; self.status.text = S2AL(@"save.reading"); [self updateButtons];
    dispatch_async(self.worker, ^{
        BOOL scoped = [url startAccessingSecurityScopedResource];
        NSString *folder = [self.workRoot stringByAppendingPathComponent:@"imports"];
        NSString *path = [folder stringByAppendingPathComponent:[NSUUID.UUID.UUIDString stringByAppendingPathExtension:@"s2asave"]];
        NSError *error = nil;
        BOOL copied = [NSFileManager.defaultManager createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:&error] &&
            [NSFileManager.defaultManager copyItemAtURL:url toURL:[NSURL fileURLWithPath:path] error:&error];
        if (scoped) [url stopAccessingSecurityScopedResource];
        dispatch_async(dispatch_get_main_queue(), ^{
            self.busy = NO;
            self.importPath = path;
            if (!copied) {
                NSString *warning = [self clearImport];
                [self showError:warning ? [NSString stringWithFormat:S2AL(@"save.readFailed.detail"), warning] : S2AL(@"save.readFailed")];
                return;
            }
            [self request:@{ @"operation": @"inspect", @"path": path } completion:^(NSDictionary *result) {
                self.preview = result; [self confirmRestore];
            }];
        });
    });
}
- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller {
    if (controller != self.exportPicker) { self.status.text = S2AL(@"save.pickCancelled"); return; }
    self.exportPicker = nil;
    NSString *warning = [self clearExport];
    [self showResult:S2AL(@"save.exportCancelled") warning:warning];
}
- (void)confirmRestore {
    NSDictionary *manifest = self.preview[@"manifest"];
    NSString *users = [manifest[@"users"][@"ids"] componentsJoinedByString:@"\n"];
    NSString *size = [NSByteCountFormatter stringFromByteCount:[self.preview[@"totalBytes"] longLongValue] countStyle:NSByteCountFormatterCountStyleFile];
    NSString *message = [NSString stringWithFormat:S2AL(@"save.restore.message"), manifest[@"game"][@"name"], manifest[@"game"][@"version"], manifest[@"createdAt"], users.length ? users : S2AL(@"save.deviceSave"), self.preview[@"fileCount"], size];
    self.status.text = S2AL(@"save.inspected");
    UIAlertController *alert = [UIAlertController alertControllerWithTitle:S2AL(@"save.restore.title") message:message preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:S2AL(@"common.cancel") style:UIAlertActionStyleCancel handler:^(UIAlertAction *action) {
        (void)action; self.preview = nil;
        NSString *warning = [self clearImport];
        [self showResult:S2AL(@"save.restoreCancelled") warning:warning];
    }]];
    [alert addAction:[UIAlertAction actionWithTitle:S2AL(@"save.restore.confirm") style:UIAlertActionStyleDestructive handler:^(UIAlertAction *action) {
        (void)action;
        [self request:@{ @"operation": @"restore", @"path": self.importPath, @"archiveSha256": self.preview[@"archiveSha256"], @"targetFingerprint": self.preview[@"targetFingerprint"] } completion:^(NSDictionary *result) {
            self.preview = nil; self.phase = s2a_save_next(self.phase, S2A_SAVE_EV_RESTORE_OK);
            self.backupPath = [result[@"previousBackup"] isKindOfClass:NSString.class] ? result[@"previousBackup"] : nil;
            self.hasSave = YES;
            NSString *warning = [self clearImport];
            NSString *message = S2AL(@"save.restored");
            [self showResult:message warning:warning];
        }];
    }]];
    [self presentViewController:alert animated:YES completion:nil];
}
@end

UIViewController *s2a_ios_save_manager(void (^start_game)(void)) {
    S2ASaveManager *manager = [S2ASaveManager new]; manager.startGame = start_game; return manager;
}
void s2a_ios_save_present(UIViewController *presenter) {
    if (!presenter || presenter.presentedViewController) return;
    UIAlertController *alert = [UIAlertController alertControllerWithTitle:S2AL(@"save.quit.title") message:S2AL(@"save.quit.message") preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:S2AL(@"save.quit.back") style:UIAlertActionStyleCancel handler:nil]];
    [alert addAction:[UIAlertAction actionWithTitle:S2AL(@"save.quit.confirm") style:UIAlertActionStyleDestructive handler:^(UIAlertAction *item) {
        (void)item; [NSUserDefaults.standardUserDefaults setBool:YES forKey:ManageKey];
        [NSUserDefaults.standardUserDefaults synchronize];
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{ s2a_host_shutdown("打开存档管理"); });
    }]];
    [presenter presentViewController:alert animated:YES completion:nil];
}
