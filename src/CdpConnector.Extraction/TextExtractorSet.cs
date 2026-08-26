// ---------------------------------------------------------------------------
// TextExtractorSet.cs
// Which extractor gets a file, and the ceiling none of them may exceed.
//
// Two rules live here rather than in the crawler because they are the same for
// every source that indexes files:
//
//   * A file above the raw ceiling is not read at all. Not read, not buffered,
//     not decompressed - the decision is made from the file's reported size
//     before a byte moves. A lake holds multi-gigabyte archives, and the cost of
//     discovering that by streaming one is a run that dies on memory.
//
//   * An unknown extension is not an error. The file is indexed by everything
//     else known about it, with a status saying there is no body.
//
// The allowlist is the operator's, not this class's: a build that can read six
// formats still only reads the ones the configuration asks for, because "what
// this connector indexes" is a decision about the deployment rather than about
// the code.
// ---------------------------------------------------------------------------

namespace CdpConnector.Extraction;

/// <summary>The extractors available, indexed by extension.</summary>
public sealed class TextExtractorSet
{
    private readonly Dictionary<string, ITextExtractor> byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="TextExtractorSet"/> class.</summary>
    /// <param name="extractors">The extractors to offer.</param>
    public TextExtractorSet(IEnumerable<ITextExtractor> extractors)
    {
        foreach (ITextExtractor extractor in extractors)
        {
            foreach (string extension in extractor.Extensions)
            {
                this.byExtension[extension] = extractor;
            }
        }
    }

    /// <summary>Everything this build can read.</summary>
    /// <returns>The default set.</returns>
    public static TextExtractorSet Default()
    {
        var extractors = new List<ITextExtractor>
        {
            new PlainTextExtractor(),
            new OpenXmlTextExtractor(),
        };

#if PDF_EXTRACTION
        extractors.Add(new PdfTextExtractor());
#endif

        return new TextExtractorSet(extractors);
    }

    /// <summary>Gets the extensions this set can extract, lower case, without dots.</summary>
    public IReadOnlyCollection<string> SupportedExtensions => this.byExtension.Keys;

    /// <summary>Gets a value indicating whether some extractor claims this extension.</summary>
    /// <param name="fileName">The file name to test.</param>
    /// <returns>True when the extension is handled.</returns>
    public bool CanExtract(string fileName)
    {
        return this.byExtension.ContainsKey(Extension(fileName));
    }

    /// <summary>Extracts the text of one file.</summary>
    /// <param name="open">Opens the file. Not called when the size ceiling refuses it.</param>
    /// <param name="fileName">The file name, for the extractor choice and the reason.</param>
    /// <param name="sizeBytes">The file's reported size.</param>
    /// <param name="maxRawBytes">The ceiling above which the file is not read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The text, or why there is none.</returns>
    public async Task<ExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> open,
        string fileName,
        long sizeBytes,
        long maxRawBytes,
        CancellationToken cancellationToken)
    {
        string extension = Extension(fileName);

        if (!this.byExtension.TryGetValue(extension, out ITextExtractor? extractor))
        {
            return ExtractionResult.Unsupported(extension.Length == 0 ? "(none)" : extension);
        }

        if (maxRawBytes > 0 && sizeBytes > maxRawBytes)
        {
            // Before the open, deliberately. See the file header.
            return ExtractionResult.TooLarge(sizeBytes, maxRawBytes);
        }

        Stream content = await open(cancellationToken);

        await using (content)
        {
            try
            {
                return await extractor.ExtractAsync(content, fileName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the run stopping, not this file failing.
                throw;
            }
            catch (Exception ex)
            {
                // An extractor that throws anyway is a bug in the extractor, and
                // one malformed file must not end a crawl of a million. The
                // message names the exception type and its own message, neither
                // of which contains file content.
                return ExtractionResult.Failed($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string Extension(string fileName)
    {
        return Path.GetExtension(fileName).TrimStart('.');
    }
}
