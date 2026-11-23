import AppKit
import CryptoKit
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class PresenterModel: ObservableObject {
    static let shared = PresenterModel()

    enum Phase: Equatable {
        case empty
        case loading(String)
        case ready        // deck loaded, slideshow not running (you can still browse notes)
        case presenting   // PowerPoint slideshow is running and we follow it
        case failed(String)
    }

    enum AudienceScreen: Equatable { case black, white, normal }

    @Published private(set) var phase: Phase = .empty {
        didSet {
            if oldValue != .presenting && phase == .presenting { CoachModel.shared.showStarted() }
            if oldValue == .presenting && phase != .presenting { CoachModel.shared.showEnded() }
            if (oldValue == .presenting) != (phase == .presenting) {
                VoiceCommands.shared.showChanged()
                Captions.shared.showChanged()
            }
        }
    }
    @Published private(set) var deckURL: URL?
    /// The slides with any draft notes filled in: what the notes panel, prompter and remote show.
    @Published private(set) var slides: [SlideInfo] = []
    /// The slides as the deck has them.
    var deckSlides: [SlideInfo] = [] { didSet { slides = DraftNotes.merge(deckSlides, drafts) } }
    /// Notes Bema wrote for slides that had none, by slide number. Kept per deck until saved or discarded.
    @Published var drafts: [Int: String] = [:] { didSet { slides = DraftNotes.merge(deckSlides, drafts) } }
    /// Writing notes now: which slide, how far through a batch.
    @Published var noteWriting: NoteWritingProgress?
    /// The notes writer settings sheet.
    @Published var noteWriterSettingsOpen = false
    var noteWritingTask: Task<Void, Never>?
    @Published private(set) var current: Int = 1 {
        didSet {
            if oldValue != current {
                clearDrawings()
                PrompterModel.shared.slideChanged(to: current)
                CoachModel.shared.slideChanged(from: oldValue, to: current)
            }
        }
    }
    @Published private(set) var pdf: PDFDocument?
    @Published private(set) var audience: AudienceScreen = .normal
    @Published private(set) var showEnded = false
    @Published var powerPointReady = false
    @Published var notice: String?
    @Published var showAccessibilityHelp = false
    /// The Stage tools panel (phone remote, poll, camera bubble, listening, rehearsal).
    @Published var stageToolsOpen = false

    // Laser pointer and drawing
    @Published var tool: PointerTool = .none {
        didSet { if !tool.followsHover { laserPoint = nil } }
    }
    @Published var laserPoint: CGPoint?
    @Published var strokes: [Stroke] = []
    @Published var activeStroke: Stroke?
    @Published var stamps: [Stamp] = []
    @Published private(set) var ripples: [Ripple] = []
    @Published private(set) var laserTrail: [TrailPoint] = []
    @Published private(set) var reaction: Reaction?
    @Published var laserStyle: LaserStyle = LaserStyle(rawValue: UserDefaults.standard.string(forKey: "laserStyle") ?? "") ?? .dot {
        didSet { UserDefaults.standard.set(laserStyle.rawValue, forKey: "laserStyle") }
    }
    @Published var stampKind: StampKind = StampKind(rawValue: UserDefaults.standard.string(forKey: "stampKind") ?? "") ?? .check {
        didSet { UserDefaults.standard.set(stampKind.rawValue, forKey: "stampKind") }
    }
    private var effectsTimer: Timer?
    /// Where PowerPoint is drawing the slideshow, in screen coordinates.
    @Published private(set) var showFrame: CGRect?
    private let overlay = AudienceOverlay()
    /// Ask which display the slides go on each time the show is started from Bema (with two or more displays).
    @Published var askForDisplay: Bool = UserDefaults.standard.object(forKey: "askForDisplay") as? Bool ?? true {
        didSet { UserDefaults.standard.set(askForDisplay, forKey: "askForDisplay") }
    }
    /// The "Where should the slides go?" sheet.
    @Published var displayChooserOpen = false
    @Published var audienceScreenName: String = UserDefaults.standard.string(forKey: "audienceScreen") ?? "" {
        didSet { UserDefaults.standard.set(audienceScreenName, forKey: "audienceScreen") }
    }
    @Published var alwaysOnTop = false { didSet { applyWindowLevel() } }
    @Published private(set) var recents: [URL] = (UserDefaults.standard.stringArray(forKey: "recents") ?? [])
        .map { URL(fileURLWithPath: $0) }
        .filter { FileManager.default.fileExists(atPath: $0.path) }

    /// Planned length of the talk in minutes (0 = no target). The timer shows progress against it.
    @Published var targetMinutes: Int = UserDefaults.standard.object(forKey: "targetMinutes") as? Int ?? 20 {
        didSet { UserDefaults.standard.set(targetMinutes, forKey: "targetMinutes") }
    }
    /// The display the running show is on, and whether it's true full screen there.
    @Published private(set) var presentingOn: String = ""
    @Published private(set) var audienceFullScreen = false
    @Published private(set) var accessibilityGranted = PowerPointController.hasAccessibility
    /// The deck currently open in PowerPoint's front window (offered as "Present …").
    @Published private(set) var openInPowerPoint: URL?
    private var permissionTimer: Timer?

    // Timer
    @Published private(set) var timerStartedAt: Date?
    @Published private(set) var timerAccumulated: TimeInterval = 0

    /// PowerPoint or Keynote, chosen by the deck's file type.
    private(set) var app: SlideshowController = PowerPointController()
    /// The app that has the deck offered on the welcome card ("Open in Keynote").
    @Published private(set) var openInAppName = "PowerPoint"

    init() {
        // Keep the pre-flight check current while the person grants Accessibility in System Settings.
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let granted = PowerPointController.hasAccessibility
                if granted != self.accessibilityGranted { self.accessibilityGranted = granted; PowerPointButtonWatcher.shared.update() }
                if granted && self.showAccessibilityHelp {
                    self.showAccessibilityHelp = false
                    self.notice = "Accessibility is allowed. End and restart the show to go full screen."
                }
                if self.phase != .presenting { Task { await self.refreshOpenInPowerPoint() } }
            }
        }
        // A display plugged in or out: refresh what depends on the count and drop notices it made stale.
        screenObserver = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                                                object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.displaysChanged() }
        }
    }
    private var screenObserver: NSObjectProtocol?
    /// How many displays are connected, kept current so views that depend on it redraw.
    @Published private(set) var displayCount = NSScreen.screens.count

    private func displaysChanged() {
        displayCount = NSScreen.screens.count
        let rehearsing = phase == .presenting && !audienceFullScreen
        notice = AudienceDisplay.noticeAfterDisplaysChanged(current: notice, displayCount: displayCount, rehearsingInWindow: rehearsing)
    }
    private var pollTask: Task<Void, Never>?
    /// Slide pictures by slide and size. Evicts the least-used ones past about 400 MB, rather than
    /// dropping everything and redrawing every visible slide at once.
    private let imageCache: NSCache<NSString, NSImage> = {
        let c = NSCache<NSString, NSImage>()
        c.totalCostLimit = 400 * 1024 * 1024
        return c
    }()

    var deckName: String { deckURL?.deletingPathExtension().lastPathComponent ?? "Bema" }
    var currentSlide: SlideInfo? { slides.first { $0.id == current } }
    var nextSlide: SlideInfo? { slides.first { $0.id > current && !$0.hidden } }
    var visibleCount: Int { slides.filter { !$0.hidden }.count }
    var visiblePosition: Int { slides.filter { !$0.hidden && $0.id <= current }.count }

    static let deckTypes: [UTType] = [
        "org.openxmlformats.presentationml.presentation",
        "org.openxmlformats.presentationml.slideshow",
        "com.microsoft.powerpoint.openxml.presentation.macroenabled",
        "com.apple.keynote.key",
    ].compactMap { UTType($0) }

    // MARK: - Loading

    func openPanel() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = Self.deckTypes
        panel.allowsMultipleSelection = false
        panel.message = "Choose a PowerPoint or Keynote presentation"
        if panel.runModal() == .OK, let url = panel.url { load(url) }
    }

    func load(_ url: URL) {
        Task { await loadDeck(url) }
    }

    private func loadDeck(_ url: URL) async {
        if phase == .presenting { await endShow() }
        pollTask?.cancel()
        deckURL = url
        noteWritingTask?.cancel()
        drafts = [:]
        deckSlides = []
        pdf = nil
        imageCache.removeAllObjects()
        powerPointReady = false
        showEnded = false
        audience = .normal
        notice = nil
        resetTimer()
        addRecent(url)

        app = SlideshowApps.controller(for: url)
        // Keynote decks, PowerPoint decks opened from OneDrive, SharePoint or Teams (no file to read), and
        // files macOS keeps from Bema, such as an attachment PowerPoint opened from Outlook.
        var readsThroughApp = app is KeynoteController || CloudDeck.isCloud(url)

        if !readsThroughApp {
            phase = .loading("Reading speaker notes…")
            do {
                deckSlides = try await Task.detached { try PPTXReader.read(url: url) }.value
                slidesLoaded(url)
            } catch PPTXError.blocked where app is PowerPointController {
                readsThroughApp = true
            } catch {
                phase = .failed(error.localizedDescription)
                return
            }
        }

        phase = .loading("Opening in \(app.appName)…")
        do {
            try await app.open(url)
            powerPointReady = true
        } catch {
            if readsThroughApp {
                phase = .failed("\(error.localizedDescription)")
                return
            }
            notice = "\(error.localizedDescription) You can still read your notes, but the slideshow can't start."
            phase = .ready
            return
        }

        if readsThroughApp {
            phase = .loading(app is KeynoteController ? "Reading presenter notes…" : "Reading speaker notes from PowerPoint…")
            do {
                deckSlides = try await app.readSlides() ?? []
            } catch {
                phase = .failed(error.localizedDescription)
                return
            }
            slidesLoaded(url)
        }

        let cached = Self.previewCacheURL(for: url)
        // A cloud deck has no modification date to notice edits by, so its previews are made fresh each time.
        if CloudDeck.isCloud(url) || !FileManager.default.fileExists(atPath: cached.path) {
            phase = .loading("Preparing slide previews…")
            do { try await app.exportPDF(to: cached) } catch {
                notice = "Slide previews aren't available: \(error.localizedDescription)"
            }
        }
        pdf = PDFDocument(url: cached)
        // Saved previews of another version of this deck: make them again from what PowerPoint has now.
        if !readsThroughApp, let saved = pdf,
           !PreviewCheck.matches(pageTexts: (0..<saved.pageCount).map { saved.page(at: $0)?.string ?? "" }, slides: deckSlides) {
            phase = .loading("Preparing slide previews…")
            try? FileManager.default.removeItem(at: cached)
            do { try await app.exportPDF(to: cached) } catch {
                notice = "Slide previews aren't available: \(error.localizedDescription)"
            }
            pdf = PDFDocument(url: cached)
        }
        phase = .ready
        // If this deck's slideshow is already running (e.g. Bema was restarted), pick it back up.
        if case .running(let slide, _, _, _, _) = (try? await app.status()) ?? .notRunning {
            if slide > 0 { current = slide }
            phase = .presenting
            startPolling()
        }
        // Opening the deck brings PowerPoint to the front; come back on top.
        showPresenterWindow()
    }

    private func slidesLoaded(_ url: URL) {
        drafts = DraftNotes.pruned(DraftNotes.load(for: url), for: deckSlides)
        current = slides.first { !$0.hidden }?.id ?? 1
        PrompterModel.shared.load(slides: slides)
        CoachModel.shared.deckLoaded(url)
        RemoteServer.shared.deckChanged()
    }

    /// Previews are cached per file path + modification date + size, so an edited deck is re-exported.
    private static func previewCacheURL(for url: URL) -> URL {
        let attrs = try? FileManager.default.attributesOfItem(atPath: url.path)
        let stamp = "\(CloudDeck.key(url))|\((attrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0)|\(attrs?[.size] ?? 0)"
        let hash = SHA256.hash(data: Data(stamp.utf8)).map { String(format: "%02x", $0) }.joined().prefix(24)
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        return caches.appendingPathComponent("Bema/\(hash).pdf")
    }

    private func addRecent(_ url: URL) {
        // Recents reopen files; a cloud deck can only be presented while PowerPoint has it open.
        guard url.isFileURL else { return }
        var list = recents.filter { $0.path != url.path }
        list.insert(url, at: 0)
        recents = Array(list.prefix(8))
        UserDefaults.standard.set(recents.map(\.path), forKey: "recents")
    }

    private let powerPointWatcher = PowerPointController()
    private let keynoteWatcher = KeynoteController()

    /// The deck in PowerPoint's or Keynote's front window (whichever was used last).
    func refreshOpenInPowerPoint() async {
        let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
        let keynoteFirst = front == KeynoteController.bundleID
            || (front != PowerPointController.bundleID && !SlideshowApps.isRunning(PowerPointController.bundleID))
        var found: (URL, String)?
        for watcher in (keynoteFirst ? [keynoteWatcher, powerPointWatcher] as [SlideshowController] : [powerPointWatcher, keynoteWatcher]) {
            if let url = await watcher.activePresentationURL() { found = (url, watcher.appName); break }
        }
        if found?.0 != openInPowerPoint { openInPowerPoint = found?.0 }
        if let name = found?.1, name != openInAppName { openInAppName = name }
    }

    /// Present the deck that's open in PowerPoint (the global shortcut, the Dock menu, the welcome card).
    func presentOpenDeck() {
        Task {
            await refreshOpenInPowerPoint()
            showPresenterWindow()
            guard let url = openInPowerPoint else {
                notice = PowerPointController.isRunning || SlideshowApps.isRunning(KeynoteController.bundleID)
                    ? "The deck in front hasn't been saved yet. Save it, on this Mac or to OneDrive, then try again."
                    : "Neither PowerPoint nor Keynote is running. Open a deck in one of them, or choose one here."
                return
            }
            if url.standardizedFileURL == deckURL?.standardizedFileURL, phase == .ready || phase == .presenting { return }
            load(url)
        }
    }

    func closeDeck() {
        Task {
            if phase == .presenting { await endShow() }
            phase = .empty
            deckURL = nil
            noteWritingTask?.cancel()
            drafts = [:]
            deckSlides = []
            pdf = nil
        }
    }

    // MARK: - Slideshow

    var screens: [NSScreen] { NSScreen.screens }

    /// The display the audience sees: your saved choice, otherwise a display other than the one
    /// showing this window, otherwise the main display.
    func audienceScreen() -> NSScreen {
        let name = AudienceDisplay.choose(saved: audienceScreenName, displays: screens.map(\.localizedName),
                                          presenter: presenterWindow?.screen?.localizedName)
        return screens.first { $0.localizedName == name } ?? screens.first ?? NSScreen.main!
    }

    /// Whether the show can be started: a deck is loaded and not already presenting. If the app lost the
    /// deck (it was closed there), starting opens it again.
    var canStart: Bool { phase == .ready && deckURL != nil }

    /// Start from Bema's own button, menu or ⌘↩: asks where the slides go first when there's a choice.
    /// The phone remote and clickers start straight away on the usual display.
    func requestStart() {
        guard canStart else { return }
        if AudienceDisplay.shouldAsk(enabled: askForDisplay, displayCount: screens.count) {
            showPresenterWindow()
            displayChooserOpen = true
        } else {
            startShow()
        }
    }

    func startShow() {
        guard canStart else { return }
        displayChooserOpen = false
        Task {
            if !powerPointReady, let url = deckURL {
                // The deck was closed in the app (or never opened there): open it again first.
                do {
                    try await app.open(url)
                    powerPointReady = true
                    notice = nil
                } catch {
                    notice = error.localizedDescription
                    return
                }
            }
            let screen = audienceScreen()
            let presenterScreen = presenterWindow?.screen
            let fullScreen = screens.count > 1 && screen != presenterScreen
            if fullScreen && !PowerPointController.hasAccessibility {
                PowerPointController.requestAccessibility()
            }
            do {
                showEnded = false
                audience = .normal
                let ok = try await app.startShow(on: screen, from: current, fullScreen: fullScreen)
                presentingOn = screen.localizedName
                audienceFullScreen = fullScreen && ok
                phase = .presenting
                startTimerForShow()
                // Replace any display notice left from an earlier start (a stale "only one display" stayed
                // after a second display was connected and the show restarted).
                if let displayNotice = AudienceDisplay.noticeAfterStart(fullScreen: fullScreen, displayCount: screens.count) {
                    notice = displayNotice
                } else if let current = notice, AudienceDisplay.notices.contains(current) {
                    notice = nil
                }
                if fullScreen && !ok {
                    notice = Self.accessibilityNotice
                    showAccessibilityHelp = true
                } else {
                    if notice == Self.accessibilityNotice { notice = nil }
                    showAccessibilityHelp = false
                }
                startPolling()
                // PowerPoint takes focus when the show starts; take it back so the keyboard and clicker reach us.
                try? await Task.sleep(nanoseconds: 700_000_000)
                showPresenterWindow()
            } catch {
                notice = error.localizedDescription
            }
        }
    }

    static let accessibilityNotice = "Full screen without a title bar needs Accessibility permission. If Bema is already listed there, select it, remove it with the – button and add it again (the old entry belongs to an earlier build). Then end and restart the show."

    func endShow() async {
        pollTask?.cancel()
        // Talking to an app that has quit would launch it again (and can hang): only ask a running app.
        if SlideshowApps.isRunning(app.bundleID) { try? await app.endShow() }
        showStopped()
    }

    /// Back to the deck after the show ends, however it ended: the timer stops and Start is available again.
    private func showStopped() {
        pollTask?.cancel()
        failedPolls = 0
        if phase == .presenting { phase = .ready }
        tool = .none
        updateOverlay()
        audience = .normal
        showEnded = false
        stopTimerForEndedShow()
    }

    /// The show ended: the timer stops, and its time stays on screen (dimmed) until the next show
    /// starts it from 0:00.
    func stopTimerForEndedShow() {
        if timerStartedAt != nil { toggleTimer() }
        timerFromEndedShow = timerAccumulated > 0
    }

    /// The presenter window was closed (Bema keeps running in the menu bar). Outside a show the timer
    /// is cleared, so the menu bar panel and the reopened window don't show an old talk's time. During
    /// a show it keeps counting: the show is still on.
    func presenterWindowClosed() {
        guard phase != .presenting else { return }
        resetTimer()
    }

    /// Whether there's a time worth showing: during a show, while it counts, or a paused time.
    var timerShown: Bool {
        phase == .presenting || timerStartedAt != nil || timerAccumulated > 0
    }

    /// A show is starting: an ended show's time is cleared first, so each show is timed from 0:00.
    func startTimerForShow() {
        if timerFromEndedShow { resetTimer() }
        if timerStartedAt == nil { toggleTimer() }
    }

    /// The deck went away in the app (closed, or the app quit) during the show.
    private func presentationClosed() {
        showStopped()
        powerPointReady = false
        notice = "The slideshow ended because the presentation was closed in \(app.appName). The timer has stopped. Start slideshow opens it again."
    }

    /// Status checks that failed in a row; many in a row (with the app still running) means the deck has gone.
    private var failedPolls = 0

    func endShowButton() { Task { await endShow() } }

    private func startPolling() {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.refresh()
                try? await Task.sleep(nanoseconds: 400_000_000)
            }
        }
    }

    private func refresh() async {
        guard phase == .presenting else { return }
        guard SlideshowApps.isRunning(app.bundleID) else { return presentationClosed() }
        guard let status = try? await app.status() else {
            failedPolls += 1
            if failedPolls >= 10 {
                // Not answering for about four seconds: stop rather than stay "presenting" forever.
                // Starting again re-checks the deck (and opens it if it's gone).
                showStopped()
                powerPointReady = false
                notice = "Bema lost touch with \(app.appName)'s slideshow, so it ended the show and stopped the timer. Start slideshow to present again."
            }
            return
        }
        failedPolls = 0
        switch status {
        case .running(let slide, let black, let white, let done, let frame):
            if slide > 0 && slide != current { current = slide }
            if app.supportsScreenStates { audience = black ? .black : (white ? .white : .normal) }
            showEnded = done
            if let frame {
                showFrame = frame
                // Work out which display the show is on (needed after reconnecting to a running show).
                if let screen = NSScreen.screens.first(where: { $0.frame.contains(CGPoint(x: frame.midX, y: frame.midY)) }) {
                    if presentingOn != screen.localizedName { presentingOn = screen.localizedName }
                    let full = abs(frame.width - screen.frame.width) < 2 && abs(frame.height - screen.frame.height) < 2
                    if audienceFullScreen != full { audienceFullScreen = full }
                }
            }
            updateOverlay()
        case .notRunning:
            // The show was ended in the app itself (Esc there, or its window closed).
            showStopped()
        case .presentationClosed:
            presentationClosed()
        }
    }

    // MARK: - Navigation

    func next() {
        if phase == .presenting {
            Task { try? await app.next(); await refresh() }
        } else if let n = nextSlide {
            current = n.id
        }
    }

    func previous() {
        if phase == .presenting {
            Task { try? await app.previous(); await refresh() }
        } else if let p = slides.last(where: { $0.id < current && !$0.hidden }) {
            current = p.id
        }
    }

    func goTo(_ id: Int) {
        guard slides.contains(where: { $0.id == id }) else { return }
        if phase == .presenting {
            current = id
            Task { try? await app.goTo(id); await refresh() }
        } else {
            current = id
        }
    }

    func first() { if let s = slides.first(where: { !$0.hidden }) { goTo(s.id) } }
    func last() { if let s = slides.last(where: { !$0.hidden }) { goTo(s.id) } }

    func toggleBlack() { setAudience(audience == .black ? .normal : .black) }
    func toggleWhite() { setAudience(audience == .white ? .normal : .white) }

    private func setAudience(_ state: AudienceScreen) {
        guard phase == .presenting else { return }
        audience = state
        let s: PowerPointController.ScreenState = state == .black ? .black : (state == .white ? .white : .running)
        Task { try? await app.setScreen(s); await refresh() }
    }

    // MARK: - Keyboard and clicker

    /// Returns true if the key was handled. Clickers usually send Page Up / Page Down, arrows or B.
    func handleKey(_ event: NSEvent) -> Bool {
        guard phase == .ready || phase == .presenting else { return false }
        // Typing (editing a draft, the notes writer settings) and sheets keep their keys.
        if let text = NSApp.keyWindow?.firstResponder as? NSTextView, text.isEditable { return false }
        if NSApp.keyWindow?.sheetParent != nil { return false }
        if !event.modifierFlags.intersection([.command, .control, .option]).isEmpty { return false }
        switch event.keyCode {
        case 124, 125, 49, 121, 36, 76: next(); return true          // → ↓ space PgDn return enter
        case 123, 126, 116, 51: previous(); return true               // ← ↑ PgUp delete
        case 115: first(); return true                                // Home
        case 119: last(); return true                                 // End
        case 53:                                                      // Esc
            if tool != .none { tool = .none; return true }
            if phase == .presenting { endShowButton(); return true }
            return false
        default: break
        }
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "b", ".": toggleBlack(); return true
        case "w", ",": toggleWhite(); return true
        case "l": tool = tool == .laser ? .none : .laser; return true
        case "s": tool = tool == .spotlight ? .none : .spotlight; return true
        case "m": tool = tool == .magnifier ? .none : .magnifier; return true
        case "t": tool = tool == .stamp ? .none : .stamp; return true
        case "1": fireReaction(.confetti); return true
        case "2": fireReaction(.applause); return true
        case "3": fireReaction(.hearts); return true
        case "p": tool = tool == .pen ? .none : .pen; return true
        case "h": tool = tool == .highlighter ? .none : .highlighter; return true
        case "e", "c": clearDrawings(); return true
        default: return false
        }
    }

    // MARK: - Laser pointer and drawing

    /// True while something on the slide is moving (trail, ripple, reaction), so the canvas animates.
    var effectsAnimating: Bool {
        !ripples.isEmpty || reaction != nil || (tool == .laser && laserStyle.usesTrail && !laserTrail.isEmpty)
    }

    func movePointer(to p: CGPoint) {
        laserPoint = p
        if tool == .laser && laserStyle.usesTrail {
            laserTrail.append(TrailPoint(point: p, time: Date()))
            if laserTrail.count > 40 { laserTrail.removeFirst(laserTrail.count - 40) }
            startEffectsTimer()
        }
    }

    func addRipple(at p: CGPoint) {
        ripples.append(Ripple(point: p, start: Date()))
        startEffectsTimer()
    }

    func placeStamp(at p: CGPoint) {
        stamps.append(Stamp(kind: stampKind, point: p))
    }

    func fireReaction(_ kind: ReactionKind) {
        reaction = Reaction(kind: kind, start: Date(), seed: UInt64.random(in: 1...UInt64.max / 2))
        startEffectsTimer()
    }

    /// Clears finished ripples, old trail points and finished reactions; stops when nothing is moving.
    private func startEffectsTimer() {
        guard effectsTimer == nil else { return }
        effectsTimer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else { timer.invalidate(); return }
                let now = Date()
                let liveRipples = self.ripples.filter { now.timeIntervalSince($0.start) < 0.7 }
                if liveRipples.count != self.ripples.count { self.ripples = liveRipples }
                let liveTrail = self.laserTrail.filter { now.timeIntervalSince($0.time) < 0.6 }
                if liveTrail.count != self.laserTrail.count { self.laserTrail = liveTrail }
                if let r = self.reaction, now.timeIntervalSince(r.start) > r.kind.duration { self.reaction = nil }
                if self.ripples.isEmpty && self.laserTrail.isEmpty && self.reaction == nil {
                    timer.invalidate()
                    self.effectsTimer = nil
                }
            }
        }
    }

    func commitStroke() {
        if let s = activeStroke, s.points.count > 0 { strokes.append(s) }
        activeStroke = nil
    }

    func clearDrawings() {
        strokes = []
        stamps = []
        activeStroke = nil
    }

    /// The slide's rectangle inside the slideshow area (PowerPoint letterboxes it to keep its shape).
    private func slideRect(in area: CGRect) -> CGRect {
        let aspect = slideAspect
        var w = area.width, h = area.width / aspect
        if h > area.height { h = area.height; w = h * aspect }
        return CGRect(x: area.midX - w / 2, y: area.midY - h / 2, width: w, height: h)
    }

    private func updateOverlay() {
        if phase == .presenting, audience == .normal || !app.supportsScreenStates, !showEnded, let frame = showFrame {
            overlay.show(over: slideRect(in: frame), model: self)
        } else {
            overlay.hide()
        }
    }

    // MARK: - Timer

    func elapsed(at date: Date) -> TimeInterval {
        timerAccumulated + (timerStartedAt.map { date.timeIntervalSince($0) } ?? 0)
    }

    /// The timer holds the time of a show that has ended: shown dimmed, and the next show starts from 0:00.
    /// Starting, pausing or resetting it by hand makes it the person's own again.
    @Published private(set) var timerFromEndedShow = false

    /// How the timer looks right now.
    func timerLook(at date: Date) -> TimerLook {
        TimerLook.of(running: timerStartedAt != nil, elapsed: elapsed(at: date), target: targetSeconds)
    }

    func toggleTimer() {
        timerFromEndedShow = false
        if let start = timerStartedAt {
            timerAccumulated += Date().timeIntervalSince(start)
            timerStartedAt = nil
        } else {
            timerStartedAt = Date()
        }
    }

    func resetTimer() {
        timerFromEndedShow = false
        timerAccumulated = 0
        timerStartedAt = phase == .presenting ? Date() : nil
    }

    // MARK: - Slide images

    /// The PDF has one page per slide, or one per visible slide if PowerPoint left hidden slides out.
    private func pageIndex(for id: Int) -> Int? {
        guard let pdf else { return nil }
        if pdf.pageCount == slides.count { return id - 1 }
        let visible = slides.filter { !$0.hidden }
        return visible.firstIndex { $0.id == id }.flatMap { $0 < pdf.pageCount ? $0 : nil }
    }

    func image(for id: Int, width points: CGFloat) -> NSImage? {
        guard let pdf, let idx = pageIndex(for: id), let page = pdf.page(at: idx) else { return nil }
        let scale = presenterWindow?.backingScaleFactor ?? 2
        let bucket = max(200, (Int(points * scale) / 200 + 1) * 200)
        let key = "\(id)-\(bucket)"
        if let img = imageCache.object(forKey: key as NSString) { return img }
        let bounds = page.bounds(for: .cropBox)
        let size = NSSize(width: CGFloat(bucket), height: CGFloat(bucket) * bounds.height / max(bounds.width, 1))
        let img = page.thumbnail(of: size, for: .cropBox)
        imageCache.setObject(img, forKey: key as NSString, cost: Int(size.width * size.height * 4))
        return img
    }

    /// The slide's text, from the slide previews (the same for PowerPoint files, cloud decks and Keynote).
    func slideText(for id: Int) -> String {
        guard let pdf, let idx = pageIndex(for: id), let page = pdf.page(at: idx) else { return "" }
        return page.string ?? ""
    }

    /// The slide as a PNG about 1280 pixels wide, for writers that read pictures.
    func slidePNG(for id: Int) -> Data? {
        guard let pdf, let idx = pageIndex(for: id), let page = pdf.page(at: idx) else { return nil }
        let bounds = page.bounds(for: .cropBox)
        let size = NSSize(width: 1280, height: 1280 * bounds.height / max(bounds.width, 1))
        let image = page.thumbnail(of: size, for: .cropBox)
        guard let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }

    var slideAspect: CGFloat {
        guard let page = pdf?.page(at: 0) else { return 16.0 / 9.0 }
        let b = page.bounds(for: .cropBox)
        return b.width / max(b.height, 1)
    }

    // MARK: - Window

    var presenterWindow: NSWindow? {
        NSApp.windows.first { $0.identifier?.rawValue.hasPrefix("main") == true && $0.isVisible }
            ?? NSApp.windows.first { $0.isVisible && $0.canBecomeMain }
    }

    /// Brings the presenter window forward, opening it again if it was closed, and puts Bema back in
    /// the Dock while it's open.
    func showPresenterWindow() {
        if NSApp.activationPolicy() == .accessory { NSApp.setActivationPolicy(.regular) }
        NSApp.activate()
        if let opener = MainWindowRef.opener {
            opener(id: "main")
        } else if let w = NSApp.windows.first(where: { $0.identifier?.rawValue.hasPrefix("main") == true }) {
            if w.isMiniaturized { w.deminiaturize(nil) }
            w.makeKeyAndOrderFront(nil)
        }
    }

    private func applyWindowLevel() {
        presenterWindow?.level = alwaysOnTop ? .floating : .normal
    }

    /// Dark, seamless title bar that blends into the top bar.
    func styleWindow() {
        guard let w = presenterWindow else { return }
        w.titlebarAppearsTransparent = true
        w.backgroundColor = Theme.nsBar
        w.appearance = NSAppearance(named: .darkAqua)
    }

    var targetSeconds: TimeInterval { TimeInterval(targetMinutes * 60) }
}

