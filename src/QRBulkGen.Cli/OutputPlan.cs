using System.Globalization;

namespace QRBulkGen.Cli;

internal sealed record OutputPlan(OutputFormat Format, IReadOnlyList<string> Paths, string Description)
{
    public bool StandardOutput => Paths.Count == 0;

    public static OutputPlan Create(GenerationOptions options, int count)
    {
        var output = options.Output;
        var directory = output is not null &&
            (Directory.Exists(output) || Path.EndsInDirectorySeparator(output));
        OutputFormat? extensionFormat = directory ? null : Path.GetExtension(output)?.ToLowerInvariant() switch
        {
            ".png" => OutputFormat.Png,
            ".svg" => OutputFormat.Svg,
            ".pdf" => OutputFormat.Pdf,
            _ => null
        };
        var format = options.Format ?? extensionFormat ?? OutputFormat.Png;
        if (options.Format.HasValue && extensionFormat.HasValue && options.Format != extensionFormat)
            throw new CliException("--format does not match the output file extension.");
        if (format != OutputFormat.Pdf &&
            (options.Label is not null || options.LabelColumn is not null || options.LabelTemplate is not null ||
             options.PageSize is not null || options.Rows is not null || options.Columns is not null ||
             options.Margin is not null || options.Gap is not null || options.FontSize is not null))
            throw new CliException("Captions and page layout options require PDF output.");
        if (format == OutputFormat.Pdf && options.PixelsPerModule.HasValue)
            throw new CliException("--pixels-per-module applies to PNG/SVG. PDF QR codes scale to their label cells.");
        if (output == "-")
        {
            if (options.Format is null)
                throw new CliException("Specify --format when writing to --output -.");
            if (format != OutputFormat.Pdf && count != 1)
                throw new CliException("PNG/SVG stdout requires exactly one payload. Use a directory or PDF for batches.");
            return new OutputPlan(format, [], "stdout");
        }
        var extension = format.ToString().ToLowerInvariant();
        output ??= format == OutputFormat.Pdf ? "qr-codes.pdf" :
            options.Text is not null || options.ResolvedInputFormat == InputFormat.Text ? $"qr.{extension}" : "qr-codes";
        var singleFile = format == OutputFormat.Pdf || (!directory && (extensionFormat.HasValue ||
            options.Output is null && (options.Text is not null || options.ResolvedInputFormat == InputFormat.Text)));
        if (singleFile && directory)
            throw new CliException("PDF output must be a file, not a directory.");
        if (singleFile && format != OutputFormat.Pdf && count != 1)
            throw new CliException("An image file requires exactly one payload. Use an output directory or a PDF file for batches.");

        string fullOutput;
        string? fullInput;
        try
        {
            fullOutput = Path.GetFullPath(output);
            fullInput = options.Input is null or "-" ? null : ResolvePath(options.Input);
        }
        catch (ArgumentException)
        {
            throw new CliException("Input or output path is invalid.");
        }
        var paths = singleFile ? [fullOutput] : Enumerable.Range(1, count)
            .Select(index => Path.Combine(fullOutput, $"{index.ToString("D6", CultureInfo.InvariantCulture)}.{extension}")).ToArray();
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in paths)
        {
            if (string.Equals(ResolvePath(path), fullInput, comparison))
                throw new CliException("Output must not overwrite the input file, even with --force.");
            if (Directory.Exists(path))
                throw new CliException($"An output path is an existing directory: {path}");
            if (new FileInfo(path).LinkTarget is not null)
                throw new CliException($"Refusing to replace a symbolic link: {path}");
            if (File.Exists(path))
            {
                if (!options.Force)
                    throw new CliException($"Output already exists: {path}. Use --force to replace it.");
            }
        }
        return new OutputPlan(format, paths, fullOutput);
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var resolved = root;
        // Resolve directory aliases too: a junction or symlink must not bypass input-file protection.
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(resolved, segment);
            FileSystemInfo entry = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            resolved = entry.LinkTarget is null ? next :
                (entry.ResolveLinkTarget(returnFinalTarget: true) ??
                 throw new CliException($"Unable to resolve symbolic link: {next}")).FullName;
        }
        return resolved;
    }
}
