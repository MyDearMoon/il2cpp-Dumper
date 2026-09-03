using System.Diagnostics;
using Il2CppDumper.Core;
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Exporters;
using Spectre.Console;

namespace Il2CppDumper.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        PrintBanner();

        if (args.Length == 0 || args.Contains("--interactive"))
        {
            return RunInteractive();
        }

        if (args.Contains("-h") || args.Contains("--help"))
        {
            PrintHelp();
            return 0;
        }

        return RunCommandLine(args);
    }

    private static void PrintBanner()
    {
        AnsiConsole.Write(
            new FigletText("Il2CppDumper")
                .Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold cyan]Il2CppDumper[/] [bold grey]v1.0.0[/]");
        AnsiConsole.WriteLine();
    }

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold yellow]Usage:[/] [cyan]il2cpp-dumper[/] [grey][[options]][/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Options:[/] ");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[cyan]Option[/]");
        table.AddColumn("[cyan]Description[/]");

        table.AddRow("-i, --input <path>", "Input file (APK, XAPK, APKM, IPA, ZIP, Game Folder, libil2cpp.so, GameAssembly.dll)");
        table.AddRow("-m, --metadata <path>", "Optional override for global-metadata.dat path");
        table.AddRow("-o, --output <path>", "Output directory (defaults to './dump')");
        table.AddRow("-a, --arch <name>", "Preferred architecture: arm64, armv7, x64, x86");
        table.AddRow("--all", "Export all formats (dump.cs, scripts, dummy DLLs, C++ SDK, Frida)");
        table.AddRow("--dump-cs", "Export dump.cs and script.json");
        table.AddRow("--scripts", "Export IDA Pro, Ghidra, and Binary Ninja Python scripts");
        table.AddRow("--dummy", "Export Dummy DLL assemblies via Mono.Cecil (for dnSpy/BepInEx)");
        table.AddRow("--cpp", "Export C++ Modding SDK (il2cpp.h and hooking scaffolding)");
        table.AddRow("--frida", "Export Frida runtime memory dumping scripts for encrypted games");
        table.AddRow("--interactive", "Launch interactive wizard mode");
        table.AddRow("-h, --help", "Show this help message");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static int RunInteractive()
    {
        AnsiConsole.MarkupLine("[bold green]>>> Interactive Mode[/]");
        AnsiConsole.MarkupLine("[grey]Drag and drop your APK, IPA, game directory, or binary file into this window and press Enter:[/]");

        var inputRaw = AnsiConsole.Ask<string>("[bold yellow]Target Input:[/] ");
        var inputPath = CleanInputPath(inputRaw);

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Path does not exist: {inputPath}");
            return 1;
        }

        // Output directory prompt
        var defaultOut = Path.Combine(
            Directory.Exists(inputPath) ? inputPath : (Path.GetDirectoryName(inputPath) ?? "."),
            "dump");
        var outputRaw = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold yellow]Output Directory:[/] ")
                .DefaultValue(defaultOut));
        var outputDir = CleanInputPath(outputRaw);

        // Architecture selection
        Architecture? preferredArch = null;
        if (PackageExtractor.IsArchive(inputPath))
        {
            var archChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold yellow]Select Preferred Architecture:[/] ")
                    .AddChoices("arm64-v8a (64-bit ARM - Recommended)", "armeabi-v7a (32-bit ARM)", "x86_64", "x86", "Auto-Detect"));

            preferredArch = archChoice switch
            {
                var s when s.StartsWith("arm64") => Architecture.Arm64,
                var s when s.StartsWith("armeabi") => Architecture.Armv7,
                var s when s.StartsWith("x86_64") => Architecture.X64,
                var s when s.StartsWith("x86") => Architecture.X86,
                _ => null
            };
        }

        // Export selection
        var choices = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[bold yellow]Select Components to Export:[/] ")
                .PageSize(10)
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                .AddChoices(new[]
                {
                    "C# Static Dump (dump.cs, script.json, stringliteral.json)",
                    "Disassembler Scripts (IDA Pro, Ghidra, Binary Ninja)",
                    "Dummy DLL Assemblies (Mono.Cecil stubs for dnSpy / BepInEx)",
                    "C++ Modding SDK (il2cpp.h, il2cpp-init.h, VS scaffolding)",
                    "Frida Runtime Memory Dumper (Anti-Cheat / Encryption Bypass)"
                }));

        var options = new ExportOptions
        {
            ExportDumpCs = choices.Any(c => c.StartsWith("C# Static Dump")),
            ExportScriptJson = choices.Any(c => c.StartsWith("C# Static Dump")),
            ExportIdaScript = choices.Any(c => c.StartsWith("Disassembler")),
            ExportGhidraScript = choices.Any(c => c.StartsWith("Disassembler")),
            ExportBinjaScript = choices.Any(c => c.StartsWith("Disassembler")),
            ExportDummyDlls = choices.Any(c => c.StartsWith("Dummy DLL")),
            ExportCppSdk = choices.Any(c => c.StartsWith("C++ Modding")),
            ExportFridaScripts = choices.Any(c => c.StartsWith("Frida"))
        };

        return ExecuteDumper(inputPath, outputDir, null, preferredArch, options);
    }

    private static int RunCommandLine(string[] args)
    {
        string? inputPath = null;
        string? metadataPath = null;
        string? outputDir = null;
        Architecture? preferredArch = null;
        string? unityVersion = null;

        var options = new ExportOptions
        {
            ExportDumpCs = false,
            ExportScriptJson = false,
            ExportIdaScript = false,
            ExportGhidraScript = false,
            ExportBinjaScript = false,
            ExportDummyDlls = false,
            ExportCppSdk = false,
            ExportFridaScripts = false
        };

        var hasSpecificExport = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg == "-i" || arg == "--input") && i + 1 < args.Length)
            {
                inputPath = CleanInputPath(args[++i]);
            }
            else if ((arg == "-m" || arg == "--metadata") && i + 1 < args.Length)
            {
                metadataPath = CleanInputPath(args[++i]);
            }
            else if ((arg == "-o" || arg == "--output") && i + 1 < args.Length)
            {
                outputDir = CleanInputPath(args[++i]);
            }
            else if ((arg == "-u" || arg == "--unity") && i + 1 < args.Length)
            {
                unityVersion = args[++i].Trim();
            }
            else if ((arg == "-a" || arg == "--arch") && i + 1 < args.Length)
            {
                var archStr = args[++i].ToLowerInvariant();
                preferredArch = archStr switch
                {
                    "arm64" or "aarch64" => Architecture.Arm64,
                    "arm" or "armv7" => Architecture.Armv7,
                    "x64" or "x86_64" => Architecture.X64,
                    "x86" => Architecture.X86,
                    _ => null
                };
            }
            else if (arg == "--all")
            {
                options = ExportOptions.All;
                hasSpecificExport = true;
            }
            else if (arg == "--dump-cs")
            {
                options.ExportDumpCs = true;
                options.ExportScriptJson = true;
                hasSpecificExport = true;
            }
            else if (arg == "--scripts")
            {
                options.ExportIdaScript = true;
                options.ExportGhidraScript = true;
                options.ExportBinjaScript = true;
                hasSpecificExport = true;
            }
            else if (arg == "--dummy")
            {
                options.ExportDummyDlls = true;
                hasSpecificExport = true;
            }
            else if (arg == "--cpp")
            {
                options.ExportCppSdk = true;
                hasSpecificExport = true;
            }
            else if (arg == "--frida")
            {
                options.ExportFridaScripts = true;
                hasSpecificExport = true;
            }
            else if (!arg.StartsWith('-') && inputPath == null)
            {
                inputPath = CleanInputPath(arg);
            }
        }

        if (string.IsNullOrEmpty(inputPath))
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] No input specified. Use -i <path> or run without arguments for interactive mode.");
            return 1;
        }

        outputDir ??= Path.Combine(Directory.Exists(inputPath) ? inputPath : (Path.GetDirectoryName(inputPath) ?? "."), "dump");

        if (!hasSpecificExport)
        {
            options = ExportOptions.All;
        }

        return ExecuteDumper(inputPath, outputDir, metadataPath, preferredArch, options, unityVersion);
    }

    private static int ExecuteDumper(
        string inputPath,
        string outputDir,
        string? metadataPath,
        Architecture? preferredArch,
        ExportOptions options,
        string? unityVersion = null)
    {
        DumpResult? result = null;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[bold cyan]Processing Unity IL2CPP target...[/]", ctx =>
            {
                result = Il2CppDumperEngine.Execute(
                    inputPath,
                    outputDir,
                    metadataPath,
                    preferredArch,
                    options,
                    unityVersion,
                    msg => AnsiConsole.MarkupLine($"[grey][[[/][blue]{DateTime.Now:HH:mm:ss}[/][grey]]][/] {Markup.Escape(msg)}"));
            });

        if (result == null || !result.Success)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold red]Dumper failed:[/] {Markup.Escape(result?.ErrorMessage ?? "Unknown error")}");
            return 1;
        }

        // Display results table
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.Title("[bold green]Dumping Pipeline Complete[/]");
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Value[/]");

        if (result.Context != null)
        {
            table.AddRow("Metadata Version", $"v{result.Context.MetadataVersion}");
            table.AddRow("Unity Version", result.Context.UnityVersion);
            table.AddRow("Architecture", result.Context.Architecture.ToString());
            table.AddRow("Binary Format", result.Context.Format.ToString());
            table.AddRow("Assemblies / Images", result.Context.TotalImages.ToString());
            table.AddRow("Types Dumped", result.Context.TotalTypes.ToString("N0"));
            table.AddRow("Methods Dumped", result.Context.TotalMethods.ToString("N0"));
            table.AddRow("Fields Dumped", result.Context.TotalFields.ToString("N0"));
            table.AddRow("String Literals", result.Context.TotalStringLiterals.ToString("N0"));
        }

        table.AddRow("Elapsed Time", $"{result.Elapsed.TotalSeconds:F2} seconds");
        table.AddRow("Output Directory", $"[link={result.OutputDirectory}]{result.OutputDirectory}[/]");
        table.AddRow("Files Generated", result.GeneratedFiles.Count.ToString());

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold green]Success![/] All requested files generated in: [cyan]{result.OutputDirectory}[/]");
        return 0;
    }

    private static string CleanInputPath(string raw)
    {
        var cleaned = raw.Trim().Trim('"', '\'');
        return Path.GetFullPath(cleaned);
    }
}
