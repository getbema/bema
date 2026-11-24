import SwiftUI

/// A small Bema in the menu bar: what's on screen, the timer, and the main controls.
struct MenuBarPanel: View {
    @EnvironmentObject private var model: PresenterModel
    @ObservedObject private var prompter = PrompterModel.shared
    @ObservedObject private var loginItem = LoginItem.shared
    @ObservedObject private var updates = Updates.shared
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                BemaMark(size: 22)
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.deckURL == nil ? "No deck open" : model.deckName).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                    Text(status).font(.system(size: 11)).foregroundStyle(Theme.muted)
                }
                Spacer()
                if model.phase == .presenting {
                    Text("LIVE").font(.system(size: 9, weight: .heavy)).tracking(1).foregroundStyle(Theme.liveSoft)
                        .padding(.horizontal, 6).padding(.vertical, 2).background(Capsule().fill(Theme.liveBg))
                }
            }

            if model.phase == .ready || model.phase == .presenting {
                if model.timerShown {
                    TimelineView(.periodic(from: .now, by: 1)) { ctx in
                        HStack(alignment: .firstTextBaseline) {
                            let look = model.timerLook(at: ctx.date)
                            Text(TimerCluster.format(model.elapsed(at: ctx.date))).font(Theme.numeral(24))
                                .foregroundStyle(look == .over ? Theme.liveSoft : look == .paused ? Theme.muted : Theme.text)
                            if model.targetMinutes > 0 { Text("of \(model.targetMinutes):00").font(.system(size: 11)).foregroundStyle(Theme.muted) }
                            Spacer()
                            PaceLabel(date: ctx.date)
                        }
                    }
                }
                HStack(spacing: 8) {
                    Button { model.previous() } label: { Image(systemName: "chevron.left") }.buttonStyle(IconButtonStyle())
                    Text("\(model.visiblePosition) / \(model.visibleCount)").font(Theme.numeral(15, .medium)).frame(maxWidth: .infinity)
                    Button { model.next() } label: { Image(systemName: "chevron.right") }.buttonStyle(IconButtonStyle(filled: true))
                }
                HStack(spacing: 8) {
                    if model.phase == .presenting {
                        Button("End show") { model.endShowButton() }.buttonStyle(PillButtonStyle(kind: .danger, height: 30))
                        Button("Black") { model.toggleBlack() }.buttonStyle(PillButtonStyle(kind: model.audience == .black ? .warning : .normal, height: 30))
                        Button("Step out") { model.stepOut() }.buttonStyle(PillButtonStyle(kind: .normal, height: 30))
                            .help("Show something else on the audience display; the timer keeps running")
                    } else if model.steppedOut {
                        Button("Back to slides") { model.backToSlides() }.buttonStyle(PillButtonStyle(kind: .accent, height: 30))
                        Button("End show") { model.endShowButton() }.buttonStyle(PillButtonStyle(kind: .danger, height: 30))
                    } else {
                        Button("Start slideshow") { model.requestStart() }.buttonStyle(PillButtonStyle(kind: .accent, height: 30))
                            .disabled(!model.canStart)
                    }
                    Button(prompter.running ? "Pause prompter" : "Prompter") { prompter.toggle() }
                        .buttonStyle(PillButtonStyle(kind: .normal, height: 30))
                }
            }

            Divider()
            VStack(alignment: .leading, spacing: 8) {
                Button { showMainWindow() } label: { Label("Show Bema window", systemImage: "macwindow") }
                Button { model.presentOpenDeck() } label: {
                    Label(model.openInPowerPoint.map { "Present “\($0.deletingPathExtension().lastPathComponent)”" } ?? "Present the deck open in PowerPoint or Keynote",
                          systemImage: "play.rectangle")
                }
                .disabled(model.openInPowerPoint == nil)
                Button { openWindow(id: "prompter") } label: { Label("Detached prompter", systemImage: "text.alignleft") }
                Button { prompter.editingOwnScript = true; NSApp.activate(); openWindow(id: "prompter") } label: {
                    Label("Prompter for your own text", systemImage: "doc.plaintext")
                }
                Button { SelfViewWindowRef.opener = openWindow; SelfViewWindowRef.toggle() } label: { Label(CameraModel.shared.selfViewOpen ? "Close self view" : "Self view (only you see it)", systemImage: "person.crop.square") }
                Button { showMainWindow(); model.stageToolsOpen = true } label: {
                    Label("Stage tools: remote, poll, camera…", systemImage: "slider.horizontal.3")
                }
            }
            .buttonStyle(.plain)

            Divider()
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Open at login", isOn: Binding(get: { loginItem.enabled }, set: { loginItem.set($0) }))
                    .toggleStyle(.switch).controlSize(.mini)
                if let error = loginItem.error {
                    Text(error).font(.system(size: 11)).foregroundStyle(Theme.warnStrong).fixedSize(horizontal: false, vertical: true)
                }
                if PowerPointController.isInstalled {
                    Button { model.installPowerPointButton(); showMainWindow() } label: {
                        Label(PowerPointAddin.isInstalled ? "PowerPoint button added" : "Add the Bema button to PowerPoint…",
                              systemImage: PowerPointAddin.isInstalled ? "checkmark.circle" : "plus.rectangle.on.rectangle")
                    }
                    .disabled(PowerPointAddin.isInstalled)
                }
                if updates.isAvailable {
                    Button { updates.checkForUpdates() } label: { Label("Check for updates…", systemImage: "arrow.down.circle") }
                        .disabled(!updates.canCheck)
                }
                Button { NSApp.terminate(nil) } label: { Label("Quit Bema", systemImage: "power") }
                    .keyboardShortcut("q")
            }
            .buttonStyle(.plain)
        }
        .font(.system(size: 13))
        .padding(14)
        .frame(width: 300)
        .background(Theme.panel)
        .foregroundStyle(Theme.text)
        .preferredColorScheme(Theme.colorScheme)
    }

    private func showMainWindow() {
        MainWindowRef.opener = openWindow
        model.showPresenterWindow()
    }

    private var status: String {
        switch model.phase {
        case .presenting: return "Slides on \(model.presentingOn)"
        case .ready: return "Ready to present"
        case .loading(let m): return m
        case .failed: return "Couldn't open the deck"
        case .empty: return "Press ⌃⌥P in PowerPoint or Keynote"
        }
    }
}
