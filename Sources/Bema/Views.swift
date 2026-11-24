import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @EnvironmentObject private var model: PresenterModel
    @State private var dropTargeted = false
    @AppStorage("hidePresenterFromSharing") private var hidePresenterFromSharing = true
    @AppStorage("theme") private var themeName = Theme.Name.backstage.rawValue
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Group {
            switch model.phase {
            case .empty:
                WelcomeView(dropTargeted: dropTargeted)
            case .loading(let message):
                VStack(spacing: 16) {
                    BemaMark(size: 40)
                    ProgressView().controlSize(.small).tint(Theme.accent)
                    Text(message).font(.system(size: 14)).foregroundStyle(Theme.muted)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .failed(let message):
                VStack(spacing: 16) {
                    Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 32)).foregroundStyle(Theme.warnStrong)
                    Text(message).font(.system(size: 15)).foregroundStyle(Theme.text)
                        .multilineTextAlignment(.center).frame(maxWidth: 440)
                    Button("Open another presentation…") { model.openPanel() }
                        .buttonStyle(PillButtonStyle(kind: .accent, height: 40))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            case .ready, .presenting:
                PresenterView()
            }
        }
        .id(themeName)
        .background(Theme.bar.ignoresSafeArea())
        .foregroundStyle(Theme.text)
        .preferredColorScheme(Theme.colorScheme)
        .tint(Theme.accent)
        .navigationTitle(model.deckURL == nil ? "Bema" : model.deckName)
        .sheet(isPresented: $model.displayChooserOpen) { DisplayChooserSheet().environmentObject(model) }
        .sheet(isPresented: $model.noteWriterSettingsOpen) { NoteWriterSettingsView().environmentObject(model) }
        .background(WindowStyler(hideFromSharing: hidePresenterFromSharing, theme: themeName))
        .onReceive(NotificationCenter.default.publisher(for: .openSelfView)) { _ in
            openWindow(id: "selfview")
        }
        .onAppear { SelfViewWindowRef.opener = openWindow; MainWindowRef.opener = openWindow }
        .onReceive(NotificationCenter.default.publisher(for: .openPrompterWindow)) { note in
            openWindow(id: "prompter")
            if (note.userInfo?["place"] as? String) == "camera" {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { PrompterWindowRef.placeUnderCamera() }
            }
        }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            guard let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url, SlideshowApps.allExtensions.contains(url.pathExtension.lowercased()) else { return }
                Task { @MainActor in PresenterModel.shared.load(url) }
            }
            return true
        }
    }
}

/// Reaches the hosting NSWindow to make the title bar blend into the dark top bar.
struct WindowStyler: NSViewRepresentable {
    /// Keep this window out of screen sharing and screenshots (Zoom, Teams, Meet, QuickTime).
    var hideFromSharing = false
    /// Changing it re-applies the window colors.
    var theme = ""

    final class Probe: NSView {
        var onWindow: ((NSWindow) -> Void)?
        static func applyTheme(to w: NSWindow) {
            w.backgroundColor = Theme.nsBar
            w.appearance = NSAppearance(named: Theme.palette.isDark ? .darkAqua : .aqua)
        }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let w = window else { return }
            w.titlebarAppearsTransparent = true
            w.titlebarSeparatorStyle = .none
            Self.applyTheme(to: w)
            onWindow?(w)
        }
    }
    func makeNSView(context: Context) -> NSView {
        let probe = Probe()
        let hide = hideFromSharing
        probe.onWindow = { $0.sharingType = hide ? .none : .readOnly }
        return probe
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        guard let w = nsView.window else { return }
        w.sharingType = hideFromSharing ? .none : .readOnly
        Probe.applyTheme(to: w)
    }
}

// MARK: - Welcome

struct WelcomeView: View {
    @EnvironmentObject private var model: PresenterModel
    var dropTargeted: Bool

    var body: some View {
        HStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 34) {
                    HStack(spacing: 16) {
                        BemaMark(size: 30)
                            .frame(width: 56, height: 56)
                            .background(RoundedRectangle(cornerRadius: 14).fill(Theme.raised))
                            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Theme.borderStrong))
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Bema").font(.system(size: 30, weight: .bold)).tracking(-0.5)
                            Text("A presenter view you control, for PowerPoint and Keynote on Mac")
                                .font(.system(size: 15)).foregroundStyle(Theme.muted)
                        }
                    }

                    if let notice = model.notice {
                        Text(notice).font(.system(size: 13)).foregroundStyle(Theme.warnText)
                            .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: 10).fill(Theme.warnBg))
                            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Theme.warnBorder))
                            .onTapGesture { model.notice = nil }
                    }

                    if let open = model.openInPowerPoint {
                        OpenInPowerPointCard(url: open)
                    }

                    VStack(spacing: 18) {
                        Image(systemName: "arrow.down.doc")
                            .font(.system(size: 36, weight: .light))
                            .foregroundStyle(dropTargeted ? Theme.accent : Theme.dim)
                        VStack(spacing: 6) {
                            Text("Drop a PowerPoint or Keynote deck here").font(.system(size: 20, weight: .semibold))
                            Text("or right-click a .pptx or .key file in Finder and choose Present with Bema")
                                .font(.system(size: 14)).foregroundStyle(Theme.muted)
                        }
                        Button("Open presentation…") { model.openPanel() }
                            .buttonStyle(PillButtonStyle(kind: .accent, height: 44))
                            .keyboardShortcut("o")
                    }
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 44).padding(.horizontal, 28)
                    .background(RoundedRectangle(cornerRadius: 18).fill(dropTargeted ? Theme.accent.opacity(0.08) : Theme.bar))
                    .overlay(RoundedRectangle(cornerRadius: 18)
                        .strokeBorder(dropTargeted ? Theme.accent : Color(hex: 0x353B47), style: StrokeStyle(lineWidth: 2, dash: [8, 6])))

                    QuickPromptCard()

                    if !model.recents.isEmpty {
                        VStack(alignment: .leading, spacing: 10) {
                            CapsLabel("Recent")
                            VStack(spacing: 0) {
                                ForEach(Array(model.recents.prefix(5).enumerated()), id: \.element) { index, url in
                                    if index > 0 { Divider().overlay(Theme.border) }
                                    RecentRow(url: url)
                                }
                            }
                            .background(RoundedRectangle(cornerRadius: 12).fill(Theme.panel))
                            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.border))
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                        }
                    }
                }
                .frame(maxWidth: 640)
                .padding(.horizontal, 56).padding(.top, 64).padding(.bottom, 48)
                .frame(maxWidth: .infinity)
            }
            .frame(minWidth: 420)
            .background(Theme.ground)

            PreflightPanel()
                .frame(width: 360)
        }
        .sheet(isPresented: $model.stageToolsOpen) {
            VStack(spacing: 0) {
                StageToolsPanel()
                HStack { Spacer(); Button("Done") { model.stageToolsOpen = false }.keyboardShortcut(.defaultAction) }
                    .padding(12).background(Theme.panel)
            }
            .preferredColorScheme(Theme.colorScheme)
        }
    }
}

