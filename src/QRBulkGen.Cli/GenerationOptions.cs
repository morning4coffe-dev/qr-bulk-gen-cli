namespace QRBulkGen.Cli;

internal enum InputFormat { Auto, Lines, Csv, Text }
internal enum OutputFormat { Png, Svg, Pdf }
internal enum ErrorCorrection { L, M, Q, H }
internal enum PaperSize { A4, Letter }

internal sealed class CliException(string message) : Exception(message);

internal sealed record QrRecord(string Payload, string? Label, int Row);

internal sealed record GenerationOptions
{
    public string? Text { get; init; }
    public string? Input { get; init; }
    public string? Output { get; init; }
    public OutputFormat? Format { get; init; }
    public InputFormat InputFormat { get; init; }
    public string? Column { get; init; }
    public string? Template { get; init; }
    public string? Label { get; init; }
    public string? LabelColumn { get; init; }
    public string? LabelTemplate { get; init; }
    public string? Delimiter { get; init; }
    public bool NoHeader { get; init; }
    public string? Encoding { get; init; }
    public ErrorCorrection ErrorCorrection { get; init; } = ErrorCorrection.M;
    public int? PixelsPerModule { get; init; }
    public PaperSize? PageSize { get; init; }
    public int? Rows { get; init; }
    public int? Columns { get; init; }
    public double? Margin { get; init; }
    public double? Gap { get; init; }
    public double? FontSize { get; init; }
    public bool Force { get; init; }

    public InputFormat ResolvedInputFormat =>
        InputFormat != InputFormat.Auto ? InputFormat :
        Path.GetExtension(Input)?.ToLowerInvariant() is ".csv" or ".tsv" ? InputFormat.Csv : InputFormat.Lines;

    public void Validate()
    {
        if ((Text is null) == (Input is null))
            throw new CliException("Supply exactly one of --text or --input. Use --input - to read a pipeline.");
        if (Text is not null && string.IsNullOrWhiteSpace(Text))
            throw new CliException("--text must not be empty or whitespace.");
        if (Input is not null && string.IsNullOrWhiteSpace(Input))
            throw new CliException("--input must not be empty.");
        if (Output is not null && string.IsNullOrWhiteSpace(Output))
            throw new CliException("--output must not be empty.");
        if (!Enum.IsDefined(InputFormat) || !Enum.IsDefined(ErrorCorrection) ||
            (Format.HasValue && !Enum.IsDefined(Format.Value)) ||
            (PageSize.HasValue && !Enum.IsDefined(PageSize.Value)))
            throw new CliException("Unknown input, output, page size, or error correction value. See --help.");
        if (Text is not null && (InputFormat != InputFormat.Auto || Encoding is not null))
            throw new CliException("--input-format and --encoding apply to --input, not --text.");
        if ((Text is not null || ResolvedInputFormat != InputFormat.Csv) &&
            (Column is not null || Template is not null || LabelColumn is not null ||
             LabelTemplate is not null || Delimiter is not null || NoHeader))
            throw new CliException("CSV options require a .csv/.tsv input or --input-format csv.");
        if (Column is not null && Template is not null)
            throw new CliException("Use either --column or --template, not both.");
        if ((Label is not null ? 1 : 0) + (LabelColumn is not null ? 1 : 0) +
            (LabelTemplate is not null ? 1 : 0) > 1)
            throw new CliException("Use only one of --label, --label-column, or --label-template.");
        if (PixelsPerModule is < 1 or > 32)
            throw new CliException("--pixels-per-module must be between 1 and 32.");
        if (Rows is < 1 or > 20 || Columns is < 1 or > 20)
            throw new CliException("--rows and --columns must be between 1 and 20.");
        if (Margin.HasValue && (!double.IsFinite(Margin.Value) || Margin < 0) ||
            Gap.HasValue && (!double.IsFinite(Gap.Value) || Gap < 0))
            throw new CliException("--margin and --gap must be finite, non-negative millimeter values.");
        if (FontSize.HasValue && (!double.IsFinite(FontSize.Value) || FontSize < 6 || FontSize > 36))
            throw new CliException("--font-size must be between 6 and 36 points.");
    }
}
