import AppKit
import ApplicationServices

enum PowerPointError: LocalizedError {
    case notInstalled
    case script(String)
    case timeout(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled: return "Microsoft PowerPoint isn't installed."
        case .script(let msg): return msg
        case .timeout(let what): return "PowerPoint didn't respond while \(what)."
        }
    }
}

/// State of the running slideshow, as read from PowerPoint.
enum ShowStatus: Equatable {
    case notRunning
    /// `frame` is the area the slideshow draws in, in Cocoa screen coordinates (bottom-left origin).
    case running(slide: Int, black: Bool, white: Bool, done: Bool, frame: CGRect?)
    case presentationClosed
}

/// Drives Microsoft PowerPoint through AppleScript. All calls run on one serial queue,
/// because NSAppleScript should only be used from a single thread.
final class PowerPointController {
    static let bundleID = "com.microsoft.Powerpoint"
    private let queue = DispatchQueue(label: "Bema.PowerPoint")

    /// The name PowerPoint uses for the open presentation (the file name).
    private(set) var presentationName: String = ""

    static var isRunning: Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }

    /// The open deck that came from OneDrive, SharePoint or Teams (a web address), if that's what this is.
    private var cloudURL: URL?

    /// The deck in PowerPoint's front window: a file on this Mac, or the web address of a deck opened from
    /// OneDrive, SharePoint or Teams. Nil for a deck that was never saved. Never launches PowerPoint.
    func activePresentationURL() async -> URL? {
        guard Self.isRunning else { return nil }
        let path = try? await run("""
        tell application "Microsoft PowerPoint"
            if (count of presentations) = 0 then return ""
            return full name of active presentation
        end tell
        """)
        guard let path else { return nil }
        if let cloud = CloudDeck.url(fromFullName: path) { return cloud }
        guard path.hasPrefix("/"), FileManager.default.fileExists(atPath: path) else { return nil }
        return URL(fileURLWithPath: path)
    }

    static var isInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    // MARK: - Commands

    /// Opens the deck in PowerPoint (or reuses it if already open) and waits until it's ready.
    func open(_ url: URL) async throws {
        guard Self.isInstalled else { throw PowerPointError.notInstalled }
        if CloudDeck.isCloud(url) { return try await attachToCloudDeck(url) }
        cloudURL = nil
        let name = url.lastPathComponent
        presentationName = name
        let p = Self.quote(url.path), n = Self.quote(name)
        try await run("""
        tell application "Microsoft PowerPoint"
            if not (exists presentation \(n)) then open POSIX file \(p)
        end tell
        """)
        for _ in 0..<60 {
            let ok = try await run("""
            tell application "Microsoft PowerPoint" to return (exists presentation \(n)) as text
            """)
            if ok == "true" { return }
            try await Task.sleep(nanoseconds: 250_000_000)
        }
        throw PowerPointError.timeout("opening \(name)")
    }

    /// A cloud deck can't be opened from a path: find it among the decks PowerPoint has open.
    private func attachToCloudDeck(_ url: URL) async throws {
        let candidates = CloudDeck.fullNameCandidates(url).map(Self.quote).joined(separator: ", ")
        let name = Self.isRunning ? try await run("""
        tell application "Microsoft PowerPoint"
            set wanted to {\(candidates)}
            repeat with i from 1 to count of presentations
                if wanted contains ((full name of presentation i) as text) then return name of presentation i
            end repeat
            return ""
        end tell
        """) : ""
        guard !name.isEmpty else {
            throw PowerPointError.script("This deck is in OneDrive or SharePoint and isn't open in PowerPoint any more. Open it in PowerPoint again, then present it from Bema.")
        }
        presentationName = name
        cloudURL = url
    }

    /// Titles and notes read through PowerPoint, for a deck Bema can't read itself: a cloud deck (there's
    /// no file) or a file macOS keeps from Bema. Other files Bema reads itself: that's faster.
    func readSlides() async throws -> [SlideInfo]? {
        // `set x to content of …` doesn't compile against PowerPoint's dictionary, hence `get` + `copy result`.
        let out = try await run("""
        tell application "Microsoft PowerPoint"
            set thePres to presentation \(Self.quote(presentationName))
            set rs to character id 30
            set us to character id 31
            set titleKinds to {"placeholder type title placeholder", "placeholder type center title placeholder"}
            set rv to ""
            repeat with i from 1 to count of slides of thePres
                set theSlide to slide i of thePres
                set titleText to ""
                set notesText to ""
                set hiddenText to "false"
                try
                    get (hidden of slide show transition of theSlide) as text
                    copy result to hiddenText
                end try
                repeat with k from 1 to count of shapes of theSlide
                    try
                        get (placeholder type of placeholder format of shape k of theSlide) as text
                        copy result to kindText
                        if titleKinds contains kindText then
                            get content of text range of text frame of shape k of theSlide
                            copy result to titleText
                            exit repeat
                        end if
                    end try
                end repeat
                set notesPage to notes page of theSlide
                repeat with k from 1 to count of shapes of notesPage
                    try
                        get (placeholder type of placeholder format of shape k of notesPage) as text
                        copy result to kindText
                        if kindText is "placeholder type body placeholder" then
                            get content of text range of text frame of shape k of notesPage
                            copy result to notesText
                            exit repeat
                        end if
                    end try
                end repeat
                set rv to rv & (i as text) & us & hiddenText & us & titleText & us & notesText & rs
            end repeat
            return rv
        end tell
        """)
        let slides = CloudDeck.parseSlides(out)
        if slides.isEmpty { throw PPTXError.unreadable("PowerPoint reported no slides.") }
        return slides
    }

    /// Exports the deck as a PDF so we can draw slide previews.
    /// Writes speaker notes into the notes area (the body placeholder of each notes page) and saves the
    /// deck. Returns the slides that have a notes area and were written.
    func writeNotes(_ notes: [Int: String]) async throws -> Set<Int> {
        let blocks = notes.sorted { $0.key < $1.key }.map { id, text in
            """
                set notesPage to notes page of slide \(id) of thePres
                repeat with k from 1 to count of shapes of notesPage
                    try
                        get (placeholder type of placeholder format of shape k of notesPage) as text
                        copy result to kindText
                        if kindText is "placeholder type body placeholder" then
                            set content of text range of text frame of shape k of notesPage to \(AppleScriptText.literal(text))
                            set writtenList to writtenList & ",\(id)"
                            exit repeat
                        end if
                    end try
                end repeat
            """
        }.joined(separator: "\n")
        let out = try await run("""
        tell application "Microsoft PowerPoint"
            set thePres to presentation \(Self.quote(presentationName))
            set writtenList to ""
        \(blocks)
            save thePres
            return writtenList
        end tell
        """)
        return Set(out.split(separator: ",").compactMap { Int($0) })
    }

    func exportPDF(to pdfURL: URL) async throws {
        try FileManager.default.createDirectory(at: pdfURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: pdfURL)
        try await run("""
        tell application "Microsoft PowerPoint"
            save presentation \(Self.quote(presentationName)) in (POSIX file \(Self.quote(pdfURL.path))) as save as PDF
        end tell
        """)
    }

    /// Starts the slideshow in a window, places it on `screen` and makes it full screen there.
    /// - Parameters:
    ///   - fullScreen: false keeps it as a normal window (rehearsing on a single display).
    /// - Returns: false if full screen needed Accessibility permission that isn't granted.
    @discardableResult
    func startShow(on screen: NSScreen, from slide: Int, fullScreen: Bool) async throws -> Bool {
        let n = Self.quote(presentationName)
        try await run("""
        tell application "Microsoft PowerPoint"
            set ss to slide show settings of presentation \(n)
            set show type of ss to slide show type window
            run slide show ss
        end tell
        """)

        // Wait for the slideshow window.
        var ready = false
        for _ in 0..<40 {
            if case .running = try await status() { ready = true; break }
            try await Task.sleep(nanoseconds: 150_000_000)
        }
        guard ready else { throw PowerPointError.timeout("starting the slideshow") }

        // AppleScript window coordinates have their origin at the top-left of the main display.
        let mainHeight = NSScreen.screens.first?.frame.maxY ?? screen.frame.maxY
        let target = fullScreen ? screen.frame : screen.visibleFrame.insetBy(dx: screen.visibleFrame.width * 0.2,
                                                                                dy: screen.visibleFrame.height * 0.2)
        let left = Int(target.minX), top = Int(mainHeight - target.maxY)
        try await run("""
        tell application "Microsoft PowerPoint"
            set w to slide show window of presentation \(n)
            set left position of w to \(left)
            set top of w to \(top)
            set width of w to \(Int(target.width))
            set height of w to \(Int(target.height))
        end tell
        """)

        if slide > 1 { try await goTo(slide) }

        guard fullScreen else { return true }
        try await Task.sleep(nanoseconds: 300_000_000)
        return Self.makeSlideShowWindowFullScreen()
    }

    func next() async throws { try await viewCommand("go to next slide") }
    func previous() async throws { try await viewCommand("go to previous slide") }

    /// PowerPoint's own "go to slide" fails for slideshow views on Mac, so step there one move at a time.
    func goTo(_ slide: Int) async throws {
        try await run("""
        tell application "Microsoft PowerPoint"
            set v to slideshow view of slide show window of presentation \(Self.quote(presentationName))
            repeat 500 times
                set curSlide to slide number of slide of v
                if curSlide = \(slide) then exit repeat
                if curSlide < \(slide) then
                    go to next slide v
                else
                    go to previous slide v
                end if
                if (slide number of slide of v) = curSlide then exit repeat
            end repeat
        end tell
        """)
    }

    enum ScreenState: String { case running, black = "black screen", white = "white screen" }

    func setScreen(_ state: ScreenState) async throws {
        try await run("""
        tell application "Microsoft PowerPoint"
            set slide state of slideshow view of slide show window of presentation \(Self.quote(presentationName)) to slide show state \(state.rawValue)
        end tell
        """)
    }

    func endShow() async throws {
        // Leave full screen first; closing a full-screen window can leave an empty, black Space behind.
        if Self.setSlideShowWindowFullScreen(false) {
            try? await Task.sleep(nanoseconds: 900_000_000)
        }
        try await run("""
        tell application "Microsoft PowerPoint"
            try
                exit slide show (slideshow view of slide show window of presentation \(Self.quote(presentationName)))
            end try
            try
                set show type of (slide show settings of presentation \(Self.quote(presentationName))) to slide show type speaker
            end try
        end tell
        """)
    }

    func status() async throws -> ShowStatus {
        let out = try await run("""
        tell application "Microsoft PowerPoint"
            if not (exists presentation \(Self.quote(presentationName))) then return "closed"
            try
                set v to slideshow view of slide show window of presentation \(Self.quote(presentationName))
            on error
                return "none"
            end try
            -- After the show ends in PowerPoint (Esc, its window closed) the view can still resolve but
            -- reading it fails: that means no show, not an error.
            try
                set stateText to (slide state of v) as text
            on error
                return "none"
            end try
            try
                set slideNum to slide number of slide of v
            on error
                set slideNum to 0
            end try
            set w to slide show window of presentation \(Self.quote(presentationName))
            set frameText to ""
            try
                set frameText to ((left position of w) as text) & "," & ((top of w) as text) & "," & ((width of w) as text) & "," & ((height of w) as text)
            end try
            return (slideNum as text) & "|" & stateText & "|" & frameText
        end tell
        """)
        switch out {
        case "closed": return .presentationClosed
        case "none", "": return .notRunning
        default:
            let parts = out.components(separatedBy: "|")
            let slide = Int(parts.first ?? "") ?? 0
            let state = parts.count > 1 ? parts[1] : ""
            return .running(slide: slide,
                            black: state.contains("black"),
                            white: state.contains("white"),
                            done: state.contains("done"),
                            frame: parts.count > 2 ? Self.cocoaFrame(parts[2]) : nil)
        }
    }

    /// PowerPoint reports the slideshow's drawing area (excluding the title bar) with a top-left origin
    /// on the main display, as "left,top,width,height". Numbers may use a decimal comma.
    private static func cocoaFrame(_ text: String) -> CGRect? {
        // Split on "," but rejoin decimal commas: values come as e.g. "3200,0,487,0,..." with some locales.
        let raw = text.components(separatedBy: ",")
        let nums: [Double]
        if raw.count == 4 {
            nums = raw.compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
        } else if raw.count == 8 {
            nums = stride(from: 0, to: 8, by: 2).compactMap { Double("\(raw[$0]).\(raw[$0 + 1])") }
        } else {
            return nil
        }
        guard nums.count == 4, nums[2] > 0, nums[3] > 0 else { return nil }
        let mainHeight = NSScreen.screens.first?.frame.maxY ?? 0
        return CGRect(x: nums[0], y: mainHeight - nums[1] - nums[3], width: nums[2], height: nums[3])
    }

    // MARK: - Accessibility (full screen on a chosen display)

    static var hasAccessibility: Bool { AXIsProcessTrusted() }

    static func openAccessibilitySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    static func requestAccessibility() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    /// Uses the Accessibility API to switch PowerPoint's slideshow window to native full screen
    /// on whichever display it's on. That hides its title bar and the menu bar.
    static func makeSlideShowWindowFullScreen() -> Bool { setSlideShowWindowFullScreen(true) }

    /// Returns true if the window's full-screen state was changed.
    @discardableResult
    static func setSlideShowWindowFullScreen(_ on: Bool) -> Bool {
        guard AXIsProcessTrusted(),
              let app = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first
        else { return false }
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &value) == .success,
              let windows = value as? [AXUIElement] else { return false }
        for window in windows {
            var title: CFTypeRef?
            AXUIElementCopyAttributeValue(window, kAXTitleAttribute as CFString, &title)
            if let t = title as? String, t.hasPrefix("PowerPoint Slide Show") {
                var current: CFTypeRef?
                AXUIElementCopyAttributeValue(window, "AXFullScreen" as CFString, &current)
                if (current as? Bool) == on { return false }
                return AXUIElementSetAttributeValue(window, "AXFullScreen" as CFString, (on ? kCFBooleanTrue : kCFBooleanFalse)) == .success
            }
        }
        return false
    }

    // MARK: - AppleScript plumbing

    private func viewCommand(_ command: String) async throws {
        try await run("""
        tell application "Microsoft PowerPoint"
            \(command) (slideshow view of slide show window of presentation \(Self.quote(presentationName)))
        end tell
        """)
    }

    @discardableResult
    private func run(_ source: String) async throws -> String {
        try await withCheckedThrowingContinuation { cont in
            queue.async {
                var error: NSDictionary?
                let script = NSAppleScript(source: source)
                let result = script?.executeAndReturnError(&error)
                if let error {
                    let msg = error[NSAppleScript.errorMessage] as? String ?? "PowerPoint reported an error."
                    let code = error[NSAppleScript.errorNumber] as? Int ?? 0
                    if code == -1743 {
                        cont.resume(throwing: PowerPointError.script(
                            "Bema isn't allowed to control PowerPoint. Turn it on in System Settings → Privacy & Security → Automation."))
                    } else {
                        cont.resume(throwing: PowerPointError.script("\(msg) (\(code))"))
                    }
                } else {
                    cont.resume(returning: result?.stringValue ?? "")
                }
            }
        }
    }

    /// An AppleScript string literal.
    static func quote(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}