/// "Open in PowerPoint: <deck>  [Present]"
struct OpenInPowerPointCard: View {
    @EnvironmentObject private var model: PresenterModel
    let url: URL

    var body: some View {
        HStack(spacing: 14) {
            RoundedRectangle(cornerRadius: 5).fill(Color.white).frame(width: 52, height: 34)
                .overlay(alignment: .topLeading) {
                    VStack(alignment: .leading, spacing: 3) {
                        Capsule().fill(Color(hex: 0x1C1F26)).frame(width: 24, height: 3)
                        Capsule().fill(Color(hex: 0xD5DAE1)).frame(width: 32, height: 2.5)
                        Capsule().fill(Color(hex: 0xD5DAE1)).frame(width: 26, height: 2.5)
                    }.padding(6)
                }
            VStack(alignment: .leading, spacing: 3) {
                CapsLabel("Open in \(model.openInAppName)", color: Theme.accent)
                Text(url.deletingPathExtension().lastPathComponent)
                    .font(.system(size: 16, weight: .semibold)).lineLimit(1)
            }
            Spacer(minLength: 8)
            Text("⌃⌥P").font(.system(size: 12, weight: .medium)).foregroundStyle(Theme.dim)
                .help("Press Control-Option-P anywhere to present the deck open in PowerPoint or Keynote")
            Button { model.load(url) } label: { Label("Present", systemImage: "play.fill").fixedSize() }
                .buttonStyle(PillButtonStyle(kind: .accent, height: 38))
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 14).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Theme.accent.opacity(0.5)))
    }
}

struct RecentRow: View {
    @EnvironmentObject private var model: PresenterModel
    let url: URL
    @State private var hovering = false

