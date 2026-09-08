using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Pdf.IO;
using Xunit;
using ZXing;

namespace QRBulkGen.Cli.Tests;

public sealed class CliTests
{
    [Fact]
    public void NoArgumentsShowsHelpWithoutReadingInput()
    {
        var result = Run([]);
        Assert.Equal(0, result.Code);
        Assert.Contains("--input", result.Help);
        Assert.Empty(result.Error);
        Assert.Empty(result.Bytes);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void InformationalOptionsSucceed(string option)
    {
        var result = Run([option]);
        Assert.Equal(0, result.Code);
        Assert.NotEmpty(result.Help);
        Assert.Empty(result.Bytes);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--text", "x", "--input", "-")]
    [InlineData("--text", "")]
    [InlineData("--text", "x", "--pixels-per-module", "0")]
    [InlineData("--text", "x", "--format", "jpeg")]
    [InlineData("--text", "x", "--format", "123")]
    [InlineData("--text", "x", "--format", "pdf", "--rows", "0")]
    [InlineData("--text", "x", "--format", "pdf", "--gap", "NaN")]
    [InlineData("--text", "x", "--format", "pdf", "--margin", "Infinity")]
    [InlineData("--text", "x", "--format", "pdf", "--font-size", "0")]
    [InlineData("--text", "x", "--column", "url")]
    [InlineData("--text", "x", "--input-format", "text")]
    [InlineData("--text", "x", "--label", "Caption")]
    [InlineData("--text", "x", "--format", "svg", "--output", "test.png")]
    [InlineData("--text", "x", "--output", "-")]
    public void InvalidArgumentsFailWithoutOutput(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(2, result.Code);
        Assert.StartsWith("error:", result.Error);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void PngHasExpectedSignatureAndModuleResolution()
    {
        var result = Run(["-t", "hello", "-f", "png", "-o", "-", "--pixels-per-module", "3", "-q"]);
        Assert.Equal(0, result.Code);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, result.Bytes[..8]);
        using var data = OutputWriter.CreateData(new QrRecord("hello", null, 1), ErrorCorrection.M);
        var width = BinaryPrimitives.ReadInt32BigEndian(result.Bytes.AsSpan(16, 4));
        Assert.Equal(data.ModuleMatrix.Count * 3, width);
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(result.Bytes.AsSpan(20, 4)));
        Assert.Empty(result.Error);
        Assert.Empty(result.Help);
    }

    [Fact]
    public void SvgStdoutContainsOnlyValidXml()
    {
        var result = Run(["--input", "-", "--format", "svg", "--output", "-"], " https://example.com/?a=1&b=2 \n");
        Assert.Equal(0, result.Code);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(result.Bytes));
        Assert.Equal("svg", xml.Root!.Name.LocalName);
        Assert.Contains("Generated 1", result.Error);
        Assert.Empty(result.Help);
    }

    [Theory]
    [InlineData("https://example.com/menu")]
    [InlineData("  ASSET-0042  ")]
    [InlineData("WIFI:T:WPA;S:Guest;P:example-only;;")]
    [InlineData("\u017dlu\u0165ou\u010dk\u00fd k\u016f\u0148 \ud83d\ude80")]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:Alex Example\nEND:VCARD\n")]
    public void QrPayloadRoundTripsWithoutNormalization(string payload)
    {
        using var data = OutputWriter.CreateData(new QrRecord(payload, null, 1), ErrorCorrection.M);
        const int scale = 4;
        var width = data.ModuleMatrix.Count * scale;
        var rgb = new byte[width * width * 3];
        for (var y = 0; y < width; y++)
        for (var x = 0; x < width; x++)
        {
            var color = data.ModuleMatrix[y / scale][x / scale] ? (byte)0 : (byte)255;
            var offset = (y * width + x) * 3;
            rgb[offset] = rgb[offset + 1] = rgb[offset + 2] = color;
        }
        var decoded = new BarcodeReaderGeneric().Decode(rgb, width, width, RGBLuminanceSource.BitmapFormat.RGB24);
        Assert.NotNull(decoded);
        Assert.Equal(payload, decoded.Text);
    }

