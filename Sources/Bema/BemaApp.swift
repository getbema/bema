import AppKit
import Carbon.HIToolbox
import SwiftUI

@main
struct BemaApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @ObservedObject private var model = PresenterModel.shared
    @AppStorage("hidePresenterFromSharing") private var hidePresenterFromSharing = true
    @ObservedObject private var coach = CoachModel.shared
    @ObservedObject private var updates = Updates.shared
    @ObservedObject private var loginItem = LoginItem.shared
    @AppStorage("theme") private var themeName = Theme.Name.backstage.rawValue
    @AppStorage("showMenuBar") private var showMenuBar = true
    @ObservedObject private var voice = VoiceCommands.shared
    @ObservedObject private var captions = Captions.shared

    init() {
        // Debug helper: `Bema --dump-notes deck.pptx` prints titles and notes, then exits.
        let args = CommandLine.arguments
        // Started by Claude Desktop (or another MCP app): relay to the running Bema, never open windows.
        if args.contains("--mcp") { MCPBridge.run() }
        if let i = args.firstIndex(of: "--dump-notes"), i + 1 < args.count {
            do {
                for s in try PPTXReader.read(url: URL(fileURLWithPath: args[i + 1])) {
                    print("[\(s.id)\(s.hidden ? " hidden" : "")] \(s.title.replacingOccurrences(of: "\n", with: " / "))")
                    if !s.notes.isEmpty { print("    " + s.notes.replacingOccurrences(of: "\n", with: "\n    ")) }
                }
                exit(0)
            } catch {
                FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
                exit(1)
            }
        }
    }

    var body: some Scene {
        Window("Bema", id: "main") {
            RootView()
                .environmentObject(model)
                .frame(minWidth: 1320, minHeight: 640)
        }
        .defaultSize(width: 1360, height: 840)
        .windowStyle(.hiddenTitleBar)
        .commands {
            CommandGroup(after: .appInfo) {
                if updates.isAvailable {
                    Button("Check for Updates…") { updates.checkForUpdates() }
                        .disabled(!updates.canCheck)
                    Toggle("Check for Updates Automatically", isOn: Binding(
                        get: { updates.automaticallyChecks },
                        set: { updates.automaticallyChecks = $0 }))
                }
            }
            CommandGroup(replacing: .newItem) {
                Button("Open Presentation…") { model.openPanel() }
                    .keyboardShortcut("o")
                Button("Present Deck Open in PowerPoint or Keynote") { model.presentOpenDeck() }
                    .keyboardShortcut("p", modifiers: [.command, .shift])
                Divider()
                Button("Add Bema Button to PowerPoint…") { model.installPowerPointButton() }
                Button("Remove Bema Button from PowerPoint") { model.removePowerPointButton() }
                Menu("Open Recent") {
                    ForEach(model.recents, id: \.self) { url in
                        Button(url.lastPathComponent) { model.load(url) }
                    }
                }
                .disabled(model.recents.isEmpty)
                Divider()
                Button("Close Presentation") { model.closeDeck() }
                    .keyboardShortcut("w", modifiers: [.command, .shift])
                    .disabled(model.deckURL == nil)
            }
            CommandMenu("Slideshow") {
                Button("Start Slideshow…") { model.requestStart() }
                    .keyboardShortcut(.return, modifiers: .command)
                    .disabled(!model.canStart)
                Button("End Slideshow") { model.endShowButton() }
                    .keyboardShortcut(".", modifiers: .command)
                    .disabled(model.phase != .presenting && !model.steppedOut)
                Button(model.steppedOut ? "Back to Slides" : "Show Something Else") {
                    if model.steppedOut { model.backToSlides() } else { model.stepOut() }
                }
                .keyboardShortcut("s", modifiers: [.command, .shift])
                .disabled(model.phase != .presenting && !model.steppedOut)
                Divider()
                Button("Next Slide") { model.next() }
                    .keyboardShortcut(.rightArrow, modifiers: .command)
                Button("Previous Slide") { model.previous() }
                    .keyboardShortcut(.leftArrow, modifiers: .command)
                Divider()
                Button("Black Screen  (B)") { model.toggleBlack() }
                    .disabled(model.phase != .presenting)
                Button("White Screen  (W)") { model.toggleWhite() }
                    .disabled(model.phase != .presenting)
                Divider()
                Button("Start / Pause Timer") { model.toggleTimer() }
                    .keyboardShortcut("t", modifiers: [.command, .option])
                Button("Reset Timer") { model.resetTimer() }
                    .keyboardShortcut("r", modifiers: [.command, .option])
                Divider()
                Button("Stage Tools…") { NSApp.activate(); model.stageToolsOpen = true }
                    .keyboardShortcut("t", modifiers: [.command, .shift])
                Button("Show or Hide Self View (Hidden from Screen Sharing)") {
                    SelfViewWindowRef.toggle()
                }
                .keyboardShortcut("v", modifiers: [.command, .option])
                Divider()
                Toggle("Rehearsal Mode (Report When the Show Ends)", isOn: $coach.rehearsalMode)
                Toggle("Speech Coach (Speaking Speed and Filler Words)", isOn: $coach.listen)
                Toggle("Cue a Minute Before Time Is Up", isOn: $coach.timeCue)
                Toggle("Play a Soft Sound with the Cue", isOn: $coach.cueSound)
                    .disabled(!coach.timeCue)
                Button("Show Last Talk Report") { coach.showingReport = true }
                    .keyboardShortcut("r", modifiers: [.command, .shift])
                    .disabled(coach.lastReport == nil)
                Button("Use Automatic Slide Budgets") { coach.resetBudgets() }
                    .disabled(!coach.usingRehearsalBudgets)
                Toggle("Voice Commands (“Next Slide”, “Go to Slide 5”)", isOn: $voice.enabled)
                Toggle("Live Captions on the Audience Screen", isOn: $captions.enabled)
                Divider()
                Button("Start / Pause Prompter") { PrompterModel.shared.toggle() }
                    .keyboardShortcut("p", modifiers: [.command, .option])
                Button("Prompter for Your Own Text…") {
                    PrompterModel.shared.editingOwnScript = true
                    NSApp.activate()
                    MainWindowRef.opener?(id: "prompter")
                }
                Divider()
                Toggle("Keep Presenter Window on Top", isOn: $model.alwaysOnTop)
                Toggle("Hide Presenter Window from Screen Sharing", isOn: $hidePresenterFromSharing)
            }
            CommandGroup(replacing: .help) {
                Button("Bema Help") { HelpLinks.open("help") }
                    .keyboardShortcut("?", modifiers: .command)
                Button("Keyboard Shortcuts") { HelpLinks.open("help#presenting") }
                Button("Troubleshooting") { HelpLinks.open("help#troubleshooting") }
                Button("The PowerPoint Button") { HelpLinks.open("help#powerpoint-button") }
                Divider()
                Button("Docs and bema:// Links") { HelpLinks.open("docs") }
                Button("Report a Problem…") { NSWorkspace.shared.open(HelpLinks.issues) }
                Button("What's New") { NSWorkspace.shared.open(HelpLinks.releases) }
            }
            CommandGroup(after: .toolbar) {
                Picker("Theme", selection: $themeName) {
                    ForEach(Theme.Name.allCases) { Text($0.label).tag($0.rawValue) }
                }
                Toggle("Show Bema in the Menu Bar", isOn: $showMenuBar)
                Toggle("Open Bema at Login", isOn: Binding(get: { loginItem.enabled }, set: { loginItem.set($0) }))
                Divider()
                Button("Write Notes for This Slide") { model.writeNotesForCurrentSlide() }
                    .disabled(model.slides.isEmpty || model.noteWriting != nil)
                Button("Write Notes for Slides Without Notes") { model.writeNotesForAllSlides() }
                    .disabled(model.slidesNeedingNotes.isEmpty || model.noteWriting != nil)
                Button("Save Draft Notes to Deck") { model.saveDraftsToDeck() }
                    .disabled(model.drafts.isEmpty)
                Button("Notes Writer Settings…") { NSApp.activate(); model.noteWriterSettingsOpen = true }
                Divider()
                Button("Bigger Notes") { NotesFont.change(by: 2) }
                    .keyboardShortcut("+")
                Button("Smaller Notes") { NotesFont.change(by: -2) }
                    .keyboardShortcut("-")
                Divider()
            }
        }

        Window("Prompter", id: "prompter") {
            PrompterWindow().id(themeName)
        }
        .defaultSize(width: 760, height: 260)
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentMinSize)

        Window("Self View", id: "selfview") {
            SelfViewWindow()
        }
        .defaultSize(width: 320, height: 240)
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentMinSize)

        MenuBarExtra(isInserted: $showMenuBar) {
            MenuBarPanel().environmentObject(model).id(themeName)
        } label: {
            Image(nsImage: MenuBarIcon.image)
        }
        .menuBarExtraStyle(.window)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var keyMonitor: Any?
    private var presentHotKey: HotKey?
    private let services = ServiceProvider()
    /// Another Bema was already running when this one started: hand everything to it and quit.
    private var defersTo: SingleInstance.Instance?
    private var dockObserver: Any?
    private var windowCloseObserver: Any?

    func applicationWillFinishLaunching(_ notification: Notification) {
        defersTo = MainActor.assumeIsolated { SingleInstance.current() }
        // Never show a second window or menu bar icon.
        if defersTo != nil { NSApp.setActivationPolicy(.prohibited) }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let other = defersTo {
            // Decks and links arrive before this; give them a moment to be handed over.
            SingleInstance.activate(other)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { NSApp.terminate(nil) }
            return
        }
        NSApp.servicesProvider = services
        NSUpdateDynamicServices()
        // Control-Option-P anywhere: hand the deck open in PowerPoint to Bema.
        presentHotKey = HotKey(keyCode: kVK_ANSI_P, modifiers: controlKey | optionKey) {
            Task { @MainActor in PresenterModel.shared.presentOpenDeck() }
        }
        Task { @MainActor in await PresenterModel.shared.refreshOpenInPowerPoint() }
        Task { @MainActor in BemaMCP.shared.startIfEnabled() }
        // Right-clicking the menu bar icon opens its panel too.
        Task { @MainActor in MenuBarRightClick.install() }
        // The PowerPoint button opens a dialog Bema watches for (no "open Bema.app?" prompt).
        Task { @MainActor in PowerPointButtonWatcher.shared.update() }
        // In the Dock only while the presenter window is open; otherwise just the menu bar icon.
        dockObserver = NotificationCenter.default.addObserver(forName: NSApplication.didUpdateNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { DockIcon.update() }
        }
        // Closing the presenter window clears the timer outside a show.
        windowCloseObserver = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { note in
            let isPresenter = (note.object as? NSWindow)?.identifier?.rawValue.hasPrefix("main") == true
            MainActor.assumeIsolated { if isPresenter { PresenterModel.shared.presenterWindowClosed() } }
        }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let handled = MainActor.assumeIsolated { PresenterModel.shared.handleKey(event) }
            return handled ? nil : event
        }
    }

    /// Finder "Open With", dragging onto the Dock icon, and `open -a Bema file.pptx`.
    func application(_ application: NSApplication, open urls: [URL]) {
        if let other = defersTo { SingleInstance.forward(urls, to: other); return }
        guard let url = urls.first else { return }
        Task { @MainActor in
            if url.scheme?.lowercased() == "bema" {
                Self.handleLink(url)
            } else {
                PresenterModel.shared.load(url)
            }
        }
    }

    /// bema://prompter?place=camera         → open the detached prompter under the camera
    /// bema://prompter?text=Hello%20there    → prompt that text (bare ?text opens the box to paste it)
    /// bema://present                       → present the deck open in PowerPoint
    /// bema://present?path=/Users/…/deck.pptx → present that file
    /// bema://present?url=file:///…           → same, as a file URL (what Office add-ins report)
    /// bema://present?url=https://…           → a deck PowerPoint opened from OneDrive or SharePoint
    @MainActor
    static func handleLink(_ link: URL) {
        let model = PresenterModel.shared
        NSApp.activate()
        let items = URLComponents(url: link, resolvingAgainstBaseURL: false)?.queryItems ?? []
        if link.host?.lowercased() == "selfview" {
            SelfViewWindowRef.open()
            return
        }
        if link.host?.lowercased() == "prompter" {
            // bema://prompter[?place=camera] opens the detached prompter.
            // …&text=Hello prompts that text; a bare ?text opens the box to paste it.
            if let text = items.first(where: { $0.name == "text" }) {
                if let value = text.value, !value.isEmpty { PrompterModel.shared.useOwnScript(value) }
                else { PrompterModel.shared.editingOwnScript = true }
            }
            NotificationCenter.default.post(name: .openPrompterWindow, object: nil,
                                            userInfo: ["place": items.first(where: { $0.name == "place" })?.value ?? ""])
            return
        }
        if let why = items.first(where: { $0.name == "why" })?.value {
            PowerPointButtonWatcher.log.notice("PowerPoint button fell back to a link: \(String(why.prefix(200)), privacy: .public)")
        }
        var file: URL?
        if let path = items.first(where: { $0.name == "path" })?.value, path.hasPrefix("/") {
            file = URL(fileURLWithPath: path)
        } else if let raw = items.first(where: { $0.name == "url" })?.value, let u = URL(string: raw), u.isFileURL {
            file = u
        }
        if let raw = items.first(where: { $0.name == "url" })?.value, let cloud = URL(string: raw), CloudDeck.isCloud(cloud) {
            if cloud != model.deckURL || model.phase == .empty { model.load(cloud) }
        } else if let file, FileManager.default.fileExists(atPath: file.path) {
            if file.standardizedFileURL != model.deckURL?.standardizedFileURL || model.phase == .empty {
                model.load(file)
            }
        } else {
            model.presentOpenDeck()
        }
    }

    /// Clicking Bema in Finder or Launchpad while it runs only in the menu bar opens its window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        MainActor.assumeIsolated {
            if !DockIcon.presenterWindowOpen { PresenterModel.shared.showPresenterWindow() }
        }
        return true
    }

    /// With the menu bar icon showing, Bema keeps running after its window closes.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        !(UserDefaults.standard.object(forKey: "showMenuBar") as? Bool ?? true)
    }

    /// Right-click the Dock icon: "Present <deck open in PowerPoint>".
    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let menu = NSMenu()
        let url = MainActor.assumeIsolated { PresenterModel.shared.openInPowerPoint }
        let title = url.map { "Present “\($0.deletingPathExtension().lastPathComponent)”" } ?? "Present Deck Open in PowerPoint or Keynote"
        let item = NSMenuItem(title: title, action: #selector(presentOpenDeckFromDock), keyEquivalent: "")
        item.target = self
        menu.addItem(item)
        return menu
    }

    @objc private func presentOpenDeckFromDock() {
        Task { @MainActor in PresenterModel.shared.presentOpenDeck() }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let model = MainActor.assumeIsolated { PresenterModel.shared }
        guard MainActor.assumeIsolated({ model.phase == .presenting }) else { return .terminateNow }
        Task { @MainActor in
            await model.endShow()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

/// Finder right-click → Quick Actions / Services → "Present with Bema".
final class ServiceProvider: NSObject {
    @objc func presentDeck(_ pboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>) {
        let urls = pboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]
        guard let url = urls?.first else {
            error.pointee = "No presentation was selected." as NSString
            return
        }
        Task { @MainActor in
            NSApp.activate()
            PresenterModel.shared.load(url)
        }
    }
}

extension Notification.Name {
    static let openPrompterWindow = Notification.Name("BemaOpenPrompterWindow")
    static let openSelfView = Notification.Name("BemaOpenSelfView")
}

/// Bema's help lives on getbema.dev, so it's always current and readable outside the app.
enum HelpLinks {
    static let site = URL(string: "https://getbema.dev/")!
    static let issues = URL(string: "https://github.com/getbema/bema/issues/new/choose")!
    static let releases = URL(string: "https://github.com/getbema/bema/releases")!

    static func open(_ path: String) {
        NSWorkspace.shared.open(URL(string: path, relativeTo: site)!.absoluteURL)
    }
}

enum NotesFont {
    static let key = "notesFontSize"
    static let range: ClosedRange<Double> = 12...72

    static func change(by delta: Double) {
        let current = UserDefaults.standard.object(forKey: key) as? Double ?? 26
        UserDefaults.standard.set(min(max(current + delta, range.lowerBound), range.upperBound), forKey: key)
    }
}
