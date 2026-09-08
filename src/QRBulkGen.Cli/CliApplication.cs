using System.CommandLine;
using System.Text;

namespace QRBulkGen.Cli;

internal static class CliApplication
{
    public static int Run(
        string[] args, Stream input, Stream output, TextWriter helpOutput, TextWriter error)
    {
        var text = new Option<string?>("--text", "-t") { Description = "Encode one literal payload." };
        var file = new Option<string?>("--input", "-i") { Description = "Input file, or - for standard input." };
        var destination = new Option<string?>("--output", "-o")
        {
            Description = "Image file, image directory, PDF file, or - for stdout. Defaults: qr.png / qr-codes / qr-codes.pdf."
        };
        var format = new Option<OutputFormat?>("--format", "-f")
        {
            Description = "Output format; inferred from a file extension, otherwise png."
        };
        var inputFormat = new Option<InputFormat>("--input-format")
        {
            Description = "Auto: .csv/.tsv are CSV, otherwise one payload per line. Text reads an entire file as one payload.",
            DefaultValueFactory = _ => InputFormat.Auto
        };
        var column = new Option<string?>("--column") { Description = "CSV payload column name; a 1-based number with --no-header." };
        var template = new Option<string?>("--template") { Description = "CSV payload template, e.g. https://example.com/{id}. Escape braces as {{ and }}." };
        var label = new Option<string?>("--label") { Description = "Constant caption below each QR code (PDF only)." };
        var labelColumn = new Option<string?>("--label-column") { Description = "CSV caption column (PDF only)." };
        var labelTemplate = new Option<string?>("--label-template") { Description = "CSV caption template, e.g. {name} - {id} (PDF only)." };
        var delimiter = new Option<string?>("--delimiter") { Description = "CSV separator: one character, or \\t. Default: comma (.tsv: tab)." };
        var noHeader = new Option<bool>("--no-header") { Description = "CSV has no header; refer to columns with 1-based numbers." };
        var encoding = new Option<string?>("--encoding") { Description = "File/stdin encoding. Default: strict UTF-8; e.g. windows-1250 for legacy data." };
        var correction = new Option<ErrorCorrection>("--error-correction")
        {
            Description = "QR error correction: L (~7%), M (~15%), Q (~25%), H (~30%).",
            DefaultValueFactory = _ => ErrorCorrection.M
        };
        var pixels = new Option<int?>("--pixels-per-module") { Description = "PNG/SVG module size, 1-32. Default: 10. Quiet zones are always included." };
        var page = new Option<PaperSize?>("--page-size") { Description = "PDF paper size. Default: A4." };
        var rows = new Option<int?>("--rows") { Description = "PDF rows per page, 1-20. Default: 5." };
        var columns = new Option<int?>("--columns") { Description = "PDF columns per page, 1-20. Default: 2." };
        var margin = new Option<double?>("--margin") { Description = "PDF outside margin in millimeters. Default: 6." };
        var gap = new Option<double?>("--gap") { Description = "PDF gap between cells in millimeters. Default: 2." };
        var fontSize = new Option<double?>("--font-size") { Description = "PDF caption size in points, 6-36. Default: 10." };
        var force = new Option<bool>("--force") { Description = "Replace existing output files (never the input file)." };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Suppress the success summary on stderr." };
        var root = new RootCommand(
            """
            Generate QR codes offline from any text, URLs, CSV records, or pipelines.

            Examples:
              qr-bulk --text "https://example.com" -o qr.png
              qr-bulk -i links.txt -o codes
              qr-bulk -i items.csv --column url --label-column name -o labels.pdf
              qr-bulk -i - --format svg -o -
            """)
        {
            text, file, destination, format, inputFormat, column, template,
            label, labelColumn, labelTemplate, delimiter, noHeader, encoding,
            correction, pixels, page, rows, columns, margin, gap, fontSize, force, quiet
        };

        root.SetAction(result =>
        {
            var options = new GenerationOptions
            {
                Text = result.GetValue(text),
                Input = result.GetValue(file),
                Output = result.GetValue(destination),
                Format = result.GetValue(format),
                InputFormat = result.GetValue(inputFormat),
                Column = result.GetValue(column),
                Template = result.GetValue(template),
                Label = result.GetValue(label),
                LabelColumn = result.GetValue(labelColumn),
                LabelTemplate = result.GetValue(labelTemplate),
                Delimiter = result.GetValue(delimiter),
                NoHeader = result.GetValue(noHeader),
                Encoding = result.GetValue(encoding),
                ErrorCorrection = result.GetValue(correction),
                PixelsPerModule = result.GetValue(pixels),
                PageSize = result.GetValue(page),
                Rows = result.GetValue(rows),
                Columns = result.GetValue(columns),
                Margin = result.GetValue(margin),
                Gap = result.GetValue(gap),
                FontSize = result.GetValue(fontSize),
                Force = result.GetValue(force)
            };
            try
            {
                options.Validate();
                var records = InputReader.Read(options, input);
                var plan = OutputPlan.Create(options, records.Count);
                OutputWriter.Write(records, options, plan, output);
                if (!result.GetValue(quiet))
                    error.WriteLine($"Generated {records.Count} QR code(s) -> {plan.Description}");
                return 0;
            }
            catch (CliException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 2;
            }
            catch (DecoderFallbackException)
            {
                error.WriteLine("error: Input is not valid in the selected encoding. Use --encoding for non-UTF-8 files.");
                return 2;
            }
            catch (IOException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
            catch (UnauthorizedAccessException exception)
            {
                error.WriteLine($"error: {exception.Message}");
                return 1;
            }
        });

        var parsed = root.Parse(args.Length == 0 ? ["--help"] : args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var parseError in parsed.Errors)
                error.WriteLine($"error: {parseError.Message}");
            error.WriteLine("Run qr-bulk --help for usage.");
            return 2;
        }
        return parsed.Invoke(new InvocationConfiguration
        {
            Output = helpOutput,
            Error = error,
            EnableDefaultExceptionHandler = false
        });
    }
}
