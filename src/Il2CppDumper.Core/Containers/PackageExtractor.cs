using System.IO.Compression;

namespace Il2CppDumper.Core.Containers;

public static class PackageExtractor
{
    private static readonly string[] BinaryNames = { "libil2cpp.so", "gameassembly.dll" };
    private const string MetadataFileName = "global-metadata.dat";

    public static bool IsBinaryCandidate(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower == "gameassembly.dll") return true;
        if (lower.EndsWith(".so") && lower.Contains("il2cpp")) return true;
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
        if (ext == ".dll" || ext == ".exe") return BinaryFormat.PE;
        if (ext == ".so") return BinaryFormat.Elf;
        if (ext == ".wasm") return BinaryFormat.Wasm;
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
            throw new FileNotFoundException("Failed to locate IL2CPP binary (libil2cpp.so, GameAssembly.dll, or libunity.so) in input.");
        }

        if (string.IsNullOrEmpty(ctx.MetadataPath) || !File.Exists(ctx.MetadataPath))
        {
            throw new FileNotFoundException("Failed to locate global-metadata.dat in input.");
        }

        return ctx;
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

                        ctx.DiscoveredBinaries.Add(new DiscoveredBinary
                        {
                            Name = fileName,
                            RelativePath = $"{apkEntry.FullName}!{entry.FullName}",
                            Architecture = arch,
                            Format = BinaryFormat.Elf,
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
            throw new InvalidOperationException("No IL2CPP binary (libil2cpp.so, GameAssembly.dll, or libunity.so) found in archive.");
        }

        // Select binary based on preferred architecture (Arm64 preferred by default)
        var selectedBinary = SelectPreferredBinary(ctx.DiscoveredBinaries, preferredArch);
        logger?.Invoke($"Selected binary: {selectedBinary.RelativePath} ({selectedBinary.Architecture})");

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

        ctx.BinaryPath = outBinaryPath;
        ctx.Architecture = selectedBinary.Architecture;
        ctx.Format = selectedBinary.Format;

        // 3. Extract metadata
        var metaEntry = zip.Entries.FirstOrDefault(e => IsMetadataCandidate(Path.GetFileName(e.FullName)));

        if (metaEntry == null && nestedApks.Count > 0)
        {
            foreach (var apkEntry in nestedApks)
            {
                using var apkStream = apkEntry.Open();
                using var nestedZip = new ZipArchive(apkStream, ZipArchiveMode.Read);
                var entry = nestedZip.Entries.FirstOrDefault(e => IsMetadataCandidate(Path.GetFileName(e.FullName)));
                if (entry != null)
                {
                    var outMeta = Path.Combine(tempDir, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(outMeta, true);
                    ctx.MetadataPath = outMeta;
                    break;
                }
            }
        }
        else if (metaEntry != null)
        {
            var outMeta = Path.Combine(tempDir, Path.GetFileName(metaEntry.FullName));
            metaEntry.ExtractToFile(outMeta, true);
            ctx.MetadataPath = outMeta;
        }
        else
        {
            logger?.Invoke("Warning: global-metadata.dat not found in standard archive paths.");
        }
    }

    private static void DetectFromDirectory(
        string dir,
        ExtractionContext ctx,
        Architecture? preferredArch,
        Action<string>? logger)
    {
        // 1. Check if directory contains split APK files
        var apkFiles = Directory.GetFiles(dir, "*.apk", SearchOption.TopDirectoryOnly);
        if (apkFiles.Length > 0)
        {
            logger?.Invoke($"Directory contains {apkFiles.Length} APK package(s). Ingesting as split bundle...");
            ExtractFromSplitApks(apkFiles, ctx, preferredArch, logger);
            return;
        }

        // 2. Loose unzipped directory scan
        var allFiles = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

        foreach (var file in allFiles)
        {
            var fileName = Path.GetFileName(file);
            if (IsBinaryCandidate(fileName))
            {
                var arch = DetectArchitectureFromPath(file);
                var fmt = DetectFormat(fileName);
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
                ctx.MetadataPath = file;
            }
        }

        // Check for Unity Mono games (Assembly-CSharp.dll without global-metadata.dat)
        var isMono = allFiles.Any(f => Path.GetFileName(f).Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase));
        if (isMono && string.IsNullOrEmpty(ctx.MetadataPath))
        {
            throw new InvalidOperationException(
                "This game is built with Unity's Mono scripting backend, not IL2CPP!\n" +
                "Managed assemblies (e.g. Assembly-CSharp.dll) already exist in the 'Managed' folder and can be opened directly in dnSpy or ILSpy without dumping.");
        }

        // Fallback for libunity.so if no libil2cpp was found
        if (ctx.DiscoveredBinaries.Count == 0)
        {
            foreach (var file in allFiles)
            {
                var fileName = Path.GetFileName(file);
                if (IsFallbackBinaryCandidate(fileName))
                {
                    var arch = DetectArchitectureFromPath(file);
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
            throw new FileNotFoundException($"No IL2CPP binary (GameAssembly.dll, libil2cpp.so, or libunity.so) found in directory: {dir}");
        }

        var selected = SelectPreferredBinary(ctx.DiscoveredBinaries, preferredArch);
        var fullPath = Path.Combine(dir, selected.RelativePath);
        ctx.BinaryPath = fullPath;
        ctx.Architecture = selected.Architecture;
        ctx.Format = selected.Format;
        logger?.Invoke($"Found binary: {fullPath} ({ctx.Architecture})");

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
            foreach (var entry in zip.Entries)
            {
                var fileName = Path.GetFileName(entry.FullName);
                if (IsBinaryCandidate(fileName))
                {
                    var arch = DetectArchitectureFromPath(entry.FullName);
                    if (arch == Architecture.Unknown)
                        arch = DetectArchitectureFromPath(apkPath);

                    var fmt = DetectFormat(fileName);
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

        // Fallback for libunity.so if no libil2cpp candidate exists
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
            // Find binary nearby
            var nearbyBin = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => IsBinaryCandidate(Path.GetFileName(f)));

            if (nearbyBin != null)
            {
                ctx.BinaryPath = nearbyBin;
                ctx.Architecture = DetectArchitectureFromPath(nearbyBin);
                ctx.Format = DetectFormat(nearbyBin);
            }
        }
        else
        {
            // If user dropped the game executable (e.g. BlueArchive.exe), resolve GameAssembly.dll alongside it
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

            ctx.BinaryPath = targetBinary;
            ctx.Architecture = DetectArchitectureFromPath(targetBinary);
            ctx.Format = DetectFormat(targetBinary);

            if (!string.IsNullOrEmpty(metadataOverride) && File.Exists(metadataOverride))
            {
                ctx.MetadataPath = metadataOverride;
            }
            else
            {
                // Look for global-metadata.dat nearby
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
