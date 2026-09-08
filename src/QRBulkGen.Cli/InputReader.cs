using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace QRBulkGen.Cli;

internal static class InputReader
{
    public static IReadOnlyList<QrRecord> Read(GenerationOptions options, Stream standardInput)
    {
        if (options.Text is not null)
            return [new QrRecord(options.Text, options.Label, 1)];

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(
                options.Encoding ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            throw new CliException("Unknown --encoding. Use an encoding name such as utf-8 or windows-1250.");
        }

        using var file = options.Input == "-" ? null : File.OpenRead(options.Input!);
        using var reader = new StreamReader(
            file ?? standardInput, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var records = options.ResolvedInputFormat switch
        {
            InputFormat.Csv => ReadCsv(reader, options),
            InputFormat.Text => ReadText(reader, options),
            _ => ReadLines(reader, options)
        };
        if (records.Count == 0)
            throw new CliException("Input contains no payloads. No output was written.");
        return records;
    }

    private static List<QrRecord> ReadText(TextReader reader, GenerationOptions options)
    {
        var text = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(text) ? [] : [new QrRecord(text, options.Label, 1)];
    }

    private static List<QrRecord> ReadLines(TextReader reader, GenerationOptions options)
    {
        List<QrRecord> records = [];
        var row = 0;
        while (reader.ReadLine() is { } line)
        {
            row++;
            if (!string.IsNullOrWhiteSpace(line))
                records.Add(new QrRecord(line, options.Label, row));
        }
        return records;
    }

    private static List<QrRecord> ReadCsv(TextReader reader, GenerationOptions options)
    {
        var delimiter = options.Delimiter ??
            (string.Equals(Path.GetExtension(options.Input), ".tsv", StringComparison.OrdinalIgnoreCase) ? "\t" : ",");
        if (delimiter == "\\t")
            delimiter = "\t";
        if (delimiter.Length != 1 || delimiter[0] is '"' or '\r' or '\n' or '\0')
            throw new CliException("--delimiter must be one character (other than a quote or newline), or \\t.");

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = !options.NoHeader,
            Delimiter = delimiter,
            DetectColumnCountChanges = true,
            ExceptionMessagesContainRawData = false
        };
        using var csv = new CsvReader(reader, configuration);
        List<QrRecord> records = [];
        try
        {
            if (!csv.Read())
                return records;
            string[] columns;
            if (options.NoHeader)
            {
                columns = Enumerable.Range(1, csv.Parser.Count)
                    .Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();
            }
            else
            {
                csv.ReadHeader();
                columns = csv.HeaderRecord!;
                if (columns.Any(string.IsNullOrWhiteSpace) ||
                    columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
                    throw new CliException("CSV headers must be non-empty and unique (names are case-sensitive).");
            }
            var payload = CreateSelector(options.Column, options.Template, columns, required: true);
            var caption = CreateSelector(options.LabelColumn, options.LabelTemplate, columns, required: false);
            var hasRow = options.NoHeader || csv.Read();
            while (hasRow)
            {
                var row = csv.Parser.Row;
                if (csv.Parser.Count != columns.Length)
                    throw new CliException($"CSV record {row} has a different number of columns than expected.");
                // Get every field so malformed quoting in an unselected column is not silently accepted.
                var fields = Enumerable.Range(0, columns.Length).Select(index => csv.GetField(index) ?? "").ToArray();
                var value = payload!(fields);
                if (string.IsNullOrWhiteSpace(value))
                    throw new CliException($"CSV record {row} has an empty payload. Select a non-empty column or template.");
                records.Add(new QrRecord(value, caption is null ? options.Label : caption(fields), row));
                hasRow = csv.Read();
            }
        }
        catch (CsvHelperException)
        {
            throw new CliException($"Invalid CSV near record {csv.Parser.Row}. Check quoting, delimiter, and column count.");
        }
        return records;
    }

    private static Func<string[], string>? CreateSelector(
        string? column, string? template, string[] columns, bool required)
    {
        if (template is not null)
            return RecordTemplate.Compile(template, columns);
        if (column is not null)
        {
            var index = Array.IndexOf(columns, column);
            if (index < 0)
                throw new CliException($"CSV column '{column}' was not found. Names are case-sensitive; --no-header uses 1-based numbers.");
            return fields => fields[index];
        }
        if (!required)
            return null;
        if (columns.Length == 1)
            return fields => fields[0];
        throw new CliException("CSV has multiple columns. Choose --column or --template.");
    }
}