    var body: some View {
        Button { model.load(url) } label: {
            HStack(spacing: 14) {
                RoundedRectangle(cornerRadius: 4).fill(Color.white).frame(width: 40, height: 26)
                    .overlay(alignment: .topLeading) {
                        VStack(alignment: .leading, spacing: 2) {
                            Capsule().fill(Color(hex: 0x1C1F26)).frame(width: 18, height: 2.5)
                            Capsule().fill(Color(hex: 0xD5DAE1)).frame(width: 24, height: 2)
                        }.padding(4)
                    }
                VStack(alignment: .leading, spacing: 2) {
                    Text(url.lastPathComponent).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                    Text(url.deletingLastPathComponent().lastPathComponent)
                        .font(.system(size: 12)).foregroundStyle(Theme.muted)
                }
                Spacer()
                Image(systemName: "arrow.right").font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(hovering ? Theme.accent : Theme.dim)
            }
            .padding(.horizontal, 16).padding(.vertical, 12)
            .background(hovering ? Theme.raised : Theme.panel)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// "Before you present": PowerPoint, the audience display and Accessibility, with live status.
struct PreflightPanel: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                VStack(alignment: .leading, spacing: 6) {
                    CapsLabel("Before you present")
                    Text(allGood ? "Ready to go" : "Almost ready").font(.system(size: 22, weight: .semibold))
                }

                VStack(spacing: 12) {
                    CheckCard(ok: PowerPointController.isInstalled || KeynoteController.isInstalled,
                              title: appsTitle,
                              detail: PowerPointController.isInstalled || KeynoteController.isInstalled
                                ? "macOS asks once to let Bema control each app"
                                : "Install Microsoft PowerPoint or Apple Keynote to present")

                    CheckCard(ok: true, title: "Audience display") {
                        AudienceDisplayPicker(style: .field)
                        Text(model.displayCount > 1
                             ? "Slides open full screen on this display"
                             : "One display connected: slides open in a window for rehearsal")
                            .font(.system(size: 12)).foregroundStyle(Theme.muted)
                    }

                    if model.accessibilityGranted {
                        CheckCard(ok: true, title: "Full screen allowed", detail: "Accessibility permission is on")
                    } else {
                        WarningCard(title: "Full screen needs Accessibility",
                                    detail: "Without it, slides show in a window with a title bar. Already listed? Remove the old entry with – and allow it again.") {
                            PowerPointController.openAccessibilitySettings()
                        }
                    }
                }

                VStack(alignment: .leading, spacing: 8) {
                    CapsLabel("Camera")
                    HStack(spacing: 12) {
                        CameraControls()
                        Text("Self view: only you see it. On slides: the audience sees it during the show.")
                            .font(.system(size: 12)).foregroundStyle(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    }
                }

                Button { model.stageToolsOpen = true } label: {
                    Label("Stage tools: phone remote, poll, captions…", systemImage: "slider.horizontal.3")
                        .font(.system(size: 13, weight: .medium))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Theme.accent)

                VStack(alignment: .leading, spacing: 10) {
                    CapsLabel("Keys during a show")
                    Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 8) {
                        keyRow("→  Space  Page Down", "Next")
                        keyRow("←  Page Up", "Previous")
                        keyRow("B  /  W", "Black or white screen")
                        keyRow("L   P   H", "Laser, pen, highlighter")
                        keyRow("E", "Clear drawings")
                        keyRow("Esc", "Put tool away, then end show")
                    }
                    .font(.system(size: 13))
                }
                .padding(.top, 8)
            }
            .padding(.horizontal, 32).padding(.top, 64).padding(.bottom, 48)
        }
        .background(Theme.bar)
        .overlay(alignment: .leading) { Rectangle().fill(Theme.border).frame(width: 1) }
    }

    private var allGood: Bool { (PowerPointController.isInstalled || KeynoteController.isInstalled) && model.accessibilityGranted }

    private var appsTitle: String {
        switch (PowerPointController.isInstalled, KeynoteController.isInstalled) {
        case (true, true): return "PowerPoint and Keynote found"
        case (true, false): return "PowerPoint found"
        case (false, true): return "Keynote found"
        case (false, false): return "No presentation app found"
        }
    }

    @ViewBuilder private func keyRow(_ keys: String, _ action: String) -> some View {
        GridRow {
            Text(keys).foregroundStyle(Theme.textSoft)
            Text(action).foregroundStyle(Theme.muted)
        }
    }
}

struct CheckCard<Extra: View>: View {
    let ok: Bool
    let title: String
    var detail: String? = nil
    @ViewBuilder var extra: Extra

    init(ok: Bool, title: String, detail: String? = nil, @ViewBuilder extra: () -> Extra = { EmptyView() }) {
        self.ok = ok; self.title = title; self.detail = detail; self.extra = extra()
    }

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: ok ? "checkmark" : "xmark")
                .font(.system(size: 11, weight: .heavy))
                .foregroundStyle(ok ? Theme.ok : Theme.liveSoft)
                .frame(width: 26, height: 26)
                .background(Circle().fill(ok ? Theme.okBg : Theme.liveBg))
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.system(size: 14, weight: .semibold))
                if let detail { Text(detail).font(.system(size: 12)).foregroundStyle(Theme.muted) }
                extra
            }
            Spacer(minLength: 0)
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.border))
    }
}

struct WarningCard: View {
    let title: String
    let detail: String
    let action: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Text("!").font(.system(size: 14, weight: .bold)).foregroundStyle(Theme.warnStrong)
                .frame(width: 26, height: 26)
                .background(Circle().fill(Color(hex: 0x4A3714)))
            VStack(alignment: .leading, spacing: 8) {
                Text(title).font(.system(size: 14, weight: .semibold))
                Text(detail).font(.system(size: 12)).foregroundStyle(Theme.warnText).fixedSize(horizontal: false, vertical: true)
                Button("Open Accessibility settings", action: action)
                    .buttonStyle(PillButtonStyle(kind: .warning, height: 32))
            }
            Spacer(minLength: 0)
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.warnBg))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Theme.warnBorder))
    }
}

// MARK: - Presenter

struct PresenterView: View {
    @EnvironmentObject private var model: PresenterModel
    @ObservedObject private var coach = CoachModel.shared

    var body: some View {
        VStack(spacing: 0) {
            TopBar()
            if let notice = model.notice {
                NoticeBar(text: notice)
            }
            HSplitView {
                SlidesColumn()
                    .frame(minWidth: 320, idealWidth: 640, maxWidth: .infinity, maxHeight: .infinity)
                NotesPanel()
                    .padding(.vertical, 20).padding(.trailing, 20).padding(.leading, 4)
                    .frame(minWidth: 300, idealWidth: 520, maxWidth: .infinity, maxHeight: .infinity)
            }
            .background(Theme.ground)
            Filmstrip()
                .frame(height: 118)
        }
        .sheet(isPresented: $coach.showingReport) {
            if let report = coach.lastReport {
                TalkReportView(report: report).environmentObject(model).preferredColorScheme(Theme.colorScheme)
            }
        }
    }
}

struct NoticeBar: View {
    @EnvironmentObject private var model: PresenterModel
    let text: String

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Text("!").font(.system(size: 12, weight: .bold)).foregroundStyle(Theme.warnStrong)
                .frame(width: 20, height: 20).background(Circle().fill(Color(hex: 0x4A3714)))
            Text(text).font(.system(size: 13)).foregroundStyle(Theme.warnText)
                .frame(maxWidth: .infinity, alignment: .leading)
            if model.showAccessibilityHelp {
                Button("Open Accessibility settings") { PowerPointController.openAccessibilitySettings() }
                    .buttonStyle(PillButtonStyle(kind: .warning, height: 30))
            }
            Button { model.notice = nil; model.showAccessibilityHelp = false } label: {
                Image(systemName: "xmark").font(.system(size: 11, weight: .bold))
            }
            .buttonStyle(.plain).foregroundStyle(Theme.warnText)
            .help("Dismiss")
        }
        .padding(.horizontal, 20).padding(.vertical, 10)
        .background(Theme.warnBg)
        .overlay(alignment: .bottom) { Rectangle().fill(Theme.warnBorder).frame(height: 1) }
    }
}

