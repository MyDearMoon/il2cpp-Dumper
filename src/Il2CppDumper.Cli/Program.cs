using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppDumper.Core;
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Exporters;
using Spectre.Console;
using Architecture = Il2CppDumper.Core.Containers.Architecture;

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
        AnsiConsole.MarkupLine("[bold yellow]Usage:[/] [cyan]il2cpp-dumper[/] [grey][[input]] [[metadata]] [[output]] [[options]][/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold yellow]Positional Arguments (No flags required):[/]");
        AnsiConsole.MarkupLine("  [cyan]il2cpp-dumper[/] [grey]<game_folder | game.apk | game.xapk>[/]");
        AnsiConsole.MarkupLine("  [cyan]il2cpp-dumper[/] [grey]<GameAssembly.dll | libil2cpp.so> <global-metadata.dat>[/]");
        AnsiConsole.MarkupLine("  [cyan]il2cpp-dumper[/] [grey]<GameAssembly.dll> <global-metadata.dat> <output_dir>[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold yellow]Options:[/] ");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[cyan]Option[/]");
        table.AddColumn("[cyan]Description[/]");

        table.AddRow("-i, --input <path>", "Input file (APK, XAPK, APKM, IPA, ZIP, Game Folder, libil2cpp.so, GameAssembly.dll)");
        table.AddRow("-m, --metadata <path>", "Optional explicit path to global-metadata.dat");
        table.AddRow("-o, --output <path>", "Output directory (defaults to './dump')");
        table.AddRow("-a, --arch <name>", "Preferred architecture: arm64, armv7, x64, x86");
        table.AddRow("-u, --unity <version>", "Override detected Unity version (e.g. 2021.3.56)");
        table.AddRow("--all", "Export all formats (dump.cs, scripts, dummy DLLs, C++ SDK, Frida) [Default]");
        table.AddRow("--dump-cs", "Export dump.cs and script.json only");
        table.AddRow("--scripts", "Export IDA Pro, Ghidra, and Binary Ninja Python scripts");
        table.AddRow("--dummy", "Export Dummy DLL assemblies via Mono.Cecil (for dnSpy/BepInEx)");
        table.AddRow("--cpp", "Export C++ Modding SDK (il2cpp.h and hooking scaffolding)");
        table.AddRow("--frida", "Export Frida runtime memory dumping scripts");
        table.AddRow("--interactive", "Launch interactive guided wizard");
        table.AddRow("-h, --help", "Show this help message");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static int RunInteractive()
    {
        AnsiConsole.MarkupLine("[bold green]Interactive Mode[/]");
        AnsiConsole.MarkupLine("[grey]Drag and drop your APK, IPA, game directory, or GameAssembly.dll into this window and press Enter:[/]");

        var inputRaw = AnsiConsole.Ask<string>("[bold yellow]Target Input:[/] ");
        var inputPath = CleanInputPath(inputRaw);

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Path does not exist: {inputPath}");
            return 1;
        }

        string? metadataPath = null;
        if (File.Exists(inputPath) && !PackageExtractor.IsArchive(inputPath) && !inputPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(inputPath) ?? ".";
            var hasNearby = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "global-metadata.dat", SearchOption.AllDirectories).Any();
            if (!hasNearby)
            {
                var metaRaw = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold yellow]Path to global-metadata.dat (press Enter if in same folder):[/] ")
                        .AllowEmpty());
                if (!string.IsNullOrWhiteSpace(metaRaw))
                {
                    metadataPath = CleanInputPath(metaRaw);
                }
            }
        }

        // Output directory prompt
        var defaultOut = Path.Combine(
            Directory.Exists(inputPath) ? inputPath : (Path.GetDirectoryName(inputPath) ?? "."),
            "dump");
        var outputRaw = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold yellow]Output Directory:[/] ")
                .DefaultValue(defaultOut));
        var outputDir = CleanInputPath(outputRaw);

        // Architecture selection for split archives
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

        return ExecuteDumper(inputPath, outputDir, metadataPath, preferredArch, options, isInteractive: true);
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
        var positionalArgs = new List<string>();

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
            else if (!arg.StartsWith('-'))
            {
                positionalArgs.Add(CleanInputPath(arg));
            }
        }

        // Positional argument binding:
        // 1 arg:  <input>
        // 2 args: <binary> <metadata>
        // 3 args: <binary> <metadata> <output>
        if (inputPath == null && positionalArgs.Count > 0)
        {
            inputPath = positionalArgs[0];
            if (positionalArgs.Count > 1 && metadataPath == null)
            {
                metadataPath = positionalArgs[1];
            }
            if (positionalArgs.Count > 2 && outputDir == null)
            {
                outputDir = positionalArgs[2];
            }
        }

        if (string.IsNullOrEmpty(inputPath))
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] No input specified. Drag-and-drop a file or use: il2cpp-dumper <input>");
            PrintHelp();
            return 1;
        }

        outputDir ??= Path.Combine(Directory.Exists(inputPath) ? inputPath : (Path.GetDirectoryName(inputPath) ?? "."), "dump");

        if (!hasSpecificExport)
        {
            options = ExportOptions.All;
        }

        // If invoked via single-argument drag-and-drop in Windows Explorer, pause before closing window
        var isDragAndDrop = args.Length == 1 && !args[0].StartsWith('-');

        return ExecuteDumper(inputPath, outputDir, metadataPath, preferredArch, options, unityVersion, isDragAndDrop);
    }

    private static int ExecuteDumper(
        string inputPath,
        string outputDir,
        string? metadataPath,
        Architecture? preferredArch,
        ExportOptions options,
        string? unityVersion = null,
        bool isInteractive = false)
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
                    msg =>
                    {
                        ctx.Status($"[bold cyan]{Markup.Escape(msg)}[/]");
                        AnsiConsole.MarkupLine($"[grey][[[/][blue]{DateTime.Now:HH:mm:ss}[/][grey]]][/] {Markup.Escape(msg)}");
                    });
            });

        if (result == null || !result.Success)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold red]Dumper failed:[/] {Markup.Escape(result?.ErrorMessage ?? "Unknown error")}");

            if (isInteractive && !Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
                try { Console.ReadKey(true); } catch { }
            }
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
        AnsiConsole.MarkupLine($"[bold green]Success![/] All files generated in: [cyan]{result.OutputDirectory}[/]");

        if (isInteractive && !Console.IsInputRedirected && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AnsiConsole.WriteLine();
            if (AnsiConsole.Confirm("Open output folder in File Explorer?", defaultValue: true))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = result.OutputDirectory,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Ignore explorer open failure
                }
            }
        }

        if (isInteractive && !Console.IsInputRedirected)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Press any key to exit...[/]");
            try { Console.ReadKey(true); } catch { }
        }

        return 0;
    }

    private static string CleanInputPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var cleaned = raw.Trim();

        // Handle PowerShell drag-and-drop prefix: & '...'
        if (cleaned.StartsWith('&'))
        {
            cleaned = cleaned[1..].Trim();
        }

        // Strip single and double quotes added by Windows drag-and-drop
        cleaned = cleaned.Trim('"', '\'');

        return Path.GetFullPath(cleaned);
    }
}
