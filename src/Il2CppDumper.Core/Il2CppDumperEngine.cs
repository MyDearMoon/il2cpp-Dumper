using System.Diagnostics;
using AssetRipper.Primitives;
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Exporters;
using Il2CppDumper.Core.Metadata;
using Il2CppDumper.Core.Metadata.Moonton;
using Il2CppDumper.Core.Model;
using Il2CppDumper.Core.Runtime;
using LibCpp2IL;

namespace Il2CppDumper.Core;

public sealed class DumpResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan Elapsed { get; set; }
    public DumpContext? Context { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> GeneratedFiles { get; set; } = new();
}

public static class Il2CppDumperEngine
{
    public static DumpResult Execute(
        string inputPath,
        string outputDirectory,
        string? metadataOverride = null,
        Architecture? preferredArch = null,
        ExportOptions? options = null,
        string? unityVersionOverride = null,
        Action<string>? logger = null)
    {
        options ??= ExportOptions.All;
        var sw = Stopwatch.StartNew();
        var result = new DumpResult
        {
            OutputDirectory = outputDirectory
        };

        ExtractionContext? extractionCtx = null;
        try
        {
            logger?.Invoke($"Starting Il2Cpp-Dumper pipeline for: {inputPath}");

            // 1. Container Ingestion & File Extraction
            extractionCtx = PackageExtractor.Ingest(inputPath, metadataOverride, preferredArch, logger);
            logger?.Invoke($"Target binary: {extractionCtx.BinaryPath} ({extractionCtx.Architecture})");

            // Normalize metadata (auto-detect and unwrap envelope/pre-header if present)
            extractionCtx.MetadataPath = MetadataNormalizer.Normalize(extractionCtx.MetadataPath, extractionCtx.TempDirectory, logger);
            logger?.Invoke($"Target metadata: {extractionCtx.MetadataPath}");

            // Validate metadata header magic (detect encryption / anti-tamper)
            ValidateMetadataHeader(extractionCtx.MetadataPath);

            DumpContext dumpContext;
            if (MoontonDumper.IsMoontonMetadata(extractionCtx.MetadataPath))
            {
                dumpContext = MoontonDumper.Dump(extractionCtx.MetadataPath, extractionCtx.BinaryPath, logger);
            }
            else
            {
                // Auto-detect or default unity version
                var unityVersion = default(UnityVersion);
                if (!string.IsNullOrEmpty(unityVersionOverride) && UnityVersionDetector.TryParseVersion(unityVersionOverride, out var parsedVer))
                {
                    unityVersion = parsedVer;
                    logger?.Invoke($"Using specified Unity version: {unityVersion}");
                }
                else
                {
                    unityVersion = UnityVersionDetector.Detect(inputPath, extractionCtx.BinaryPath, extractionCtx.MetadataPath, logger);
                }

                try
                {
                    logger?.Invoke("Parsing binary structures and global-metadata.dat...");
                    var cppContext = LibCpp2IlMain.LoadFromFileAsContext(extractionCtx.BinaryPath, extractionCtx.MetadataPath, unityVersion);

                    if (cppContext == null)
                    {
                        throw new InvalidOperationException("Failed to initialize LibCpp2IL context from provided files.");
                    }

                    // 3. Build Unified Object Model
                    dumpContext = DumpModelBuilder.Build(cppContext, extractionCtx.Architecture, extractionCtx.Format, logger);
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Warning] Binary structure analysis encountered an issue: {ex.Message}");
                    logger?.Invoke("Switching to Metadata Fallback mode (reconstructing all assemblies, types, methods, fields, and strings directly from global-metadata.dat)...");
                    dumpContext = MetadataOnlyDumper.Dump(extractionCtx.MetadataPath, extractionCtx.BinaryPath, unityVersion, logger);
                }
            }
            result.Context = dumpContext;

            // 4. Run Exporters
            Directory.CreateDirectory(outputDirectory);

            var dumpCsExporter = new DumpCsExporter();
            dumpCsExporter.Export(dumpContext, outputDirectory, options, logger);

            var scriptExporter = new ScriptExporter();
            scriptExporter.Export(dumpContext, outputDirectory, options, logger);

            var dummyExporter = new DummyAssemblyExporter();
            try
            {
                dummyExporter.Export(dumpContext, outputDirectory, options, logger);
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Warning] Dummy assembly generation skipped: {ex.Message}");
            }

            var cppSdkExporter = new CppSdkExporter();
            cppSdkExporter.Export(dumpContext, outputDirectory, options, logger);

            if (options.ExportFridaScripts)
            {
                FridaDumpGenerator.GenerateScripts(outputDirectory, logger);
            }

            // Collect generated files
            if (Directory.Exists(outputDirectory))
            {
                result.GeneratedFiles.AddRange(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories));
            }

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.Success = true;
            logger?.Invoke($"Pipeline completed successfully in {sw.Elapsed.TotalSeconds:F2}s!");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            result.Success = false;
            result.ErrorMessage = ex.Message;
            logger?.Invoke($"Error: {ex.Message}");
            return result;
        }
        finally
        {
            extractionCtx?.Dispose();
        }
    }

    private static void ValidateMetadataHeader(string metadataPath)
    {
        using var fs = File.OpenRead(metadataPath);
        var buffer = new byte[4];
        if (fs.Read(buffer, 0, 4) < 4)
            throw new InvalidDataException("global-metadata.dat is too small or truncated.");

        var magic = BitConverter.ToUInt32(buffer, 0);
        if (magic != 0xFAB11BAF)
        {
            if (buffer[0] == 0x4D && buffer[1] == 0x48 && buffer[2] == 0x59) // "MHY"
            {
                throw new InvalidOperationException(
                    "Detected HoYoverse encrypted metadata (starts with 'MHY\\0')!\n" +
                    "HoYoverse games (Zenless Zone Zero, Genshin Impact, Honkai: Star Rail) encrypt global-metadata.dat on disk.\n" +
                    "Static dumpers cannot read disk files directly. You must dump the decrypted global-metadata.dat from memory at runtime.");
            }

            throw new InvalidOperationException(
                $"global-metadata.dat is encrypted or obfuscated (Magic: 0x{magic:X8} instead of 0xFAB11BAF).\n" +
                "Use a runtime memory dumper (or the bundled Frida script) to dump the decrypted metadata from RAM at runtime.");
        }
    }
}