// MARK: Top bar

struct TopBar: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        // With captions when they fit; otherwise the camera and Stage tools buttons show icons only
        // (tooltips stay), so nothing is squeezed or cut off at the window's edge.
        ViewThatFits(in: .horizontal) {
            bar.environment(\.iconsOnly, false)
            bar.environment(\.iconsOnly, true)
        }
        .padding(.leading, 80).padding(.trailing, 16).padding(.top, 10).padding(.bottom, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.bar)
        .overlay(alignment: .bottom) { Rectangle().fill(Theme.border).frame(height: 1) }
    }

    private var bar: some View {
        HStack(spacing: 9) {
            ShowControl()
            Rectangle().fill(Theme.border).frame(width: 1, height: 30)
            Transport()
            PointerToolGroup()
            BlackScreenButton()
            Spacer(minLength: 8)
            TimerCluster()
            CameraControls()
            StageToolsButton()
        }
    }
}

/// Start / End plus the live badge, deck name and where the slides are.
struct ShowControl: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        HStack(spacing: 12) {
            if model.phase == .presenting {
                Button { model.endShowButton() } label: {
                    Label("End show", systemImage: "stop.fill").fixedSize()
                }
                .buttonStyle(PillButtonStyle(kind: .danger))
                .help("End the slideshow (Esc)")
                Button { model.stepOut() } label: {
                    Image(systemName: "macwindow.on.rectangle")
                }
                .buttonStyle(IconButtonStyle())
                .accessibilityLabel("Show something else")
                .help("Take the slides off the audience display so a browser or app there is what you share. The timer keeps running (⇧⌘S)")
            } else if model.steppedOut {
                Button { model.backToSlides() } label: {
                    Label("Resume", systemImage: "play.fill").fixedSize()
                }
                .buttonStyle(PillButtonStyle(kind: .accent))
                .help("Start the slideshow again on slide \(model.steppedOutAt ?? 1) (⌘↩)")
                Button { model.endShowButton() } label: {
                    Image(systemName: "stop.fill")
                }
                .buttonStyle(IconButtonStyle())
                .accessibilityLabel("End show")
                .help("End the talk and stop the timer (Esc)")
            } else {
                Button { model.requestStart() } label: {
                    Label("Start slideshow", systemImage: "play.fill").fixedSize()
                }
                .buttonStyle(PillButtonStyle(kind: .accent))
                .disabled(!model.canStart)
                .help("Start the slideshow on the audience display (⌘↩)")
            }

            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    if model.phase == .presenting {
                        HStack(spacing: 5) {
                            Circle().fill(Theme.live).frame(width: 7, height: 7)
                            Text("LIVE").font(.system(size: 10, weight: .heavy)).tracking(1)
                        }
                        .foregroundStyle(Theme.liveSoft)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(Theme.liveBg))
                        .fixedSize()
                    }
                    if CoachModel.shared.rehearsalMode {
                        Text("REHEARSAL").font(.system(size: 10, weight: .heavy)).tracking(1)
                            .foregroundStyle(Theme.warnStrong)
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(Capsule().fill(Color(hex: 0x4A3714)))
                            .fixedSize()
                            .help("Rehearsal mode: a timing report opens when the show ends")
                    }
                    Text(model.deckName).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                }
                if model.phase == .presenting {
                    Text("Slides on \(model.presentingOn) · \(model.audienceFullScreen ? "full screen" : "in a window")")
                        .font(.system(size: 12)).foregroundStyle(Theme.muted).lineLimit(1)
                } else if model.steppedOut {
                    Text("Stepped out")
                        .font(.system(size: 12)).foregroundStyle(Theme.muted).lineLimit(1)
                } else {
                    AudienceDisplayPicker(style: .inline)
                }
            }
            .frame(minWidth: 110, maxWidth: 180, alignment: .leading)
            .layoutPriority(-1)
        }
    }
}

struct AudienceDisplayPicker: View {
    enum Style { case inline, field }
    @EnvironmentObject private var model: PresenterModel
    let style: Style

    var body: some View {
        Menu {
            ForEach(NSScreen.screens, id: \.localizedName) { screen in
                Button {
                    model.audienceScreenName = screen.localizedName
                } label: {
                    if screen.localizedName == model.audienceScreen().localizedName {
                        Label(screen.localizedName, systemImage: "checkmark")
                    } else {
                        Text(screen.localizedName)
                    }
                }
            }
            if NSScreen.screens.count > 1 {
                Divider()
                Toggle("Ask Before Each Slideshow", isOn: $model.askForDisplay)
            }
        } label: {
            HStack(spacing: 5) {
                if style == .inline { Text("Slides on").foregroundStyle(Theme.muted).fixedSize() }
                Text(model.audienceScreen().localizedName).foregroundStyle(style == .inline ? Theme.textSoft : Theme.text)
                    .lineLimit(1).truncationMode(.tail)
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .bold)).foregroundStyle(Theme.muted)
                if style == .field { Spacer(minLength: 0) }
            }
            .font(.system(size: style == .inline ? 12 : 13, weight: style == .inline ? .regular : .medium))
            .padding(.horizontal, style == .field ? 10 : 0)
            .frame(height: style == .field ? 34 : nil)
            .background(style == .field ? RoundedRectangle(cornerRadius: 9).fill(Theme.ground) : nil)
            .overlay(style == .field ? RoundedRectangle(cornerRadius: 9).strokeBorder(Theme.borderStrong) : nil)
            .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize(horizontal: false, vertical: true)
        .disabled(model.phase == .presenting)
        .help("Which display the audience sees")
    }
}

