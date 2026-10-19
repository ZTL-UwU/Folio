// Recto's developer tools. See docs/tools.md.
//
//   bench [options]          how fast PDF viewers show a document and how much memory they use, and how fast
//                            File Explorer thumbnail handlers make a thumbnail of it
//   chart <results.json>     redraws the chart of an earlier bench run
//   icon                     rebuilds the app icon files from tools/Recto.Tools/AppIcon
using Recto.Tools;
using Recto.Tools.Bench;

const string Usage = """
    Usage: dotnet run --project tools/Recto.Tools -- <command> [options]

      bench   Times PDF viewers opening the benchmark documents and measures their memory, then times
              File Explorer thumbnail handlers (Recto's, Acrobat's, SumatraPDF's and PowerToys') making thumbnails of them.
              --viewer or --thumbnail to measure only that part.
              --apps Recto,Edge,Acrobat,Sumatra,PowerToys   --runs 5   --warmup 1   --recto <exe>
              --thumbnail-size 256   --close-running   --save-frames   --settle 5   --timeout 60   --out <dir>
      chart   Redraws a bench chart: chart <results.json> [--apps Recto,Edge] [--out <png>]
      icon    Rebuilds src/Recto/Assets/AppIcon.* and the package logos from tools/Recto.Tools/AppIcon.

    See docs/tools.md.
    """;

try
{
    var command = args.FirstOrDefault();
    var options = new Options(args.Skip(1));
    Func<Options, Task>? run = command switch
    {
        "bench" => BenchCommand.RunAsync,
        "chart" => ChartCommand.RunAsync,
        "icon" => IconCommand.RunAsync,
        "thumbnail-host" => ThumbnailHost.ServeAsync,
        _ => null,
    };
    if (run is null)
    {
        Console.Error.WriteLine(Usage);
        return command is null or "help" or "--help" or "-h" ? 0 : 2;
    }
    options.CheckKnown(command!);
    await run(options);
    return 0;
}
catch (ToolException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
