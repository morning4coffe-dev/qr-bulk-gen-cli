using System.Text;
using QRBulkGen.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
return CliApplication.Run(
    args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Out, Console.Error);
