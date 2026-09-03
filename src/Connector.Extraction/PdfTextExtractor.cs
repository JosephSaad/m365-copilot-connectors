// ---------------------------------------------------------------------------
// PdfTextExtractor.cs
// Optional, and off unless the build asks for it.
//
// PDF is the one format a document lake is full of and the base class library
// cannot read. Adding a parser is a dependency decision for the deployment
// rather than a coding one, so it is a build switch:
//
//     dotnet build -p:EnablePdfExtraction=true
//
// and then regenerate build/Get-OfflinePackages.ps1's list in the same change,
// or the air-gapped build machine gets a restore failure instead of a PDF.
//
// PdfPig is Apache-2.0, which is the reason it is the one named here: this
// repository is redistributed to a customer, so a copyleft or per-server
// licensed parser would travel with it. Do not swap it for iText or Aspose
// without that conversation.
//
// Without the switch the file compiles to nothing and a PDF is indexed by its
// metadata with extractStatus explaining why there is no body - which is the
// same behaviour as any other unsupported type, and findable.
// ---------------------------------------------------------------------------

#if PDF_EXTRACTION

namespace Connector.Extraction;

using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

/// <summary>PDF, when the build enabled it.</summary>
public sealed class PdfTextExtractor : ITextExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = new[] { "pdf" };

    /// <inheritdoc/>
    public async Task<ExtractionResult> ExtractAsync(
        Stream content, string fileName, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            using PdfDocument document = PdfDocument.Open(buffer);

            var text = new StringBuilder();

            foreach (Page page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.AppendLine(page.Text);
            }

            string extracted = text.ToString().Trim();

            // A scanned PDF parses perfectly and yields nothing. That is Empty
            // rather than Failed, and the difference matters to whoever reads
            // the run summary: Empty at scale means "buy OCR", Failed means
            // "something is wrong with these files".
            return ExtractionResult.Success(extracted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failed($"the PDF could not be parsed ({ex.GetType().Name}): {ex.Message}");
        }
    }
}

#endif
