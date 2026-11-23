import Foundation

struct SlideInfo: Identifiable, Hashable {
    /// 1-based slide number, as PowerPoint numbers slides.
    let id: Int
    let title: String
    var notes: String
    let hidden: Bool
    /// The notes are a draft Bema wrote (kept by Bema), not notes from the deck.
    var draft = false
}

enum PPTXError: LocalizedError {
    case unreadable(String)
    /// The file is there but macOS won't let Bema open it, e.g. an attachment PowerPoint opened from
    /// Outlook's own folder, which macOS keeps from other apps.
    case blocked

    var errorDescription: String? {
        switch self {
        case .unreadable(let why): return "Couldn't read the presentation: \(why)"
        case .blocked: return "Couldn't read the presentation: macOS doesn't let Bema open this file."
        }
    }
}

/// Reads slide titles and speaker notes straight from the .pptx (a zip of XML files),
/// without going through PowerPoint.
enum PPTXReader {
    private static let relNS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

    static func read(url: URL) throws -> [SlideInfo] {
        let fm = FileManager.default
        if isBlocked(url) { throw PPTXError.blocked }
        let tmp = fm.temporaryDirectory.appendingPathComponent("Bema-\(UUID().uuidString)")
        try fm.createDirectory(at: tmp, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: tmp) }

        try unzip(url, into: tmp, entries: [
            "ppt/presentation.xml",
            "ppt/_rels/presentation.xml.rels",
            "ppt/slides/*",
            "ppt/notesSlides/*",
        ])

        let ppt = tmp.appendingPathComponent("ppt")
        let presentationXML = ppt.appendingPathComponent("presentation.xml")
        guard fm.fileExists(atPath: presentationXML.path) else {
            throw PPTXError.unreadable("it doesn't look like a PowerPoint (.pptx) file.")
        }

        let presRels = try relationships(ppt.appendingPathComponent("_rels/presentation.xml.rels"), base: ppt)
        let presDoc = try XMLDocument(contentsOf: presentationXML)
        let slideIds = try presDoc.nodes(forXPath: "//*[local-name()='sldIdLst']/*[local-name()='sldId']")

        var slides: [SlideInfo] = []
        for (index, node) in slideIds.enumerated() {
            guard let el = node as? XMLElement,
                  let rid = el.attribute(forLocalName: "id", uri: relNS)?.stringValue
                    ?? el.attribute(forName: "r:id")?.stringValue,
                  let slideURL = presRels[rid],
                  let slideDoc = try? XMLDocument(contentsOf: slideURL)
            else { continue }

            let hidden = slideDoc.rootElement()?.attribute(forName: "show")?.stringValue == "0"
            let title = shapeText(in: slideDoc, placeholderTypes: ["title", "ctrTitle"])
                ?? firstShapeText(in: slideDoc)
                ?? ""

            var notes = ""
            let slideRelsURL = slideURL.deletingLastPathComponent()
                .appendingPathComponent("_rels/\(slideURL.lastPathComponent).rels")
            if let rels = try? relationships(slideRelsURL, base: slideURL.deletingLastPathComponent(), typeSuffix: "/notesSlide"),
               let notesURL = rels.values.first,
               let notesDoc = try? XMLDocument(contentsOf: notesURL) {
                notes = shapeText(in: notesDoc, placeholderTypes: ["body"]) ?? ""
            }

            slides.append(SlideInfo(id: index + 1, title: title, notes: notes, hidden: hidden))
        }

        if slides.isEmpty { throw PPTXError.unreadable("no slides were found.") }
        return slides
    }

    /// True when the file exists but can't be opened for reading (no permission, or macOS keeps it from
    /// Bema). Then the slides have to be read through the app that has it open.
    static func isBlocked(_ url: URL) -> Bool {
        guard FileManager.default.fileExists(atPath: url.path) else { return false }
        let fd = Darwin.open(url.path, O_RDONLY)
        if fd >= 0 { Darwin.close(fd); return false }
        return errno == EPERM || errno == EACCES
    }

    // MARK: - Helpers

    private static func unzip(_ archive: URL, into dir: URL, entries: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        p.arguments = ["-qq", "-o", archive.path] + entries + ["-d", dir.path]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try p.run()
        p.waitUntilExit()
        // Exit code 11 means one of the patterns matched nothing (e.g. a deck with no notes); that's fine.
        if p.terminationStatus == 9 {
            throw PPTXError.unreadable("it doesn't look like a PowerPoint (.pptx) file.")
        }
        if p.terminationStatus != 0 && p.terminationStatus != 11 {
            throw PPTXError.unreadable("unzip failed with code \(p.terminationStatus).")
        }
    }

    /// Relationship Id -> resolved file URL.
    private static func relationships(_ relsURL: URL, base: URL, typeSuffix: String? = nil) throws -> [String: URL] {
        let doc = try XMLDocument(contentsOf: relsURL)
        var map: [String: URL] = [:]
        for node in try doc.nodes(forXPath: "//*[local-name()='Relationship']") {
            guard let el = node as? XMLElement,
                  let id = el.attribute(forName: "Id")?.stringValue,
                  let target = el.attribute(forName: "Target")?.stringValue,
                  el.attribute(forName: "TargetMode")?.stringValue != "External"
            else { continue }
            if let suffix = typeSuffix, !(el.attribute(forName: "Type")?.stringValue ?? "").hasSuffix(suffix) { continue }
            map[id] = URL(fileURLWithPath: target, relativeTo: base).standardizedFileURL
        }
        return map
    }

    private static func shapeText(in doc: XMLDocument, placeholderTypes: Set<String>) -> String? {
        guard let shapes = try? doc.nodes(forXPath: "//*[local-name()='sp']") else { return nil }
        for shape in shapes {
            guard let ph = (try? shape.nodes(forXPath: "./*[local-name()='nvSpPr']/*[local-name()='nvPr']/*[local-name()='ph']"))?.first as? XMLElement,
                  let type = ph.attribute(forName: "type")?.stringValue,
                  placeholderTypes.contains(type)
            else { continue }
            let text = paragraphs(of: shape)
            if !text.isEmpty { return text }
        }
        return nil
    }

    private static func firstShapeText(in doc: XMLDocument) -> String? {
        guard let shapes = try? doc.nodes(forXPath: "//*[local-name()='sp']") else { return nil }
        for shape in shapes {
            let text = paragraphs(of: shape)
            if !text.isEmpty { return text.components(separatedBy: "\n").first }
        }
        return nil
    }

    /// Text of a shape, one line per paragraph, keeping line breaks inside paragraphs.
    private static func paragraphs(of shape: XMLNode) -> String {
        guard let paras = try? shape.nodes(forXPath: ".//*[local-name()='txBody']/*[local-name()='p']") else { return "" }
        let lines = paras.map { para -> String in
            var line = ""
            for child in para.children ?? [] {
                switch child.localName {
                case "r", "fld":
                    if let t = (try? child.nodes(forXPath: "./*[local-name()='t']"))?.first { line += t.stringValue ?? "" }
                case "br":
                    line += "\n"
                default:
                    break
                }
            }
            return line
        }
        return lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
