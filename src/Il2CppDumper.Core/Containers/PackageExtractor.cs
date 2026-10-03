using System.IO.Compression;
using Il2CppDumper.Core.Metadata;

namespace Il2CppDumper.Core.Containers;

public static class PackageExtractor
{
    private const long MaxArchiveEntries = 100_000;
    private const long MaxExtractedSize = 8L * 1024 * 1024 * 1024; // 8 GB
    private const long MaxCompressionRatio = 100; // 100:1

    public static bool IsBinaryCandidate(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower == "gameassembly.dll") return true;
        if (lower.EndsWith(".so") && lower.Contains("il2cpp")) return true;
        if (lower == "unityframework" || lower.EndsWith(".dylib")) return true;
        return false;
    }

    public static bool IsFallbackBinaryCandidate(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower == "libunity.so";
    }

    public static bool IsMetadataCandidate(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower == "global-metadata.dat") return true;
        if (lower.EndsWith(".dat") && lower.Contains("metadata")) return true;
        return false;
    }

    public static bool IsArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".apk" or ".xapk" or ".apkm" or ".ipa" or ".zip";
    }

    public static bool IsDirectory(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    public static Architecture DetectArchitectureFromPath(string path)
    {
        var lower = path.ToLowerInvariant().Replace('\\', '/');
        if (lower.Contains("arm64-v8a") || lower.Contains("arm64_v8a") || lower.Contains("aarch64"))
            return Architecture.Arm64;
        if (lower.Contains("armeabi-v7a") || lower.Contains("armeabi_v7a") || lower.Contains("armeabi") || lower.Contains("armv7"))
            return Architecture.Armv7;
        if (lower.Contains("x86_64") || lower.Contains("x64") || lower.Contains("amd64"))
            return Architecture.X64;
        if (lower.Contains("x86") || lower.Contains("i386") || lower.Contains("i686"))
            return Architecture.X86;
        if (lower.Contains("wasm"))
            return Architecture.Wasm;

        return Architecture.Unknown;
    }

    public static BinaryFormat DetectFormat(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".dll" or ".exe") return BinaryFormat.PE;
        if (ext == ".so") return BinaryFormat.Elf;
        if (ext == ".wasm") return BinaryFormat.Wasm;
        if (fileName.Equals("unityframework", StringComparison.OrdinalIgnoreCase) || ext == ".dylib") return BinaryFormat.MachO;
        return BinaryFormat.Unknown;
    }

    public static ExtractionContext Ingest(
        string inputPath,
        string? metadataOverride = null,
        Architecture? preferredArch = null,
        Action<string>? logger = null)
    {
        var ctx = new ExtractionContext
        {
            OriginalInput = inputPath
        };

        if (IsArchive(inputPath))
        {
            logger?.Invoke($"Inspecting archive container: {Path.GetFileName(inputPath)}...");
            ExtractFromArchive(inputPath, ctx, preferredArch, logger);
        }
        else if (IsDirectory(inputPath))
        {
            logger?.Invoke($"Scanning directory: {inputPath}...");
            DetectFromDirectory(inputPath, ctx, preferredArch, logger);
        }
        else if (File.Exists(inputPath))
        {
            logger?.Invoke($"Inspecting direct file: {Path.GetFileName(inputPath)}...");
            DetectFromFile(inputPath, metadataOverride, ctx, logger);
        }
        else
        {
            throw new FileNotFoundException($"Input path does not exist: {inputPath}");
        }

        if (!string.IsNullOrEmpty(metadataOverride) && File.Exists(metadataOverride))
        {
            ctx.MetadataPath = metadataOverride;
        }

        if (string.IsNullOrEmpty(ctx.BinaryPath) || !File.Exists(ctx.BinaryPath))
        {
            throw new FileNotFoundException("Failed to locate IL2CPP binary (libil2cpp.so, GameAssembly.dll, UnityFramework, or libunity.so) in input.");
        }

        if (string.IsNullOrEmpty(ctx.MetadataPath) || !File.Exists(ctx.MetadataPath))
        {
            throw new FileNotFoundException("Failed to locate global-metadata.dat in input.");
        }

        return ctx;
    }

    private static void ValidateArchiveSafety(string archivePath, ZipArchive zip)
    {
        if (zip.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidOperationException($"Archive exceeds safety limit of {MaxArchiveEntries} entries (actual: {zip.Entries.Count}).");
        }

        long totalUncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            totalUncompressed += entry.Length;
            if (totalUncompressed > MaxExtractedSize)
            {
                throw new InvalidOperationException($"Archive exceeds safety limit of {MaxExtractedSize / 1024 / 1024 / 1024} GB uncompressed size.");
            }
        }

        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length > 1024 * 1024 && totalUncompressed > MaxCompressionRatio * fileInfo.Length)
        {
            throw new InvalidOperationException("Archive compression ratio exceeds safety threshold (possible zip bomb).");
        }
    }

    private static void ExtractFromArchive(
        string archivePath,
        ExtractionContext ctx,
        Architecture? preferredArch,
        Action<string>? logger)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp_dumper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ctx.TempDirectory = tempDir;

        using var zip = ZipFile.OpenRead(archivePath);
        ValidateArchiveSafety(archivePath, zip);

        // 1. Check for nested APKs in XAPK / APKM
        var nestedApks = zip.Entries.Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();

        // 2. Discover binaries
        foreach (var entry in zip.Entries)
        {
            var fileName = Path.GetFileName(entry.FullName);
            if (IsBinaryCandidate(fileName))
            {
                var arch = DetectArchitectureFromPath(entry.FullName);
                var fmt = DetectFormat(fileName);

                // Use header-based inspection on the entry stream
                try
                {
                    using var s = entry.Open();
                    var id = BinaryInspector.Inspect(s);
                    if (id.Format != BinaryFormat.Unknown) fmt = id.Format;
                    if (id.Architecture != Architecture.Unknown) arch = id.Architecture;
                }
                catch
                {
                    // Fall back to path-based detection
                }

                ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                {
                    Name = fileName,
                    RelativePath = entry.FullName,
                    Architecture = arch,
                    Format = fmt == BinaryFormat.Unknown ? BinaryFormat.Elf : fmt,
                    Size = entry.Length,
                    ArchiveEntryName = entry.FullName
                });
            }
        }

        // Search nested APKs if none found in root
        if (ctx.DiscoveredBinaries.Count == 0 && nestedApks.Count > 0)
        {
            foreach (var apkEntry in nestedApks)
            {
                using var stream = apkEntry.Open();
                using var nestedZip = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in nestedZip.Entries)
                {
                    var fileName = Path.GetFileName(entry.FullName);
                    if (IsBinaryCandidate(fileName))
                    {
                        var arch = DetectArchitectureFromPath(entry.FullName);
                        if (arch == Architecture.Unknown)
                            arch = DetectArchitectureFromPath(apkEntry.FullName);

                        var fmt = DetectFormat(fileName);
                        try
                        {
                            using var s = entry.Open();
                            var id = BinaryInspector.Inspect(s);
                            if (id.Format != BinaryFormat.Unknown) fmt = id.Format;
                            if (id.Architecture != Architecture.Unknown) arch = id.Architecture;
                        }
                        catch { }

                        ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                        {
                            Name = fileName,
                            RelativePath = $"{apkEntry.FullName}!{entry.FullName}",
                            Architecture = arch,
                            Format = fmt == BinaryFormat.Unknown ? BinaryFormat.Elf : fmt,
                            Size = entry.Length,
                            ArchiveEntryName = entry.FullName,
                            NestedArchiveEntryName = apkEntry.FullName
                        });
                    }
                }
            }
        }

        // Fallback search for libunity.so if no standard libil2cpp candidate exists
        if (ctx.DiscoveredBinaries.Count == 0)
        {
            foreach (var entry in zip.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (IsFallbackBinaryCandidate(fileName))
                {
                    var arch = DetectArchitectureFromPath(entry.FullName);
                    ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                    {
                        Name = fileName,
                        RelativePath = entry.FullName,
                        Architecture = arch,
                        Format = BinaryFormat.Elf,
                        Size = entry.Length,
                        ArchiveEntryName = entry.FullName
                    });
                }
            }
        }

        if (ctx.DiscoveredBinaries.Count == 0)
        {
            throw new InvalidOperationException("No IL2CPP binary (libil2cpp.so, GameAssembly.dll, UnityFramework, or libunity.so) found in archive.");
        }

        // Select binary based on preferred architecture (Arm64 preferred by default)
        var selectedBinary = SelectPreferredBinary(ctx.DiscoveredBinaries, preferredArch);
        logger?.Invoke($"Selected binary: {selectedBinary.RelativePath} ({selectedBinary.Architecture}, {selectedBinary.Format})");

        var outBinaryPath = Path.Combine(tempDir, selectedBinary.Name);
        if (selectedBinary.NestedArchiveEntryName != null)
        {
            var apkEntry = zip.GetEntry(selectedBinary.NestedArchiveEntryName)!;
            using var apkStream = apkEntry.Open();
            using var nestedZip = new ZipArchive(apkStream, ZipArchiveMode.Read);
            var entry = nestedZip.GetEntry(selectedBinary.ArchiveEntryName!)!;
            entry.ExtractToFile(outBinaryPath, true);
        }
        else
        {
            var entry = zip.GetEntry(selectedBinary.ArchiveEntryName!)!;
            entry.ExtractToFile(outBinaryPath, true);
        }

        // If binary is a Mach-O fat binary, extract slice
        using (var bFs = File.OpenRead(outBinaryPath))
        {
            var id = BinaryInspector.Inspect(bFs);
            if (id.IsFatBinary)
            {
                var slice = BinaryInspector.ExtractSlice(bFs, preferredArch ?? Architecture.Arm64);
                if (slice != null && slice.Length > 0)
                {
                    bFs.Close();
                    File.WriteAllBytes(outBinaryPath, slice);
                    logger?.Invoke($"Extracted {id.Architecture} slice from Mach-O universal fat binary.");
                }
            }
            if (id.Format != BinaryFormat.Unknown) selectedBinary.Format = id.Format;
            if (id.Architecture != Architecture.Unknown) selectedBinary.Architecture = id.Architecture;
        }

        ctx.BinaryPath = outBinaryPath;
        ctx.Architecture = selectedBinary.Architecture;
        ctx.Format = selectedBinary.Format;

        // 3. Extract metadata with candidate scoring (Part 9)
        var bestMetaEntry = SelectBestMetadataEntry(zip, nestedApks, logger);
        if (bestMetaEntry != null)
        {
            var outMeta = Path.Combine(tempDir, Path.GetFileName(bestMetaEntry.FullName));
            bestMetaEntry.ExtractToFile(outMeta, true);
            ctx.MetadataPath = outMeta;
            logger?.Invoke($"Selected best metadata entry: {bestMetaEntry.FullName}");
        }
        else
        {
            logger?.Invoke("Warning: global-metadata.dat not found in standard archive paths.");
        }
    }

    private static ZipArchiveEntry? SelectBestMetadataEntry(
        ZipArchive zip,
        List<ZipArchiveEntry> nestedApks,
        Action<string>? logger)
    {
        var candidates = new List<(ZipArchiveEntry Entry, int Score)>();

        void EvaluateEntry(ZipArchiveEntry entry)
        {
            var fileName = Path.GetFileName(entry.FullName);
            if (!IsMetadataCandidate(fileName)) return;

            var score = 0;
            var lower = entry.FullName.ToLowerInvariant().Replace('\\', '/');

            if (lower.Contains("assets/bin/data/managed/metadata/global-metadata.dat") ||
                lower.Contains("data/managed/metadata/global-metadata.dat"))
            {
                score += 100;
            }
            else if (lower.Contains("managed/metadata"))
            {
                score += 70;
            }

            if (fileName.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }
            else
            {
                score += 10;
            }

            // Sample header for structural validity
            try
            {
                using var stream = entry.Open();
                var header = new byte[Math.Min(288, (int)entry.Length)];
                var read = stream.Read(header, 0, header.Length);
                if (read >= 8)
                {
                    var conf = MetadataRecoveryEngine.EvaluateHeader(header.AsSpan(0, read), entry.Length);
                    score += conf.Score;
                }
            }
            catch
            {
                // Ignore evaluation errors
            }

            candidates.Add((entry, score));
        }

        foreach (var entry in zip.Entries)
        {
            EvaluateEntry(entry);
        }

        if (candidates.Count == 0 && nestedApks.Count > 0)
        {
            foreach (var apk in nestedApks)
            {
                using var s = apk.Open();
                using var nZip = new ZipArchive(s, ZipArchiveMode.Read);
                foreach (var entry in nZip.Entries)
                {
                    EvaluateEntry(entry);
                }
            }
        }

        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(c => c.Score).First().Entry;
    }

    private static void DetectFromDirectory(
        string dir,
        ExtractionContext ctx,
        Architecture? preferredArch,
        Action<string>? logger)
    {
        var apkFiles = Directory.GetFiles(dir, "*.apk", SearchOption.TopDirectoryOnly);
        if (apkFiles.Length > 0)
        {
            logger?.Invoke($"Directory contains {apkFiles.Length} APK package(s). Ingesting as split bundle...");
            ExtractFromSplitApks(apkFiles, ctx, preferredArch, logger);
            return;
        }

        var allFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        var metaCandidates = new List<(string Path, int Score)>();

        foreach (var file in allFiles)
        {
            var fileName = Path.GetFileName(file);
            if (IsBinaryCandidate(fileName))
            {
                var id = BinaryInspector.Inspect(file);
                var arch = id.Architecture != Architecture.Unknown ? id.Architecture : DetectArchitectureFromPath(file);
                var fmt = id.Format != BinaryFormat.Unknown ? id.Format : DetectFormat(fileName);

                ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                {
                    Name = fileName,
                    RelativePath = Path.GetRelativePath(dir, file),
                    Architecture = arch != Architecture.Unknown ? arch : (fmt == BinaryFormat.PE ? Architecture.X64 : Architecture.Arm64),
                    Format = fmt,
                    Size = new FileInfo(file).Length
                });
            }

            if (IsMetadataCandidate(fileName))
            {
                var score = 0;
                var lower = file.ToLowerInvariant().Replace('\\', '/');
                if (lower.Contains("managed/metadata")) score += 70;
                if (fileName.Equals("global-metadata.dat", StringComparison.OrdinalIgnoreCase)) score += 30;

                try
                {
                    var fileLen = new FileInfo(file).Length;
                    using var fs = File.OpenRead(file);
                    var header = new byte[Math.Min(288, (int)fileLen)];
                    var read = fs.Read(header, 0, header.Length);
                    if (read >= 8)
                    {
                        var conf = MetadataRecoveryEngine.EvaluateHeader(header.AsSpan(0, read), fileLen, stream: fs);
                        score += conf.Score;
                    }
                }
                catch { }

                metaCandidates.Add((file, score));
            }
        }

        if (metaCandidates.Count > 0)
        {
            ctx.MetadataPath = metaCandidates.OrderByDescending(m => m.Score).First().Path;
        }

        var isMono = allFiles.Any(f => Path.GetFileName(f).Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase));
        if (isMono && string.IsNullOrEmpty(ctx.MetadataPath))
        {
            throw new InvalidOperationException(
                "This game is built with Unity's Mono scripting backend, not IL2CPP!\n" +
                "Managed assemblies (e.g. Assembly-CSharp.dll) already exist in the 'Managed' folder and can be opened directly in dnSpy or ILSpy without dumping.");
        }

        if (ctx.DiscoveredBinaries.Count == 0)
        {
            foreach (var file in allFiles)
            {
                var fileName = Path.GetFileName(file);
                if (IsFallbackBinaryCandidate(fileName))
                {
                    var id = BinaryInspector.Inspect(file);
                    var arch = id.Architecture != Architecture.Unknown ? id.Architecture : DetectArchitectureFromPath(file);
                    ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                    {
                        Name = fileName,
                        RelativePath = Path.GetRelativePath(dir, file),
                        Architecture = arch != Architecture.Unknown ? arch : Architecture.Arm64,
                        Format = BinaryFormat.Elf,
                        Size = new FileInfo(file).Length
                    });
                }
            }
        }

        if (ctx.DiscoveredBinaries.Count == 0)
        {
            throw new FileNotFoundException($"No IL2CPP binary (GameAssembly.dll, libil2cpp.so, UnityFramework, or libunity.so) found in directory: {dir}");
        }

        var selected = SelectPreferredBinary(ctx.DiscoveredBinaries, preferredArch);
        var fullPath = Path.Combine(dir, selected.RelativePath);
        ctx.BinaryPath = fullPath;
        ctx.Architecture = selected.Architecture;
        ctx.Format = selected.Format;
        logger?.Invoke($"Found binary: {fullPath} ({ctx.Architecture}, {ctx.Format})");

        if (!string.IsNullOrEmpty(ctx.MetadataPath))
        {
            logger?.Invoke($"Found metadata: {ctx.MetadataPath}");
        }
    }

    private static void ExtractFromSplitApks(
        string[] apkFiles,
        ExtractionContext ctx,
        Architecture? preferredArch,
        Action<string>? logger)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp_dumper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ctx.TempDirectory = tempDir;

        string? foundMetaApk = null;
        string? foundMetaEntry = null;

        foreach (var apkPath in apkFiles)
        {
            using var zip = ZipFile.OpenRead(apkPath);
            ValidateArchiveSafety(apkPath, zip);

            foreach (var entry in zip.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (IsBinaryCandidate(fileName))
                {
                    var arch = DetectArchitectureFromPath(entry.FullName);
                    if (arch == Architecture.Unknown)
                        arch = DetectArchitectureFromPath(apkPath);

                    var fmt = DetectFormat(fileName);
                    try
                    {
                        using var s = entry.Open();
                        var id = BinaryInspector.Inspect(s);
                        if (id.Format != BinaryFormat.Unknown) fmt = id.Format;
                        if (id.Architecture != Architecture.Unknown) arch = id.Architecture;
                    }
                    catch { }

                    ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                    {
                        Name = fileName,
                        RelativePath = $"{Path.GetFileName(apkPath)}!{entry.FullName}",
                        Architecture = arch,
                        Format = fmt == BinaryFormat.Unknown ? BinaryFormat.Elf : fmt,
                        Size = entry.Length,
                        ArchiveEntryName = entry.FullName,
                        NestedArchiveEntryName = apkPath
                    });
                }

                if (foundMetaEntry == null && IsMetadataCandidate(fileName))
                {
                    foundMetaApk = apkPath;
                    foundMetaEntry = entry.FullName;
                }
            }
        }

        if (ctx.DiscoveredBinaries.Count == 0)
        {
            foreach (var apkPath in apkFiles)
            {
                using var zip = ZipFile.OpenRead(apkPath);
                foreach (var entry in zip.Entries)
                {
                    var fileName = Path.GetFileName(entry.FullName);
                    if (IsFallbackBinaryCandidate(fileName))
                    {
                        var arch = DetectArchitectureFromPath(entry.FullName);
                        if (arch == Architecture.Unknown)
                            arch = DetectArchitectureFromPath(apkPath);

                        ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                        {
                            Name = fileName,
                            RelativePath = $"{Path.GetFileName(apkPath)}!{entry.FullName}",
                            Architecture = arch,
                            Format = BinaryFormat.Elf,
                            Size = entry.Length,
                            ArchiveEntryName = entry.FullName,
                            NestedArchiveEntryName = apkPath
                        });
                    }
                }
            }
        }

        if (ctx.DiscoveredBinaries.Count == 0)
        {
            throw new InvalidOperationException("No IL2CPP binary found in split APK bundle.");
        }

        var selected = SelectPreferredBinary(ctx.DiscoveredBinaries, preferredArch);
        logger?.Invoke($"Selected binary from split bundle: {selected.RelativePath} ({selected.Architecture})");

        var outBinaryPath = Path.Combine(tempDir, selected.Name);
        using (var sourceZip = ZipFile.OpenRead(selected.NestedArchiveEntryName!))
        {
            var entry = sourceZip.GetEntry(selected.ArchiveEntryName!)!;
            entry.ExtractToFile(outBinaryPath, true);
        }

        ctx.BinaryPath = outBinaryPath;
        ctx.Architecture = selected.Architecture;
        ctx.Format = selected.Format;

        if (foundMetaApk != null && foundMetaEntry != null)
        {
            var outMetaPath = Path.Combine(tempDir, Path.GetFileName(foundMetaEntry));
            using var metaZip = ZipFile.OpenRead(foundMetaApk);
            var mEntry = metaZip.GetEntry(foundMetaEntry)!;
            mEntry.ExtractToFile(outMetaPath, true);
            ctx.MetadataPath = outMetaPath;
            logger?.Invoke($"Extracted metadata from {Path.GetFileName(foundMetaApk)}: {mEntry.FullName}");
        }
        else
        {
            logger?.Invoke("Warning: global-metadata.dat not found in split APK bundle.");
        }
    }

    private static void DetectFromFile(
        string filePath,
        string? metadataOverride,
        ExtractionContext ctx,
        Action<string>? logger)
    {
        var fileName = Path.GetFileName(filePath);
        var dir = Path.GetDirectoryName(filePath) ?? ".";

        if (IsMetadataCandidate(fileName))
        {
            ctx.MetadataPath = filePath;
            var nearbyBin = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => IsBinaryCandidate(Path.GetFileName(f)));

            if (nearbyBin != null)
            {
                var id = BinaryInspector.Inspect(nearbyBin);
                ctx.BinaryPath = nearbyBin;
                ctx.Architecture = id.Architecture != Architecture.Unknown ? id.Architecture : DetectArchitectureFromPath(nearbyBin);
                ctx.Format = id.Format != BinaryFormat.Unknown ? id.Format : DetectFormat(nearbyBin);
            }
        }
        else
        {
            var targetBinary = filePath;
            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsBinaryCandidate(fileName))
            {
                var gameAssembly = Path.Combine(dir, "GameAssembly.dll");
                if (File.Exists(gameAssembly))
                {
                    targetBinary = gameAssembly;
                    logger?.Invoke($"Target is game executable. Automatically resolved GameAssembly.dll: {gameAssembly}");
                }
            }

            var id = BinaryInspector.Inspect(targetBinary);
            ctx.BinaryPath = targetBinary;
            ctx.Architecture = id.Architecture != Architecture.Unknown ? id.Architecture : DetectArchitectureFromPath(targetBinary);
            ctx.Format = id.Format != BinaryFormat.Unknown ? id.Format : DetectFormat(targetBinary);

            if (!string.IsNullOrEmpty(metadataOverride) && File.Exists(metadataOverride))
            {
                ctx.MetadataPath = metadataOverride;
            }
            else
            {
                var nearbyMeta = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => IsMetadataCandidate(Path.GetFileName(f)));
                if (nearbyMeta != null)
                {
                    ctx.MetadataPath = nearbyMeta;
                    logger?.Invoke($"Auto-detected metadata file: {nearbyMeta}");
                }
            }
        }
    }

    private static DiscoveredBinary SelectPreferredBinary(List<DiscoveredBinary> binaries, Architecture? preferred)
    {
        if (preferred.HasValue && preferred.Value != Architecture.Unknown)
        {
            var match = binaries.FirstOrDefault(b => b.Architecture == preferred.Value);
            if (match != null) return match;
        }

        // Priority: Arm64 > X64 > Armv7 > X86 > First
        return binaries.FirstOrDefault(b => b.Architecture == Architecture.Arm64)
            ?? binaries.FirstOrDefault(b => b.Architecture == Architecture.X64)
            ?? binaries.FirstOrDefault(b => b.Architecture == Architecture.Armv7)
            ?? binaries.FirstOrDefault(b => b.Architecture == Architecture.X86)
            ?? binaries.First();
    }
}