struct Transport: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        HStack(spacing: 6) {
            Button { model.previous() } label: { Image(systemName: "chevron.left") }
                .buttonStyle(IconButtonStyle())
                .help("Previous (← or Page Up)")
            HStack(alignment: .firstTextBaseline, spacing: 0) {
                Text("\(model.visiblePosition)").font(Theme.numeral(24))
                Text(" / \(model.visibleCount)").font(Theme.numeral(16, .medium)).foregroundStyle(Theme.dim)
            }
            .frame(minWidth: 62)
            Button { model.next() } label: { Image(systemName: "chevron.right") }
                .buttonStyle(IconButtonStyle(filled: true))
                .help("Next (→, Space or Page Down)")
        }
    }
}

struct PointerToolGroup: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        SegmentGroup {
            ForEach(PointerTool.allCases) { tool in
                Button { model.tool = tool } label: { Image(systemName: tool == .laser ? model.laserStyle.symbol : tool.symbol) }
                    .buttonStyle(SegmentButtonStyle(isOn: model.tool == tool, onColor: color(for: tool), width: 34))
                    .help(tool.label)
                    .accessibilityLabel(tool.label)
            }
            Rectangle().fill(Theme.borderStrong).frame(width: 1, height: 18).padding(.horizontal, 2)
            EffectsMenu()
            Button { model.clearDrawings() } label: { Image(systemName: "eraser") }
                .buttonStyle(SegmentButtonStyle(isOn: false, width: 34))
                .disabled(model.strokes.isEmpty && model.stamps.isEmpty)
                .help("Clear drawings and stamps (E)")
        }
    }

    private func color(for tool: PointerTool) -> Color {
        switch tool {
        case .none: return Theme.text
        case .laser: return Theme.laser
        case .stamp: return model.stampKind.color
        default: return Theme.accent
        }
    }
}

/// Laser style, stamp choice and reactions.
struct EffectsMenu: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        Menu {
            Section("Laser style") {
                ForEach(LaserStyle.allCases) { style in
                    Button { model.laserStyle = style; model.tool = .laser } label: {
                        Label(style.label, systemImage: model.laserStyle == style ? "checkmark" : style.symbol)
                    }
                }
            }
            Section("Stamp (T, then click the slide)") {
                ForEach(StampKind.allCases) { kind in
                    Button { model.stampKind = kind; model.tool = .stamp } label: {
                        Label(kind.label, systemImage: model.stampKind == kind ? "checkmark" : kind.symbol)
                    }
                }
            }
            Section("Reactions on the audience screen") {
                ForEach(ReactionKind.allCases) { kind in
                    Button { model.fireReaction(kind) } label: { Label(kind.label, systemImage: kind.symbol) }
                }
            }
        } label: {
            Image(systemName: "sparkles")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Theme.muted)
                .frame(width: 30, height: 30)
                .contentShape(Rectangle())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Laser styles, stamps and reactions (confetti 1, applause 2, hearts 3)")
    }
}

struct BlackScreenButton: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        Button { model.toggleBlack() } label: {
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 3).fill(Color.black).frame(width: 16, height: 11)
                    .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(Color(hex: 0x5A616E)))
                if model.audience == .black { Text("Black on").fixedSize() }
                Keycap(key: "B")
            }
        }
        .buttonStyle(PillButtonStyle(kind: model.audience == .black ? .warning : .normal))
        .disabled(model.phase != .presenting)
        .help("Black out the audience screen (B). White screen: W")
    }
}

struct TimerCluster: View {
    @EnvironmentObject private var model: PresenterModel
    @ObservedObject private var coach = CoachModel.shared

