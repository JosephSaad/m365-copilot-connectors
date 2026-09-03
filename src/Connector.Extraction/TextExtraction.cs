// ---------------------------------------------------------------------------
// TextExtraction.cs
// File bytes in, indexable text out - or an honest reason there is none.
//
// The return type is the design. Extraction fails routinely at data-lake scale:
// a PDF that is a scan, a document written by a tool nobody remembers, a file
// whose extension lies about its contents. None of those should stop a crawl,
// and none of them should make the file disappear from search either. So every
// path returns a result carrying a status, and the caller decides what to index
// - which for this connector means "the metadata, and a property saying why the
// body is missing".
//
// Nothing here reads a whole file into memory twice, and nothing here throws
// for a malformed input. A file that cannot be parsed is a status, not an
// exception; only genuinely exceptional things - a stream that dies mid-read -
// travel as exceptions.
// ---------------------------------------------------------------------------

namespace Connector.Extraction;

/// <summary>What happened when a file was turned into text.</summary>
public enum ExtractionStatus
{
    /// <summary>Text was extracted.</summary>
    Extracted = 0,

    /// <summary>The file parsed, and genuinely holds no text.</summary>
    Empty = 1,

    /// <summary>No extractor handles this file type. The metadata is still indexed.</summary>
    Unsupported = 2,

    /// <summary>The file was larger than the configured raw-size ceiling and was not read.</summary>
    TooLarge = 3,

    /// <summary>An extractor recognised the type and could not parse the file.</summary>
    Failed = 4,
}

/// <summary>The text of one file, or why there is none.</summary>
public sealed class ExtractionResult
{
    private ExtractionResult(ExtractionStatus status, string text, string detail)
    {
        this.Status = status;
        this.Text = text;
        this.Detail = detail;
    }

    /// <summary>Gets what happened.</summary>
    public ExtractionStatus Status { get; }

    /// <summary>Gets the extracted text, empty unless the status is Extracted.</summary>
    public string Text { get; }

    /// <summary>
    /// Gets a short reason, safe to log and to index as a property. It names the
    /// failure and never quotes file content.
    /// </summary>
    public string Detail { get; }

    /// <summary>Gets a value indicating whether there is a body to index.</summary>
    public bool HasText => this.Status == ExtractionStatus.Extracted && this.Text.Length > 0;

    /// <summary>Text was extracted.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The result.</returns>
    public static ExtractionResult Success(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? new ExtractionResult(ExtractionStatus.Empty, string.Empty, "the file holds no text")
            : new ExtractionResult(ExtractionStatus.Extracted, text, string.Empty);
    }

    /// <summary>Nothing handles this file type.</summary>
    /// <param name="extension">The extension, for the reason string.</param>
    /// <returns>The result.</returns>
    public static ExtractionResult Unsupported(string extension)
    {
        return new ExtractionResult(
            ExtractionStatus.Unsupported,
            string.Empty,
            $"no text extractor for '{extension}' files in this build");
    }

    /// <summary>The file was too large to read.</summary>
    /// <param name="bytes">The file size.</param>
    /// <param name="ceiling">The configured ceiling.</param>
    /// <returns>The result.</returns>
    public static ExtractionResult TooLarge(long bytes, long ceiling)
    {
        return new ExtractionResult(
            ExtractionStatus.TooLarge,
            string.Empty,
            $"file is {bytes} bytes, above the {ceiling} byte ceiling");
    }

    /// <summary>An extractor recognised the type and could not parse it.</summary>
    /// <param name="reason">Why, without file content in it.</param>
    /// <returns>The result.</returns>
    public static ExtractionResult Failed(string reason)
    {
        return new ExtractionResult(ExtractionStatus.Failed, string.Empty, reason);
    }
}

/// <summary>One file format, or a family of them.</summary>
public interface ITextExtractor
{
    /// <summary>Gets the lower-case extensions this handles, without the dot.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Reads the stream and returns its text.</summary>
    /// <param name="content">The file, positioned at the start.</param>
    /// <param name="fileName">The file name, for reasons and for format hints.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The text, or why there is none.</returns>
    Task<ExtractionResult> ExtractAsync(Stream content, string fileName, CancellationToken cancellationToken);
}
