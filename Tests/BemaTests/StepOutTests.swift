import AppKit
import SwiftUI
import XCTest
@testable import Bema

/// A slideshow app that only records what Bema asked of it.
private final class FakeSlideshow: SlideshowController {
    var appName = "Fake"
    var bundleID = Bundle.main.bundleIdentifier ?? "com.apple.dt.xctest.tool"
    var supportsScreenStates = true
    var ended = 0
    func open(_ url: URL) async throws {}
    func readSlides() async throws -> [SlideInfo]? { nil }
    func exportPDF(to pdfURL: URL) async throws {}
    func writeNotes(_ notes: [Int: String]) async throws -> Set<Int> { [] }
    func startShow(on screen: NSScreen, from slide: Int, fullScreen: Bool) async throws -> Bool { true }
    func next() async throws {}
    func previous() async throws {}
    func goTo(_ slide: Int) async throws {}
    func setScreen(_ state: PowerPointController.ScreenState) async throws {}
    func endShow() async throws { ended += 1 }
    func status() async throws -> ShowStatus { .notRunning }
    func activePresentationURL() async -> URL? { nil }
}

@MainActor
final class StepOutTests: XCTestCase {
    func testSteppingOutTakesTheSlidesAwayButKeepsTheTalkGoing() async {
        let model = PresenterModel()
        model.app = FakeSlideshow()
        model.phase = .presenting
        model.startTimerForShow()

        await model.stepOutOfShow()

        XCTAssertTrue(model.steppedOut)
        XCTAssertEqual(model.steppedOutAt, 1, "Bema remembers the slide to come back to")
        XCTAssertNotEqual(model.phase, .presenting, "the slideshow is off the audience display")
        XCTAssertNotNil(model.timerStartedAt, "the timer keeps running while something else is shown")
        XCTAssertFalse(model.timerFromEndedShow, "so Back to slides carries on instead of starting at 0:00")
        XCTAssertTrue(model.timerShown)
    }

    func testEndingTheShowWhileSteppedOutStopsTheTimer() async {
        let model = PresenterModel()
        model.app = FakeSlideshow()
        model.phase = .presenting
        model.startTimerForShow()
        await model.stepOutOfShow()

        await model.endShow()

        XCTAssertFalse(model.steppedOut)
        XCTAssertNil(model.timerStartedAt)
        XCTAssertTrue(model.timerFromEndedShow)
    }

    func testSteppingOutOnlyHappensDuringAShow() async {
        let model = PresenterModel()
        model.app = FakeSlideshow()
        await model.stepOutOfShow()
        XCTAssertFalse(model.steppedOut)
    }
}

/// Writes $TMPDIR/bema-topbar-presenting.png and bema-topbar-stepped-out.png at the narrowest width.
final class StepOutRenderTests: XCTestCase {
    @MainActor func testTheTopBarFitsDuringAShowAndWhileSteppedOut() async throws {
        _ = NSApplication.shared
        let model = PresenterModel()
        model.app = FakeSlideshow()
        model.phase = .presenting
        try render(model, "presenting")
        await model.stepOutOfShow()
        try render(model, "stepped-out")
    }

    @MainActor private func render(_ model: PresenterModel, _ name: String) throws {
        let width = 1320.0
        let view = TopBar().environmentObject(model).frame(width: width).environment(\.colorScheme, .dark)
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        let image = try XCTUnwrap(renderer.nsImage)
        XCTAssertEqual(image.size.width, width, accuracy: 1)
        let png = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation))?.representation(using: .png, properties: [:]))
        try png.write(to: FileManager.default.temporaryDirectory.appendingPathComponent("bema-topbar-\(name).png"))
    }
}
