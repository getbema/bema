import XCTest
@testable import Bema

final class PPTXReaderTests: XCTestCase {
    private var sample: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("../../Samples/Sample.pptx").standardizedFileURL
    }

    func testReadsTheSampleDeck() throws {
        XCTAssertFalse(PPTXReader.isBlocked(sample))
        XCTAssertFalse(try PPTXReader.read(url: sample).isEmpty)
    }

    /// A file Bema may not open (like an attachment in Outlook's folder) is reported as blocked, so the
    /// notes are read through PowerPoint instead of failing with "doesn't look like a PowerPoint file".
    func testAFileBemaCantOpenIsBlocked() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("bema-blocked-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let deck = dir.appendingPathComponent("Deck[12].pptx")
        try FileManager.default.copyItem(at: sample, to: deck)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: deck.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: deck.path) }

        XCTAssertTrue(PPTXReader.isBlocked(deck))
        XCTAssertThrowsError(try PPTXReader.read(url: deck)) { error in
            guard case PPTXError.blocked = error else { return XCTFail("expected .blocked, got \(error)") }
        }
    }

    func testAMissingFileIsNotBlocked() {
        XCTAssertFalse(PPTXReader.isBlocked(URL(fileURLWithPath: "/nonexistent/Deck.pptx")))
    }

    func testABracketedFileNameReads() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("bema-brackets-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let deck = dir.appendingPathComponent("Deck[81].pptx")
        try FileManager.default.copyItem(at: sample, to: deck)
        XCTAssertFalse(try PPTXReader.read(url: deck).isEmpty)
    }
}
