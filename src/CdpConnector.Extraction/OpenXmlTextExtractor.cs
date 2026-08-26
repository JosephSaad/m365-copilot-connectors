// ---------------------------------------------------------------------------
// OpenXmlTextExtractor.cs
// Word, Excel and PowerPoint, without a document library.
//
// An OOXML file is a zip of XML parts, and the text of a document lives in
// <w:t>, <a:t> and (for a workbook) the shared string table plus any inline
// strings. System.IO.Compression and XmlReader are enough to read that, which
// means the three formats an enterprise actually stores documents in cost this
// project no third-party dependency at all.
//
// What this does not do is layout. Table structure, slide order beyond part
// order, and cell coordinates are lost; what comes out is the words, in
// document order, which is what a search index wants. A tool that needed
// fidelity would need a real library, and that is a different requirement.
//
// Streaming throughout: parts are read one at a time through XmlReader rather
// than loaded as documents, because a lake holds spreadsheets that are tens of
// megabytes of shared strings and this runs against thousands of them.
// ---------------------------------------------------------------------------

namespace CdpConnector.Extraction;

using System.IO.Compression;
using System.Text;
using System.Xml;

/// <summary>docx, xlsx and pptx, read as the zips of XML they are.</summary>
public sealed class OpenXmlTextExtractor : ITextExtractor
{
    private const int MaxCharacters = 32 * 1024 * 1024;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = new[] { "docx", "docm", "xlsx", "xlsm", "pptx", "pptm" };

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractAsync(
        Stream content, string fileName, CancellationToken cancellationToken)
    {
        // ZipArchive wants a seekable stream and a network read is not one, so
        // the file is buffered first. The caller has already refused anything
        // above MaxRawFileBytes, which is what bounds this.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            var text = new StringBuilder();

            foreach (ZipArchiveEntry entry in TextBearingParts(archive))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (text.Length >= MaxCharacters)
                {
                    break;
                }

                using Stream part = entry.Open();
                AppendText(part, text);
            }

            return ExtractionResult.Success(text.ToString().Trim());
        }
        catch (InvalidDataException ex)
        {
            // Not a zip, or a corrupt one. Common enough in a lake - a truncated
            // upload, a file renamed to .docx - to be a status rather than an
            // exception that ends the crawl.
            return ExtractionResult.Failed("the file is not readable as Open XML: " + ex.Message);
        }
        catch (XmlException ex)
        {
            return ExtractionResult.Failed("the document's XML is malformed: " + ex.Message);
        }
    }

    /// <summary>
    /// The parts holding body text, in the order a reader would meet them.
    ///
    /// Ordered by name so a deck's slides come out in slide order rather than in
    /// whatever order the zip's central directory happens to list them, which
    /// varies by the tool that wrote the file.
    /// </summary>
    private static IEnumerable<ZipArchiveEntry> TextBearingParts(ZipArchive archive)
    {
        return archive.Entries
            .Where(entry => IsTextBearing(entry.FullName))
            .OrderBy(entry => entry.FullName.Length)
            .ThenBy(entry => entry.FullName, StringComparer.Ordinal);
    }

    private static bool IsTextBearing(string path)
    {
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)
            || path.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends every text run in one part.
    ///
    /// The element name is matched without its namespace prefix: w:t in a
    /// document, a:t in a slide, t in a workbook's shared strings. Matching the
    /// local name covers all three without three code paths, and the risk of a
    /// false positive is a stray word in a search index rather than a fault.
    /// </summary>
    private static void AppendText(Stream part, StringBuilder text)
    {
        var settings = new XmlReaderSettings
        {
            // A zip entry from an untrusted lake is exactly the input a DTD
            // reference should not be resolved for.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = false,
            CloseInput = false,
        };

        using XmlReader reader = XmlReader.Create(part, settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "t")
            {
                string value = reader.ReadElementContentAsString();

                if (value.Length > 0)
                {
                    text.Append(value);
                    text.Append(' ');
                }

                continue;
            }

            // A paragraph, a table row or a slide break becomes a line break, so
            // that words either side of it are not run together into a token
            // that matches nothing.
            if (reader.LocalName is "p" or "tr" or "br")
            {
                text.Append('\n');
            }
        }
    }
}