    var body: some View {
        TimelineView(.periodic(from: .now, by: (coach.cueFlashUntil ?? .distantPast) > Date() ? 0.5 : 1)) { context in
            let elapsed = model.elapsed(at: context.date)
            let target = model.targetSeconds
            let look = model.timerLook(at: context.date)
            let over = look == .over
            let paused = look == .paused
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(Self.format(elapsed))
                            .font(Theme.numeral(26))
                            .fixedSize()
                            .foregroundStyle(over ? Theme.liveSoft : paused ? Theme.muted : Theme.text)
                            .help(paused && elapsed > 0 ? "Paused. The next slideshow starts the timer from 0:00." : "")
                        TargetMenu()
                    }
                    if target > 0 {
                        GeometryReader { geo in
                            ZStack(alignment: .leading) {
                                Capsule().fill(Theme.border)
                                Capsule().fill(over ? Theme.live : paused ? Theme.muted.opacity(0.5) : Theme.accent)
                                    .frame(width: geo.size.width * min(elapsed / target, 1))
                            }
                        }
                        .frame(height: 5)
                    }
                }
                .frame(width: 126)
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 8)
                    .fill(Theme.accent.opacity((coach.cueFlashUntil ?? .distantPast) > context.date
                        ? (Int(context.date.timeIntervalSince1970 * 2) % 2 == 0 ? 0.28 : 0.1) : 0)))

                VStack(alignment: .trailing, spacing: 2) {
                    Text(context.date, format: .dateTime.hour().minute())
                        .font(Theme.numeral(18, .medium)).foregroundStyle(Theme.textSoft)
                        .fixedSize()
                    if CoachModel.shared.paceDelta(at: context.date) != nil {
                        PaceLabel(date: context.date)
                    } else if target > 0, !paused {
                        let end = context.date.addingTimeInterval(max(target - elapsed, 0))
                        Text(over ? "over time" : "ends ~\(end.formatted(.dateTime.hour().minute()))")
                            .font(.system(size: 11)).foregroundStyle(over ? Theme.liveSoft : Theme.muted)
                            .fixedSize()
                    }
                }

                Button { model.toggleTimer() } label: {
                    Image(systemName: model.timerStartedAt == nil ? "play.fill" : "pause.fill")
                }
                .buttonStyle(IconButtonStyle())
                .help(model.timerStartedAt == nil ? "Start the timer" : "Pause the timer")
                .contextMenu { Button("Reset timer") { model.resetTimer() } }
            }
        }
    }

    static func format(_ t: TimeInterval) -> String {
        let s = Int(t)
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
                         : String(format: "%02d:%02d", s / 60, s % 60)
    }
}

/// "of 20:00": click to pick the planned length of the talk.
struct TargetMenu: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        Menu {
            ForEach([5, 10, 15, 20, 30, 45, 60, 90], id: \.self) { m in
                Button { model.targetMinutes = m } label: {
                    if m == model.targetMinutes { Label("\(m) minutes", systemImage: "checkmark") } else { Text("\(m) minutes") }
                }
            }
            Divider()
            Button("No target") { model.targetMinutes = 0 }
            Divider()
            Button("Reset timer") { model.resetTimer() }
        } label: {
            Text(model.targetMinutes > 0 ? "of \(model.targetMinutes):00" : "no target")
                .font(.system(size: 12)).foregroundStyle(Theme.muted)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Planned length of the talk")
    }
}

// MARK: Slides

struct SlidesColumn: View {
    @EnvironmentObject private var model: PresenterModel
    /// How much of the column's height the current slide may take; drag the handle under it to change.
    @AppStorage("currentSlideShare") private var share: Double = SlideSplit.standard
    @State private var dragStart: Double?

    var body: some View {
        GeometryReader { geo in
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 12) {
                    CapsLabel(model.phase == .presenting ? "On screen" : "Preview",
                              color: model.phase == .presenting ? Theme.accent : Theme.muted)
                    SlideBudgetChip()
                    Spacer()
                    if SpeechHub.shared.listening {
                        SpeechStatsLabel()
                    } else {
                        Text(hint).font(.system(size: 12)).foregroundStyle(Theme.muted).lineLimit(1)
                    }
                }
                SlideImage(slide: model.currentSlide, label: "Current", interactive: true,
                           ring: model.phase == .presenting ? Theme.accent : Theme.borderStrong)
                    .overlay(alignment: .topTrailing) {
                        if model.audience != .normal {
                            Text(model.audience == .black ? "Audience sees a black screen" : "Audience sees a white screen")
                                .font(.system(size: 12, weight: .semibold))
                                .padding(.horizontal, 10).padding(.vertical, 5)
                                .background(Capsule().fill(.black.opacity(0.8)))
                                .foregroundStyle(.white)
                                .padding(10)
                        }
                    }
                    // An exact height, not a ceiling: with only a ceiling the column split its height evenly
                    // with the up-next row and the handle seemed to do nothing.
                    .frame(height: SlideSplit.slideHeight(columnHeight: geo.size.height, share: share))

                SlideResizeHandle()
                    .padding(.vertical, -8)
                    .gesture(DragGesture(minimumDistance: 1)
                        .onChanged { drag in
                            let start = dragStart ?? share
                            dragStart = start
                            share = SlideSplit.share(start: start, dragY: drag.translation.height, height: geo.size.height)
                        }
                        .onEnded { _ in dragStart = nil })
                    .onTapGesture(count: 2) { share = SlideSplit.standard }

                HStack(alignment: .top, spacing: 18) {
                    SlideImage(slide: model.nextSlide, label: "Next", ring: Theme.borderStrong)
                        .frame(maxWidth: geo.size.width * 0.44)
                    VStack(alignment: .leading, spacing: 8) {
                        if let next = model.nextSlide {
                            CapsLabel("Up next · slide \(next.id)")
                            Text(next.title.nilIfEmpty ?? "Slide \(next.id)")
                                .font(.system(size: 18, weight: .semibold)).lineLimit(2)
                            if let firstLine = NoteCues.plain(next.notes).split(separator: "\n").first {
                                Text(firstLine).font(.system(size: 13)).foregroundStyle(Theme.muted).lineLimit(3)
                            }
                        } else {
                            CapsLabel("Up next")
                            Text("End of slideshow").font(.system(size: 18, weight: .semibold))
                        }
                    }
                    .padding(.top, 4)
                }
                .frame(maxHeight: .infinity, alignment: .top)
            }
            .padding(20)
        }
    }

    private var hint: String {
        guard model.phase == .presenting else { return "Arrow keys browse your notes · ⌘↩ starts the show" }
        switch model.tool {
        case .none: return "Pointer · the audience sees your mouse over the slide · L laser · S spotlight · P pen"
        case .laser: return "Laser on · move over the slide to point"
        case .spotlight: return "Spotlight · the audience sees only around your pointer"
        case .magnifier: return "Magnifier · zooms in where you point"
        case .stamp: return "Stamp · click the slide to place \(model.stampKind.label.lowercased())"
        case .pen: return "Pen · drag on the slide to draw"
        case .highlighter: return "Highlighter · drag on the slide to mark"
        }
    }
}

