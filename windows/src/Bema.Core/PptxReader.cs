using System.IO.Compression;
using System.Xml.Linq;

namespace Bema.Core;

public sealed class PptxException(string why) : Exception($"Couldn't read the presentation: {why}");

/// <summary>
/// Reads slide titles and speaker notes straight from a .pptx (a zip of XML files), without going
/// through PowerPoint. Same rules as the Mac app's PPTXReader.
/// </summary>
public static class PptxReader
{
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static IReadOnlyList<SlideInfo> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static IReadOnlyList<SlideInfo> Read(Stream stream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new PptxException("it doesn't look like a PowerPoint (.pptx) file."); }
        using (zip)
        {
            var pres = Load(zip, "ppt/presentation.xml") ?? throw new PptxException("it doesn't look like a PowerPoint (.pptx) file.");
            var presRels = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt");
            var slides = new List<SlideInfo>();
            var ids = pres.Descendants().Where(e => e.Name.LocalName == "sldIdLst").Elements().Where(e => e.Name.LocalName == "sldId").ToList();
            for (var index = 0; index < ids.Count; index++)
            {
                var rid = (string?)ids[index].Attribute(XName.Get("id", RelNs));
                if (rid is null || !presRels.TryGetValue(rid, out var slidePath)) continue;
                var slide = Load(zip, slidePath);
                if (slide?.Root is null) continue;
                var hidden = (string?)slide.Root.Attribute("show") == "0";
                var title = ShapeText(slide, ["title", "ctrTitle"]) ?? FirstShapeText(slide) ?? "";
                var notes = "";
                var dir = Path.GetDirectoryName(slidePath)!.Replace('\\', '/');
                var relsPath = $"{dir}/_rels/{Path.GetFileName(slidePath)}.rels";
                var rels = Relationships(zip, relsPath, dir, "/notesSlide");
                if (rels.Count > 0 && Load(zip, rels.Values.First()) is { } notesDoc)
                    notes = ShapeText(notesDoc, ["body"]) ?? "";
                slides.Add(new SlideInfo(index + 1, title, notes, hidden));
            }
            if (slides.Count == 0) throw new PptxException("no slides were found.");
            return slides;
        }
    }

    static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Relationship id to the zip path of its target (internal targets only).</summary>
    static Dictionary<string, string> Relationships(ZipArchive zip, string relsPath, string baseDir, string? typeSuffix = null)
    {
        var map = new Dictionary<string, string>();
        var doc = Load(zip, relsPath);
        if (doc is null) return map;
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var id = (string?)el.Attribute("Id");
            var target = (string?)el.Attribute("Target");
            if (id is null || target is null || (string?)el.Attribute("TargetMode") == "External") continue;
            if (typeSuffix is not null && !((string?)el.Attribute("Type") ?? "").EndsWith(typeSuffix, StringComparison.Ordinal)) continue;
            map[id] = Resolve(baseDir, target);
        }
        return map;
    }

    internal static string Resolve(string baseDir, string target)
    {
        var parts = new List<string>(target.StartsWith('/') ? [] : baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != ".") parts.Add(p);
        }
        return string.Join('/', parts);
    }

    static string? ShapeText(XDocument doc, string[] placeholderTypes)
    {
        foreach (var shape in doc.Descendants().Where(e => e.Name.LocalName == "sp"))
        {
            var ph = shape.Elements().Where(e => e.Name.LocalName == "nvSpPr").Elements().Where(e => e.Name.LocalName == "nvPr")
                .Elements().FirstOrDefault(e => e.Name.LocalName == "ph");
            var type = (string?)ph?.Attribute("type");
            if (type is null || !placeholderTypes.Contains(type)) continue;
            var text = Paragraphs(shape);
            if (text.Length > 0) return text;
        }
        return null;
    }

    static string? FirstShapeText(XDocument doc)
    {
        foreach (var shape in doc.Descendants().Where(e => e.Name.LocalName == "sp"))
        {
            var text = Paragraphs(shape);
            if (text.Length > 0) return text.Split('\n')[0];
        }
        return null;
    }

    /// <summary>Text of a shape, one line per paragraph, keeping line breaks inside paragraphs.</summary>
    static string Paragraphs(XElement shape)
    {
        var paras = shape.Descendants().Where(e => e.Name.LocalName == "txBody").Elements().Where(e => e.Name.LocalName == "p");
        var lines = paras.Select(p =>
        {
            var line = new System.Text.StringBuilder();
            foreach (var child in p.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "r" or "fld":
                        line.Append(child.Elements().FirstOrDefault(e => e.Name.LocalName == "t")?.Value ?? "");
                        break;
                    case "br":
                        line.Append('\n');
                        break;
                }
            }
            return line.ToString();
        });
        return string.Join("\n", lines).Trim();
    }
}
