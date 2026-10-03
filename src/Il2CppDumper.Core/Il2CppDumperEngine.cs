using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AssetRipper.Primitives;
using Il2CppDumper.Core.Common;
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
    public MetadataRecoveryResult? RecoveryResult { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public List<string> GeneratedFiles { get; set; } = new();
    public List<ExportResult> ExporterResults { get; set; } = new();
}

public sealed class DumpManifest
{
    public string ToolVersion { get; set; } = "1.4.0";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public float MetadataVersion { get; set; }
    public string UnityVersion { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string BinaryFormat { get; set; } = string.Empty;
    public string AnalysisMode { get; set; } = string.Empty;
    public string AddressConfidence { get; set; } = string.Empty;
    public string RecoveryMethod { get; set; } = string.Empty;
    public long RecoveryOffset { get; set; }
    public int RecoveryConfidence { get; set; }
    public string InputBinaryPath { get; set; } = string.Empty;
    public string InputBinarySha256 { get; set; } = string.Empty;
    public string InputMetadataPath { get; set; } = string.Empty;
    public string InputMetadataSha256 { get; set; } = string.Empty;
    public List<string> GeneratedFiles { get; set; } = new();
    public List<ExportResult> ExporterResults { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
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
        MetadataRecoveryOptions? recoveryOptions = null,
        Action<string>? logger = null)
    {
        options ??= ExportOptions.All;
        recoveryOptions ??= new MetadataRecoveryOptions();
        var sw = Stopwatch.StartNew();
        var result = new DumpResult
        {
            OutputDirectory = outputDirectory
        };

        using var tempWorkspace = new TempWorkspace();
        ExtractionContext? extractionCtx = null;
        try
        {
            logger?.Invoke($"Starting Il2Cpp-Dumper pipeline for: {inputPath}");

            // 1. Output Hygiene: Clean known stale artifacts (Part 15)
            CleanOutputDirectory(outputDirectory, logger);

            // 2. Container Ingestion & File Extraction
            extractionCtx = PackageExtractor.Ingest(inputPath, metadataOverride, preferredArch, logger);
            if (!string.IsNullOrEmpty(extractionCtx.TempDirectory))
            {
                tempWorkspace.TrackDirectory(extractionCtx.TempDirectory);
            }
            logger?.Invoke($"Target binary: {extractionCtx.BinaryPath} ({extractionCtx.Architecture}, {extractionCtx.Format})");

            // 3. Normalize and recover metadata
            var recovery = MetadataNormalizer.Normalize(extractionCtx.MetadataPath, recoveryOptions, tempWorkspace.WorkspaceRoot, logger);
            result.RecoveryResult = recovery;

            if (!recovery.Success)
            {
                throw new InvalidOperationException(recovery.Diagnostic ?? "Metadata normalization and recovery failed.");
            }

            if (recovery.WasNormalized)
            {
                tempWorkspace.TrackFile(recovery.ResultPath);
            }
            extractionCtx.MetadataPath = recovery.ResultPath;
            logger?.Invoke($"Target metadata: {extractionCtx.MetadataPath}");

            if (recovery.Method != MetadataRecoveryMethod.Standard)
            {
                logger?.Invoke($"[Recovery] Recovered metadata via {recovery.Method} (Version: {recovery.Version}, Confidence: {recovery.ConfidenceScore}%)");
            }

            DumpContext dumpContext;
            if (MoontonDumper.IsMoontonMetadata(extractionCtx.MetadataPath))
            {
                dumpContext = MoontonDumper.Dump(extractionCtx.MetadataPath, extractionCtx.BinaryPath, logger);
            }
            else
            {
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

                    dumpContext = DumpModelBuilder.Build(cppContext, extractionCtx.Architecture, extractionCtx.Format, logger);
                }
                // Parse failure classification (Part 18): Only legitimate binary parsing limitations fallback to metadata-only
                catch (Exception ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or OutOfMemoryException or ArgumentNullException or NullReferenceException))
                {
                    logger?.Invoke($"[Warning] Binary structure analysis encountered an issue: {ex.Message}");
                    logger?.Invoke("Switching to Metadata Fallback mode (reconstructing all assemblies, types, methods, fields, and strings directly from global-metadata.dat)...");
                    dumpContext = MetadataOnlyDumper.Dump(extractionCtx.MetadataPath, extractionCtx.BinaryPath, unityVersion, logger);
                }
            }

            // Set provenance info on model
            dumpContext.RecoveryMethod = recovery.Method;
            dumpContext.RecoveryOffset = recovery.Offset;
            dumpContext.RecoveryConfidenceScore = recovery.ConfidenceScore;
            result.Context = dumpContext;

            // 4. Run Exporters Independently (Part 14)
            Directory.CreateDirectory(outputDirectory);

            var dumpCsExporter = new DumpCsExporter();
            var dumpCsRes = dumpCsExporter.Export(dumpContext, outputDirectory, options, logger);
            result.ExporterResults.Add(dumpCsRes);

            var scriptExporter = new ScriptExporter();
            var scriptRes = scriptExporter.Export(dumpContext, outputDirectory, options, logger);
            result.ExporterResults.Add(scriptRes);

            var dummyExporter = new DummyAssemblyExporter();
            var dummyRes = dummyExporter.Export(dumpContext, outputDirectory, options, logger);
            result.ExporterResults.Add(dummyRes);

            var cppSdkExporter = new CppSdkExporter();
            var cppSdkRes = cppSdkExporter.Export(dumpContext, outputDirectory, options, logger);
            result.ExporterResults.Add(cppSdkRes);

            if (options.ExportFridaScripts)
            {
                try
                {
                    FridaDumpGenerator.GenerateScripts(outputDirectory, logger);
                    var fridaDir = Path.Combine(outputDirectory, "frida-runtime-dumper");
                    var fridaFiles = Directory.Exists(fridaDir)
                        ? Directory.GetFiles(fridaDir, "*", SearchOption.AllDirectories).ToList()
                        : new List<string>();

                    result.ExporterResults.Add(new ExportResult
                    {
                        Name = "Frida Scripts",
                        Success = true,
                        GeneratedFiles = fridaFiles
                    });
                }
                catch (Exception ex)
                {
                    result.ExporterResults.Add(new ExportResult
                    {
                        Name = "Frida Scripts",
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            // Collect all generated files (excluding VCS directories like .git)
            if (Directory.Exists(outputDirectory))
            {
                var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) &&
                                !f.Contains("/.git/") &&
                                !f.EndsWith(Path.DirectorySeparatorChar + ".git"));
                result.GeneratedFiles.AddRange(files);
            }

            // 5. Generate dump-manifest.json (Part 13)
            var manifest = new DumpManifest
            {
                MetadataVersion = dumpContext.MetadataVersion,
                UnityVersion = dumpContext.UnityVersion,
                Architecture = dumpContext.Architecture.ToString(),
                BinaryFormat = dumpContext.Format.ToString(),
                AnalysisMode = dumpContext.AnalysisMode.ToString(),
                AddressConfidence = dumpContext.AddressConfidence.ToString(),
                RecoveryMethod = recovery.Method.ToString(),
                RecoveryOffset = recovery.Offset,
                RecoveryConfidence = recovery.ConfidenceScore,
                InputBinaryPath = extractionCtx.BinaryPath,
                InputBinarySha256 = ComputeSha256(extractionCtx.BinaryPath),
                InputMetadataPath = extractionCtx.MetadataPath,
                InputMetadataSha256 = ComputeSha256(extractionCtx.MetadataPath),
                GeneratedFiles = result.GeneratedFiles.ToList(),
                ExporterResults = result.ExporterResults.ToList(),
                Warnings = result.ExporterResults.SelectMany(r => r.Warnings).ToList()
            };

            var manifestPath = Path.Combine(outputDirectory, "dump-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.GeneratedFiles.Contains(manifestPath))
            {
                result.GeneratedFiles.Add(manifestPath);
            }
            logger?.Invoke($"Wrote: {manifestPath}");

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            var failureCount = result.ExporterResults.Count(r => !r.Success);
            result.Success = failureCount == 0;

            if (failureCount > 0)
            {
                logger?.Invoke($"Dump completed with {failureCount} exporter failure(s) in {sw.Elapsed.TotalSeconds:F2}s.");
            }
            else
            {
                logger?.Invoke($"Pipeline completed successfully in {sw.Elapsed.TotalSeconds:F2}s!");
            }

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

    private static void CleanOutputDirectory(string outputDirectory, Action<string>? logger)
    {
        if (!Directory.Exists(outputDirectory)) return;

        var knownFiles = new[]
        {
            "dump.cs",
            "script.json",
            "stringliteral.json",
            "dump-manifest.json",
            "ida.py",
            "ghidra.py",
            "binja.py"
        };

        foreach (var file in knownFiles)
        {
            var p = Path.Combine(outputDirectory, file);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }

        var knownDirs = new[] { "cpp-sdk", "DummyDll", "frida-scripts", "frida-runtime-dumper" };
        foreach (var dir in knownDirs)
        {
            var p = Path.Combine(outputDirectory, dir);
            if (Directory.Exists(p))
            {
                try { Directory.Delete(p, true); } catch { }
            }
        }
    }

    private static string ComputeSha256(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return string.Empty;
        try
        {
            using var fs = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
