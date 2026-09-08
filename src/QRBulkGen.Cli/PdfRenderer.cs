using System.Globalization;
using System.Reflection;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace QRBulkGen.Cli;

internal static class PdfRenderer
{
    static PdfRenderer() => GlobalFontSettings.FontResolver = new BundledFontResolver();

    public static void Render(IReadOnlyList<QrRecord> records, GenerationOptions options, Stream output)
    {
        using var document = new PdfDocument();
        document.Info.Title = "QR code labels";
        document.Info.Creator = "QR Bulk CLI";
        var rows = options.Rows ?? 5;
        var columns = options.Columns ?? 2;
        var margin = Millimeters(options.Margin ?? 6);
        var gap = Millimeters(options.Gap ?? 2);
        var pageWidth = Millimeters(options.PageSize == PaperSize.Letter ? 215.9 : 210);
        var pageHeight = Millimeters(options.PageSize == PaperSize.Letter ? 279.4 : 297);
        var width = (pageWidth - 2 * margin - (columns - 1) * gap) / columns;
        var height = (pageHeight - 2 * margin - (rows - 1) * gap) / rows;
        if (width < Millimeters(12) || height < Millimeters(12))
            throw new CliException("PDF cells are too small. Reduce rows, columns, margin, or gap.");
        var font = new XFont("Noto Sans", options.FontSize ?? 10, XFontStyleEx.Regular);
        var perPage = rows * columns;
        for (var offset = 0; offset < records.Count; offset += perPage)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            using var graphics = XGraphics.FromPdfPage(page);
            for (var index = offset; index < Math.Min(offset + perPage, records.Count); index++)
            {
                var position = index - offset;
                var x = margin + position % columns * (width + gap);
                var y = margin + position / columns * (height + gap);
                DrawCell(graphics, records[index], options, font, x, y, width, height);
            }
        }
        document.Save(output, closeStream: false);
    }

    private static void DrawCell(
        XGraphics graphics, QrRecord record, GenerationOptions options, XFont font,
        double x, double y, double width, double height)
    {
        var padding = Millimeters(1);
        var lines = WrapText(graphics, record.Label, font, width - 2 * padding);
        var lineHeight = font.GetHeight();
        var captionHeight = lines.Count == 0 ? 0 : lines.Count * lineHeight + Millimeters(2);
        var size = Math.Min(width - 2 * padding, height - 2 * padding - captionHeight);
        using var data = OutputWriter.CreateData(record, options.ErrorCorrection);
        var moduleCount = data.ModuleMatrix.Count;
        if (size < Millimeters(10) || size / moduleCount < Millimeters(0.25))
            throw new CliException($"Record {record.Row} cannot fit legibly in its PDF cell. Shorten the caption/payload or reduce --rows/--columns.");

        var qrX = x + (width - size) / 2;
        var qrY = y + (height - size - captionHeight) / 2;
        var module = size / moduleCount;
        graphics.DrawRectangle(XBrushes.White, qrX, qrY, size, size);
        for (var row = 0; row < moduleCount; row++)
        {
            for (var column = 0; column < moduleCount; column++)
            {
                if (!data.ModuleMatrix[row][column])
                    continue;
                var start = column;
                while (column + 1 < moduleCount && data.ModuleMatrix[row][column + 1])
                    column++;
                graphics.DrawRectangle(XBrushes.Black,
                    qrX + start * module, qrY + row * module, (column - start + 1) * module, module);
            }
        }
        for (var index = 0; index < lines.Count; index++)
            graphics.DrawString(lines[index], font, XBrushes.Black,
                new XRect(x + padding, qrY + size + Millimeters(2) + index * lineHeight,
                    width - 2 * padding, lineHeight), XStringFormats.TopCenter);
    }

    private static List<string> WrapText(XGraphics graphics, string? text, XFont font, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        List<string> lines = [];
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(paragraph);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (graphics.MeasureString(line + element, font).Width > maxWidth && line.Length > 0)
                {
                    var value = line.ToString();
                    var space = value.LastIndexOf(' ');
                    if (space > 0)
                    {
                        lines.Add(value[..space]);
                        line.Clear().Append(value[(space + 1)..]);
                    }
                    else
                    {
                        lines.Add(value);
                        line.Clear();
                    }
                }
                line.Append(element);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static double Millimeters(double value) => value * 72 / 25.4;

    private sealed class BundledFontResolver : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            familyName == "Noto Sans" ? new FontResolverInfo("NotoSans-Regular") : null;

        public byte[]? GetFont(string faceName)
        {
            if (faceName != "NotoSans-Regular")
                return null;
            using var resource = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("QRBulkGen.Cli.Fonts.NotoSans-Regular.ttf") ??
                throw new InvalidOperationException("The bundled Noto Sans font is missing.");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
