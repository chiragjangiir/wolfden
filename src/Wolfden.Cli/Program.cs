using System.Text;
using Wolfden.Cli;

// Diagnostics quote names as written, which can be any Unicode text.
Console.OutputEncoding = new UTF8Encoding(false);

// Ctrl+C stops a command that runs until stopped (index --watch) cleanly,
// rather than ending the process in the middle of writing the index. A
// second Ctrl+C, or Ctrl+C during any other command, ends it as usual.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (CliApp.RunsUntilStopped && !stop.IsCancellationRequested)
    {
        e.Cancel = true;
        stop.Cancel();
    }
};

return CliApp.RunUntil(args, Console.Out, Console.Error, stop.Token);