/// The current slide's share of the slides column, set by dragging the handle under it.
enum SlideSplit {
    static let standard = 0.66
    static let range = 0.35...0.9

    /// Height of the column's header, padding and gaps, which the slides don't get.
    static let chrome = 90.0

    /// The current slide's height in a column this tall.
    static func slideHeight(columnHeight: Double, share: Double) -> Double {
        let clamped = min(max(share, range.lowerBound), range.upperBound)
        return max(60, (columnHeight - chrome) * clamped)
    }

    static func share(start: Double, dragY: Double, height: Double) -> Double {
        guard height > 0 else { return start }
        return min(max(start + dragY / max(height - chrome, 1), range.lowerBound), range.upperBound)
    }
}

/// A thin grip under the current slide: drag down to make the slide bigger, up to give the next slide room.
struct SlideResizeHandle: View {
    @State private var hovering = false

    var body: some View {
        ZStack {
            Color.clear.frame(height: 14).contentShape(Rectangle())
            Capsule().fill(hovering ? Theme.textSoft : Theme.borderStrong).frame(width: 44, height: 4)
        }
        .frame(maxWidth: .infinity)
        .onHover { inside in
            hovering = inside
            if inside { NSCursor.resizeUpDown.push() } else { NSCursor.pop() }
        }
        .help("Drag to make the current slide bigger or smaller · double-click to reset. Drag the divider beside the notes to make it wider.")
        .accessibilityLabel("Resize the current slide")
    }
}

struct SlideImage: View {
    @EnvironmentObject private var model: PresenterModel
    let slide: SlideInfo?
    let label: String
    var interactive = false
    var ring: Color = Theme.borderStrong

    var body: some View {
        GeometryReader { geo in
            let size = fitted(in: geo.size)
            ZStack {
                if let slide, let img = model.image(for: slide.id, width: size.width) {
                    Image(nsImage: img).resizable().interpolation(.high)
                } else {
                    Rectangle().fill(Theme.raised)
                    Text(slide.map { $0.title.nilIfEmpty ?? "Slide \($0.id)" } ?? "End of slideshow")
                        .font(.system(size: 16, weight: .medium)).foregroundStyle(Theme.muted)
                        .multilineTextAlignment(.center).padding()
                }
            }
            .overlay {
                if interactive {
                    AnnotationCanvas()
                    switch model.tool.slideInput {
                    case .hoverOnly: PointerHoverInput()
                    case .full: AnnotationInput()
                    }
                }
            }
            .frame(width: size.width, height: size.height)
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(ring, lineWidth: ring == Theme.accent ? 2 : 1))
            .shadow(color: .black.opacity(0.45), radius: 14, y: 8)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .accessibilityLabel("\(label) slide")
        }
        .aspectRatio(model.slideAspect, contentMode: .fit)
    }

    private func fitted(in size: CGSize) -> CGSize {
        let aspect = model.slideAspect
        let w = min(size.width, size.height * aspect)
        return CGSize(width: max(w, 1), height: max(w / aspect, 1))
    }
}

// MARK: Notes