/// Whether saved slide previews still show this deck. Previews are saved per file path, date and size,
/// but PowerPoint can export an older version it still has open (a file replaced on disk), and those
/// wrong pictures would then be reused for good. So the previews' text is checked against slide titles.
enum PreviewCheck {
    static func matches(pageTexts: [String], slides: [SlideInfo]) -> Bool {
        let visible = slides.filter { !$0.hidden }
        guard pageTexts.count == slides.count || pageTexts.count == visible.count else { return false }
        let pairs = pageTexts.count == slides.count ? Array(zip(pageTexts, slides)) : Array(zip(pageTexts, visible))
        let titled = pairs.compactMap { text, slide -> (String, String)? in
            let title = squeeze(slide.title)
            return title.count >= 4 ? (squeeze(text), String(title.prefix(16))) : nil
        }
        guard !titled.isEmpty else { return true }
        let found = titled.filter { text, title in text.contains(title) }.count
        return found * 2 >= titled.count
    }

    /// Lower case with all spacing removed: PDF text breaks lines and spaces differently from the deck.
    static func squeeze(_ s: String) -> String {
        String(s.lowercased().unicodeScalars.filter { !CharacterSet.whitespacesAndNewlines.contains($0) }.map(Character.init))
    }
}

/// How the timer is drawn. Pure, so it's tested: a paused timer (after a show ends, or paused by hand)
/// is dimmed and never says "over time", so it doesn't look like it's still counting.
enum TimerLook: Equatable {
    /// Counting, within the planned time (or no plan).
    case running
    /// Counting, past the planned time: red, "over time".
    case over
    /// Not counting: the time stays, dimmed.
    case paused

    static func of(running: Bool, elapsed: TimeInterval, target: TimeInterval) -> TimerLook {
        guard running else { return .paused }
        return target > 0 && elapsed > target ? .over : .running
    }
}