    [Fact]
    public void LinesIgnoreBlanksButPreservePayloadWhitespaceAndDuplicates()
    {
        var records = Read(new GenerationOptions { Input = "-" }, "  asset  \n\n \t\nasset\nasset\n");
        Assert.Equal(["  asset  ", "asset", "asset"], records.Select(record => record.Payload));
        Assert.Equal([1, 4, 5], records.Select(record => record.Row));
    }

    [Fact]
    public void WholeFilePreservesNewlinesAndTrailingNewline()
    {
        const string payload = "one\r\ntwo\n";
        var records = Read(new GenerationOptions { Input = "-", InputFormat = InputFormat.Text }, payload);
        Assert.Equal(payload, Assert.Single(records).Payload);
    }

    [Fact]
    public void Utf8BomIsNotPartOfPayload()
    {
        var records = Read(new GenerationOptions { Input = "-" }, "\ufeff\u017dlu\u0165ou\u010dk\u00fd\n");
        Assert.Equal("\u017dlu\u0165ou\u010dk\u00fd", Assert.Single(records).Payload);
    }

    [Fact]
    public void LegacyEncodingIsExplicitAndPreservesDiacritics()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(Encoding.GetEncoding("windows-1250").GetBytes("\u017dlu\u0165ou\u010dk\u00fd"));
        var records = InputReader.Read(new GenerationOptions { Input = "-", Encoding = "windows-1250" }, input);
        Assert.Equal("\u017dlu\u0165ou\u010dk\u00fd", Assert.Single(records).Payload);
    }

    [Fact]
    public void InvalidUtf8FailsRatherThanReplacingCharacters()
    {
        var result = RunBytes(["-i", "-", "-f", "svg", "-o", "-"], [0xc3, 0x28]);
        Assert.Equal(2, result.Code);
        Assert.Contains("--encoding", result.Error);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void CsvSupportsQuotedDelimitersQuotesAndNewlines()
    {
        var options = new GenerationOptions
        {
            Input = "-", InputFormat = InputFormat.Csv, Column = "payload", LabelColumn = "label"
        };
        var records = Read(options, "payload,label\r\n\"a,b\",\"A \"\"quote\"\"\"\r\n\"two\nlines\",Second\r\n");
        Assert.Equal("a,b", records[0].Payload);
        Assert.Equal("A \"quote\"", records[0].Label);
        Assert.Equal("two\nlines", records[1].Payload);
    }

    [Fact]
    public void HeaderlessCsvUsesOneBasedColumns()
    {
        var records = Read(new GenerationOptions
        {
            Input = "-", InputFormat = InputFormat.Csv, Delimiter = ";", NoHeader = true,
            Template = "{2}; {3}", LabelTemplate = "{1}: {3}"
        }, "001;Alpha;Beta\n002;Gamma;Delta\n");
        Assert.Equal("Alpha; Beta", records[0].Payload);
        Assert.Equal("001: Beta", records[0].Label);
        Assert.Equal("Gamma; Delta", records[1].Payload);
    }

    [Fact]
    public void CsvTemplatesEscapeBracesWithoutReinterpretingFieldContents()
    {
        var records = Read(new GenerationOptions
        {
            Input = "-", InputFormat = InputFormat.Csv, Template = "{{\"id\":\"{id}\"}}/{value}"
        }, "id,value\n001,{id}\n");
        Assert.Equal("{\"id\":\"001\"}/{id}", Assert.Single(records).Payload);
    }

    [Theory]
    [InlineData("{missing}")]
    [InlineData("{id")]
    [InlineData("id}")]
    [InlineData("{}")]
    public void InvalidTemplatesFail(string template)
    {
        Assert.Throws<CliException>(() => Read(new GenerationOptions
        {
            Input = "-", InputFormat = InputFormat.Csv, Template = template
        }, "id\n001\n"));
    }

    [Fact]
    public void SingleColumnCsvNeedsNoSelector()
    {
        var records = Read(new GenerationOptions { Input = "-", InputFormat = InputFormat.Csv }, "url\nhttps://example.com\n");
        Assert.Equal("https://example.com", Assert.Single(records).Payload);
    }

    [Theory]
    [InlineData("id,id\n1,2\n")]
    [InlineData(",name\n1,2\n")]
    [InlineData("id,name\n1\n")]
    [InlineData("id,name\n1,2,3\n")]
    [InlineData("id,name\n,Name\n")]
    [InlineData("id,name\n1,bad\"quote\n")]
    [InlineData("id,name\n\"unterminated,Name\n")]
    public void InvalidCsvFailsWithoutLeakingRecords(string csv)
    {
        var result = Run(["-i", "-", "--input-format", "csv", "--column", "id", "-f", "pdf", "-o", "-"], csv);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Bytes);
        Assert.DoesNotContain("bad\"quote", result.Error);
        Assert.DoesNotContain("unterminated,Name", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n  \n")]
    public void EmptyInputIsAnError(string input)
    {
        var result = Run(["-i", "-", "-f", "pdf", "-o", "-"], input);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void BatchImagesUseStableNumericNamesAndSafeOverwriteRules()
    {
        using var folder = new TemporaryDirectory();
        string[] args = ["-i", "-", "-f", "svg", "-o", folder.Path];
        var first = Run(args, "../../not-a-filename\nanother\n");
        Assert.Equal(0, first.Code);
        Assert.Equal(["000001.svg", "000002.svg"], Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order());
        var original = File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "000001.svg"));
        Assert.Equal(2, Run(args, "replacement\nanother\n").Code);
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "000001.svg")));
        Assert.Equal(0, Run([.. args, "--force"], "replacement\nanother\n").Code);
        Assert.NotEqual(original, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "000001.svg")));
    }

    [Fact]
    public void OversizedLaterRecordDoesNotReplaceExistingBatch()
    {
        using var folder = new TemporaryDirectory();
        var existing = System.IO.Path.Combine(folder.Path, "000001.svg");
        File.WriteAllText(existing, "keep me");
        var result = Run(["-i", "-", "-f", "svg", "-o", folder.Path, "--force"], $"first\n{new string('x', 6000)}\n");
        Assert.Equal(2, result.Code);
        Assert.Contains("record 2", result.Error);
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void TrailingSeparatorCreatesAnExtensionNamedDirectory()
    {
        using var folder = new TemporaryDirectory();
        var output = System.IO.Path.Combine(folder.Path, "batch.png") + System.IO.Path.DirectorySeparatorChar;
        var result = Run(["-i", "-", "-o", output], "one\ntwo\n");
        Assert.Equal(0, result.Code);
        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    [Fact]
    public void DirectoryAliasesCannotBypassInputProtection()
    {
        using var folder = new TemporaryDirectory();
        var realDirectory = System.IO.Path.Combine(folder.Path, "real");
        var aliasDirectory = System.IO.Path.Combine(folder.Path, "alias");
        Directory.CreateDirectory(realDirectory);
        if (OperatingSystem.IsWindows())
        {
            // NTFS junctions do not require Developer Mode or elevated symlink privileges.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{aliasDirectory}\" \"{realDirectory}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
        }
        var input = System.IO.Path.Combine(realDirectory, "input.txt");
        File.WriteAllText(input, "keep me");
        var output = System.IO.Path.Combine(aliasDirectory, "input.txt");
        try
        {
            var result = Run(["-i", input, "-f", "pdf", "-o", output, "--force"]);
            Assert.Equal(2, result.Code);
            Assert.Contains("input file", result.Error);
            Assert.Equal("keep me", File.ReadAllText(input));
        }
        finally
        {
            Directory.Delete(aliasDirectory);
        }
    }

    [Fact]
    public void InputCannotBeOverwrittenEvenWithForce()
    {
        using var folder = new TemporaryDirectory();
        var input = System.IO.Path.Combine(folder.Path, "input.txt");
        File.WriteAllText(input, "keep me");
        var result = Run(["-i", input, "-f", "pdf", "-o", input, "--force"]);
        Assert.Equal(2, result.Code);
        Assert.Equal("keep me", File.ReadAllText(input));
    }

    [Fact]
    public void InputProtectionHandlesCaseInsensitiveFileSystems()
    {
        using var folder = new TemporaryDirectory();
        var input = System.IO.Path.Combine(folder.Path, "input.txt");
        var output = System.IO.Path.Combine(folder.Path, "INPUT.TXT");
        File.WriteAllText(input, "keep me");
        if (!File.Exists(output))
            return;
        var result = Run(["-i", input, "-f", "pdf", "-o", output, "--force"]);
        Assert.Equal(2, result.Code);
        Assert.Equal("keep me", File.ReadAllText(input));
    }

    [Fact]
    public void MultiRecordImagesCannotUseStdoutOrASingleFile()
    {
        using var folder = new TemporaryDirectory();
        var result = Run(["-i", "-", "-f", "png", "-o", "-"], "one\ntwo\n");
        Assert.Equal(2, result.Code);
        var output = System.IO.Path.Combine(folder.Path, "single.png");
        Assert.Equal(2, Run(["-i", "-", "-o", output], "one\ntwo\n").Code);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void PdfPaginatesAndUsesLetterSizeWithEmbeddedUnicodeCaptions()
    {
        var csv = "url,name\n" + string.Join("\n", Enumerable.Range(1, 11)
            .Select(index => $"https://example.com/{index},\u017dlu\u0165ou\u010dk\u00fd {index}"));
        var result = Run([
            "-i", "-", "--input-format", "csv", "--column", "url", "--label-column", "name",
            "-f", "pdf", "-o", "-", "--page-size", "Letter"
        ], csv);
        Assert.Equal(0, result.Code);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(result.Bytes));
        using var stream = new MemoryStream(result.Bytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(612, document.Pages[0].Width.Point, precision: 1);
        Assert.Equal(792, document.Pages[0].Height.Point, precision: 1);
    }

    [Theory]
    [InlineData("--margin", "200")]
    [InlineData("--columns", "20")]
    public void UnusablePdfLayoutFailsWithoutOutput(string option, string value)
    {
        var result = Run(["-t", "hello", "-f", "pdf", "-o", "-", option, value]);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void OverlongCaptionFailsInsteadOfClipping()
    {
        var result = Run(["-t", "hello", "--label", new string('x', 2000), "-f", "pdf", "-o", "-"]);
        Assert.Equal(2, result.Code);
        Assert.Contains("cannot fit", result.Error);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void MissingInputIsAnIoError()
    {
        using var folder = new TemporaryDirectory();
        var result = Run(["-i", System.IO.Path.Combine(folder.Path, "missing.txt"), "-f", "svg", "-o", "-"]);
        Assert.Equal(1, result.Code);
        Assert.Empty(result.Bytes);
    }

    private static IReadOnlyList<QrRecord> Read(GenerationOptions options, string text)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return InputReader.Read(options, input);
    }

    private static RunResult Run(string[] args, string input = "") => RunBytes(args, Encoding.UTF8.GetBytes(input));

    private static RunResult RunBytes(string[] args, byte[] input)
    {
        using var source = new MemoryStream(input);
        using var destination = new MemoryStream();
        using var help = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, source, destination, help, error);
        return new RunResult(code, destination.ToArray(), help.ToString(), error.ToString());
    }

    private sealed record RunResult(int Code, byte[] Bytes, string Help, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qr-bulk-test-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
