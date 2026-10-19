using System.Text;

// Diagnostics quote names as written, which can be any Unicode text.
Console.OutputEncoding = new UTF8Encoding(false);
return Wolfden.Cli.CliApp.Run(args, Console.Out, Console.Error);
