using System.Text;
using QRCoder;
using QRCoder.Exceptions;

namespace QRBulkGen.Cli;

internal static class OutputWriter
{
    public static void Write(
        IReadOnlyList<QrRecord> records, GenerationOptions options, OutputPlan plan, Stream standardOutput)
    {
        if (plan.StandardOutput)
        {
            using var buffer = new MemoryStream();
            if (plan.Format == OutputFormat.Pdf)
                PdfRenderer.Render(records, options, buffer);
            else
                RenderImage(records[0], options, plan.Format, buffer);
            buffer.Position = 0;
            buffer.CopyTo(standardOutput);
            standardOutput.Flush();
            return;
        }

        List<(string Temporary, string Destination)> staged = [];
        try
        {
            for (var index = 0; index < plan.Paths.Count; index++)
            {
                var destination = plan.Paths[index];
                var parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);
                var temporary = Path.Combine(parent, $".qr-bulk-{Guid.NewGuid():N}.tmp");
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                staged.Add((temporary, destination));
                if (plan.Format == OutputFormat.Pdf)
                    PdfRenderer.Render(records, options, stream);
                else
                    RenderImage(records[index], options, plan.Format, stream);
            }
            // Render the complete batch before replacing anything. Each final rename is on the same filesystem.
            foreach (var (temporary, destination) in staged)
                File.Move(temporary, destination, overwrite: options.Force);
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                File.Delete(temporary);
        }
    }

    public static QRCodeData CreateData(QrRecord record, ErrorCorrection correction)
    {
        try
        {
            return QRCodeGenerator.GenerateQrCode(
                record.Payload, Enum.Parse<QRCodeGenerator.ECCLevel>(correction.ToString()),
                forceUtf8: true, utf8BOM: false, eciMode: QRCodeGenerator.EciMode.Utf8);
        }
        catch (DataTooLongException)
        {
            throw new CliException($"Payload at record {record.Row} exceeds QR capacity. Shorten it or choose a lower --error-correction level.");
        }
    }

    private static void RenderImage(
        QrRecord record, GenerationOptions options, OutputFormat format, Stream output)
    {
        using var data = CreateData(record, options.ErrorCorrection);
        if (format == OutputFormat.Png)
        {
            using var renderer = new PngByteQRCode(data);
            output.Write(renderer.GetGraphic(options.PixelsPerModule ?? 10));
        }
        else
        {
            using var renderer = new SvgQRCode(data);
            var svg = renderer.GetGraphic(options.PixelsPerModule ?? 10);
            output.Write(Encoding.UTF8.GetBytes(svg));
        }
    }
}