struct NotesPanel: View {
    @EnvironmentObject private var model: PresenterModel
    @AppStorage(NotesFont.key) private var fontSize: Double = 26
    @AppStorage("notesTab") private var tab = "notes"
    @State private var editingDraft = false
    @State private var draftText = ""

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                SegmentGroup {
                    Button("Notes") { tab = "notes" }.buttonStyle(SegmentButtonStyle(isOn: tab == "notes", width: nil))
                    Button("Prompter") { tab = "prompter" }.buttonStyle(SegmentButtonStyle(isOn: tab == "prompter", width: nil))
                        .help("Your notes as a scrolling script that follows your voice")
                }
                Spacer()
                if tab == "notes" {
                HStack(spacing: 6) {
                    NoteWriterMenu()
                        .padding(.trailing, 4)
                    Button("A−") { fontSize = max(fontSize - 2, NotesFont.range.lowerBound) }
                        .buttonStyle(IconButtonStyle(tint: Theme.textSoft, size: 32))
                        .help("Smaller notes (⌘−)")
                    Text("\(Int(fontSize)) pt").font(.system(size: 12, weight: .medium)).foregroundStyle(Theme.muted)
                        .frame(minWidth: 40)
                    Button("A+") { fontSize = min(fontSize + 2, NotesFont.range.upperBound) }
                        .buttonStyle(IconButtonStyle(tint: Theme.textSoft, size: 32))
                        .help("Bigger notes (⌘+)")
                }
                }
            }
            .padding(.horizontal, 14).padding(.vertical, 10)
            .overlay(alignment: .bottom) { Rectangle().fill(Theme.border).frame(height: 1) }

            if tab == "prompter" {
                PrompterView(compact: true)
            } else {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        if let slide = model.currentSlide {
                            HStack(alignment: .top) {
                                VStack(alignment: .leading, spacing: 4) {
                                    CapsLabel("Slide \(slide.id)")
                                    if !slide.title.isEmpty {
                                        Text(slide.title.replacingOccurrences(of: "\n", with: " "))
                                            .font(.system(size: 16, weight: .semibold)).foregroundStyle(Theme.textSoft)
                                    }
                                }
                                Spacer(minLength: 8)
                                if !slide.draft && !editingDraft && !slide.notes.isEmpty {
                                    Button { draftText = slide.notes; editingDraft = true } label: { Image(systemName: "pencil") }
                                        .buttonStyle(IconButtonStyle(tint: Theme.textSoft, size: 28))
                                        .help("Edit these notes. Your changes stay in Bema until you save them to the deck.")
                                        .accessibilityLabel("Edit notes")
                                }
                            }
                            if let w = model.noteWriting, w.total > 1, w.slide != slide.id {
                                WritingRow(progress: w)
                            }
                            if slide.draft || editingDraft {
                                DraftBar(slide: slide.id, editing: $editingDraft, text: $draftText)
                            }
                            if editingDraft {
                                TextEditor(text: $draftText)
                                    .font(.system(size: max(14, fontSize * 0.7)))
                                    .scrollContentBackground(.hidden)
                                    .padding(8)
                                    .frame(minHeight: 200)
                                    .background(RoundedRectangle(cornerRadius: 10).fill(Theme.ground))
                                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Theme.borderStrong))
                            } else if !slide.notes.isEmpty {
                                Text(NoteCues.attributed(slide.notes, fontSize: fontSize))
                                    .font(.system(size: fontSize))
                                    .lineSpacing(fontSize * 0.3)
                                    .foregroundStyle(Theme.textBright)
                                    .textSelection(.enabled)
                                    .multilineTextAlignment(slide.notes.isRightToLeft ? .trailing : .leading)
                                    .frame(maxWidth: .infinity, alignment: slide.notes.isRightToLeft ? .topTrailing : .topLeading)
                                    .environment(\.layoutDirection, slide.notes.isRightToLeft ? .rightToLeft : .leftToRight)
                            } else {
                                NoNotesView(slide: slide.id, fontSize: fontSize) { draftText = ""; editingDraft = true }
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .topLeading)
                    .padding(.horizontal, 24).padding(.vertical, 20)
                    .id("top")
                }
                .onChange(of: model.current) { proxy.scrollTo("top", anchor: .top); editingDraft = false }
            }

            if let next = model.nextSlide, let line = NoteCues.plain(next.notes).split(separator: "\n").first {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    CapsLabel("Next notes", color: Theme.dim)
                    Text(line).font(.system(size: 13)).foregroundStyle(Theme.muted).lineLimit(1)
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 24).padding(.vertical, 12)
                .background(Theme.bar)
                .overlay(alignment: .top) { Rectangle().fill(Theme.border).frame(height: 1) }
            }
            }
        }
        .background(Theme.panel)
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Theme.border))
    }
}

// MARK: Filmstrip

struct Filmstrip: View {
    @EnvironmentObject private var model: PresenterModel

    var body: some View {
        // Worked out once per redraw, not once per thumbnail: it reads every slide's notes.
        let budgets = CoachModel.shared.budgets()
        ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(alignment: .top, spacing: 14) {
                    ForEach(model.slides) { slide in
                        Thumbnail(slide: slide, isCurrent: slide.id == model.current, budget: budgets[slide.id])
                            .id(slide.id)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
            }
            .onChange(of: model.current) {
                withAnimation(.easeInOut(duration: 0.2)) { proxy.scrollTo(model.current, anchor: .center) }
            }
        }
        .background(Theme.bar)
        .overlay(alignment: .top) { Rectangle().fill(Theme.border).frame(height: 1) }
    }
}

struct Thumbnail: View {
    @EnvironmentObject private var model: PresenterModel
    let slide: SlideInfo
    let isCurrent: Bool
    var budget: Double?
    @State private var hovering = false

    var body: some View {
        let height: CGFloat = 66
        let width = height * model.slideAspect
        Button { model.goTo(slide.id) } label: {
            VStack(alignment: .leading, spacing: 6) {
                ZStack {
                    if let img = model.image(for: slide.id, width: width) {
                        Image(nsImage: img).resizable()
                    } else {
                        Rectangle().fill(Theme.raised)
                        Text(slide.title).font(.system(size: 8)).foregroundStyle(Theme.muted).lineLimit(3).padding(4)
                    }
                }
                .frame(width: width, height: height)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .overlay(RoundedRectangle(cornerRadius: 7)
                    .strokeBorder(isCurrent ? Theme.accent : (hovering ? Theme.dim : Theme.borderStrong),
                                  lineWidth: isCurrent ? 3 : 1))
                Text(slide.hidden ? "\(slide.id) · hidden" : (budget.map { "\(slide.id) · \(formatDuration($0))" } ?? "\(slide.id)"))
                    .font(.system(size: 12, weight: isCurrent ? .bold : .medium))
                    .foregroundStyle(isCurrent ? Theme.accent : Theme.muted)
            }
            .opacity(slide.hidden ? 0.45 : 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(slide.hidden ? "\(slide.title) (hidden slide)" : slide.title)
    }
}

extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }

    /// True when the first letter is Hebrew or Arabic, so notes in those languages align right.
    var isRightToLeft: Bool {
        for scalar in unicodeScalars where CharacterSet.letters.contains(scalar) {
            return (0x0590...0x08FF).contains(scalar.value) || (0xFB1D...0xFDFF).contains(scalar.value) || (0xFE70...0xFEFF).contains(scalar.value)
        }
        return false
    }
}
