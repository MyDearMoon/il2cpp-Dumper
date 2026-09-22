using System.Buffers.Binary;
using System.Text;

namespace Il2CppDumper.Core.Metadata;

public static class MetadataRecoveryEngine
{
    private static readonly int[] RecognizedVersions = { 16, 19, 20, 21, 22, 23, 24, 27, 29, 31, 1024 };
    private static readonly byte[] CanonicalMagicBytes = { 0xAF, 0x1B, 0xB1, 0xFA }; // 0xFAB11BAF little-endian

    public static MetadataConfidence EvaluateHeader(
        ReadOnlySpan<byte> header,
        long fileLength,
        MetadataRecoveryOptions? options = null,
        Stream? stream = null,
        long headerOffsetInFile = 0)
    {
        var confidence = new MetadataConfidence();

        if (header.Length < 4 || fileLength < 4)
        {
            confidence.Score = 0;
            confidence.Diagnostic = "global-metadata.dat is too small or truncated (< 4 bytes).";
            return confidence;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var version = header.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4)) : 0;

        // Truncated header / stub file check
        if (header.Length < 252 || fileLength < 252)
        {
            if (magic == MetadataFingerprintRegistry.CanonicalMagic ||
                (options?.CustomMagic.HasValue == true && magic == options.CustomMagic.Value))
            {
                if (RecognizedVersions.Contains(version) || version is >= 1 and <= 100)
                {
                    confidence.Score = 75;
                    confidence.IsRecognizedVersion = RecognizedVersions.Contains(version);
                    confidence.IsPlausibleVersion = true;
                    return confidence;
                }
            }

            confidence.Score = 0;
            confidence.Diagnostic = "global-metadata.dat is too small or truncated (< 252 bytes).";
            return confidence;
        }

        // 1. Version Analysis
        if (version <= 0 || (version > 100 && version != 1024))
        {
            confidence.Score = 0;
            confidence.Diagnostic = $"Impossible IL2CPP metadata version: {version}.";
            return confidence;
        }

        if (RecognizedVersions.Contains(version))
        {
            confidence.IsRecognizedVersion = true;
            confidence.Score += 20;
        }
        else if (version is >= 1 and <= 100)
        {
            confidence.IsPlausibleVersion = true;
            confidence.Score += 10;
        }

        // 2. Magic Analysis
        if (magic == MetadataFingerprintRegistry.CanonicalMagic)
        {
            confidence.Score += 25;
        }
        else if (options?.CustomMagic.HasValue == true && magic == options.CustomMagic.Value)
        {
            confidence.Score += 25;
        }
        else if (MetadataFingerprintRegistry.TryGetDiagnostic(magic, out var diag))
        {
            confidence.Score += 15;
            confidence.Diagnostic = diag;
        }
        else if (options?.IgnoreMagic == true)
        {
            confidence.Score += 10;
        }

        // 3. Section Offsets and Bounds Checking
        var isMoonton = version == 1024;
        var offsetShift = isMoonton ? 4 : 0; // Moonton shifts offsets by 4 bytes due to partitionId at offset 8

        var sectionPairs = new List<(int Offset, int Size)>();

        // Check key sections
        // Pairs in standard header:
        // (8, 12): stringLiteral, (16, 20): stringLiteralData, (24, 28): string,
        // (48, 52): methods, (88, 92): parameters, (96, 100): fields, (160, 164): typeDefs
        int[] checkIndices = isMoonton
            ? new[] { 12, 20, 28, 52, 92, 100, 164 }
            : new[] { 8, 16, 24, 48, 88, 96, 160 };

        var effectiveFileLength = (ulong)(fileLength - headerOffsetInFile);
        var offsetsValid = true;

        foreach (var idx in checkIndices)
        {
            if (idx + 8 > header.Length) continue;

            var secOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(idx, 4));
            var secSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(idx + 4, 4));

            if (secOffset < 0 || secSize < 0)
            {
                offsetsValid = false;
                break;
            }

            if (secOffset > 0)
            {
                // Must not point within the header itself
                if (secOffset < 252)
                {
                    offsetsValid = false;
                    break;
                }

                // 64-bit safe bounds check against integer overflow
                var requiredEnd = (ulong)(uint)secOffset + (ulong)(uint)secSize;
                if (requiredEnd > effectiveFileLength)
                {
                    offsetsValid = false;
                    break;
                }

                sectionPairs.Add((secOffset, secSize));
            }
        }

        // Validate core sections: stringOffset and typeDefsOffset must exist
        var stringOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(isMoonton ? 28 : 24, 4));
        var stringSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(isMoonton ? 32 : 28, 4));
        var typeDefsOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(isMoonton ? 164 : 160, 4));
        var typeDefsSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(isMoonton ? 168 : 164, 4));

        if (!offsetsValid || stringOffset <= 0 || typeDefsOffset <= 0)
        {
            confidence.OffsetsValid = false;
            confidence.Score = 0;
            confidence.Diagnostic = "Section offsets or sizes are negative, point within header, or exceed file bounds.";
            return confidence;
        }

        confidence.OffsetsValid = true;
        confidence.Score += 30;

        // 4. Monotonicity check
        var nonZeroOffsets = sectionPairs.Select(p => p.Offset).ToList();
        var monotonic = true;
        for (var i = 1; i < nonZeroOffsets.Count; i++)
        {
            if (nonZeroOffsets[i] < nonZeroOffsets[i - 1])
            {
                monotonic = false;
                break;
            }
        }

        if (monotonic && nonZeroOffsets.Count >= 3)
        {
            confidence.MonotonicOffsets = true;
            confidence.Score += 15;
        }

        // 5. String Table Validation
        var absStringOffset = headerOffsetInFile + stringOffset;
        if (absStringOffset > 0 && (ulong)absStringOffset < (ulong)fileLength)
        {
            var stringSample = new byte[Math.Min(64, (int)(fileLength - absStringOffset))];
            var bytesRead = 0;

            if (stream != null && stream.CanSeek)
            {
                var originalPos = stream.Position;
                try
                {
                    stream.Position = absStringOffset;
                    bytesRead = stream.Read(stringSample, 0, stringSample.Length);
                }
                finally
                {
                    stream.Position = originalPos;
                }
            }
            else if (headerOffsetInFile == 0 && stringOffset + stringSample.Length <= header.Length)
            {
                header.Slice(stringOffset, stringSample.Length).CopyTo(stringSample);
                bytesRead = stringSample.Length;
            }

            if (bytesRead > 0)
            {
                var printableCount = 0;
                for (var i = 0; i < bytesRead; i++)
                {
                    var b = stringSample[i];
                    if (b == 0 || (b >= 0x20 && b <= 0x7E))
                        printableCount++;
                }

                if ((double)printableCount / bytesRead >= 0.8)
                {
                    confidence.StringTableValid = true;
                    confidence.Score += 10;
                }
            }
        }

        // Clamp score to 0..100
        confidence.Score = Math.Clamp(confidence.Score, 0, 100);
        return confidence;
    }

    public static MetadataRecoveryResult Recover(
        string metadataPath,
        MetadataRecoveryOptions? options = null,
        string? tempDir = null,
        Action<string>? logger = null)
    {
        options ??= new MetadataRecoveryOptions();

        if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
        {
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Diagnostic = $"File does not exist: {metadataPath}"
            };
        }

        using var fs = File.OpenRead(metadataPath);
        var fileLen = fs.Length;

        if (fileLen < 4)
        {
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Diagnostic = "global-metadata.dat is too small or truncated (< 4 bytes)."
            };
        }

        var scanDepth = options.ScanDepth <= 0 ? (int)Math.Min(int.MaxValue, fileLen) : (int)Math.Min(options.ScanDepth, fileLen);
        var searchLen = Math.Max(256, Math.Min(scanDepth, (int)Math.Min(1024 * 1024, fileLen)));
        var buffer = new byte[searchLen];
        var read = fs.Read(buffer, 0, searchLen);

        var headerSlice = buffer.AsSpan(0, Math.Min(read, 288));
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerSlice[..4]);
        var version = headerSlice.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(headerSlice.Slice(4, 4)) : 0;

        // ----------------------------------------------------
        // Path 1: Fast Path (Canonical Magic at Offset 0)
        // ----------------------------------------------------
        if (magic == MetadataFingerprintRegistry.CanonicalMagic)
        {
            var conf = EvaluateHeader(headerSlice, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = metadataPath,
                    Method = MetadataRecoveryMethod.Standard,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    Version = version,
                    WasNormalized = false
                };
            }
        }

        // ----------------------------------------------------
        // Path 2: Custom Magic Override (--magic <val>)
        // ----------------------------------------------------
        if (options.CustomMagic.HasValue && magic == options.CustomMagic.Value)
        {
            var conf = EvaluateHeader(headerSlice, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                logger?.Invoke($"[Recovery] Matched custom magic 0x{magic:X8} at offset 0 (version {version}, confidence {conf.Score}%). Restoring canonical magic for downstream compatibility...");
                var normPath = WriteNormalizedFile(fs, 0, fileLen, restoreMagic: true, xorKey: null, tempDir);
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = normPath,
                    Method = MetadataRecoveryMethod.TamperedMagic,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    Version = version,
                    WasNormalized = true
                };
            }
        }

        // ----------------------------------------------------
        // Check for Known Non-Recoverable Fingerprints (BigEndian or Hoyo)
        // ----------------------------------------------------
        if (magic == MetadataFingerprintRegistry.BigEndianMagic)
        {
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Method = MetadataRecoveryMethod.ByteSwapped,
                ConfidenceScore = 0,
                Diagnostic = "Detected byte-swapped (big-endian) IL2CPP metadata magic (FA B1 1B AF)."
            };
        }

        if (magic == MetadataFingerprintRegistry.HoyoMagic)
        {
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Method = MetadataRecoveryMethod.None,
                ConfidenceScore = 0,
                Diagnostic = "Detected HoYoverse encrypted metadata (starts with 'MHY\\0')!\n" +
                             "HoYoverse games (Zenless Zone Zero, Genshin Impact, Honkai: Star Rail) encrypt global-metadata.dat on disk.\n" +
                             "Static dumpers cannot read disk files directly. Dump the decrypted global-metadata.dat from RAM at runtime."
            };
        }

        // ----------------------------------------------------
        // Path 3: Tampered Magic or --ignore-magic at Offset 0
        // ----------------------------------------------------
        if (options.IgnoreMagic || magic != MetadataFingerprintRegistry.CanonicalMagic)
        {
            var conf = EvaluateHeader(headerSlice, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                MetadataFingerprintRegistry.TryGetDiagnostic(magic, out var diag);
                var reason = diag ?? $"Tampered magic 0x{magic:X8}";
                logger?.Invoke($"[Recovery] Structurally valid IL2CPP metadata recognized ({reason}, version {version}, confidence {conf.Score}%). Restoring canonical magic for downstream compatibility...");
                var normPath = WriteNormalizedFile(fs, 0, fileLen, restoreMagic: true, xorKey: null, tempDir);
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = normPath,
                    Method = MetadataRecoveryMethod.TamperedMagic,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    Version = version,
                    Diagnostic = diag,
                    WasNormalized = true
                };
            }
        }

        // ----------------------------------------------------
        // Path 4: 1-Byte and 4-Byte XOR Auto-Detection at Offset 0
        // ----------------------------------------------------
        // 1-Byte XOR candidate derivation
        var k1 = (byte)(buffer[0] ^ CanonicalMagicBytes[0]);
        if (k1 != 0 &&
            (buffer[1] ^ CanonicalMagicBytes[1]) == k1 &&
            (buffer[2] ^ CanonicalMagicBytes[2]) == k1 &&
            (buffer[3] ^ CanonicalMagicBytes[3]) == k1)
        {
            var testHeader = new byte[headerSlice.Length];
            for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ k1);

            var conf = EvaluateHeader(testHeader, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                logger?.Invoke($"[Recovery] Detected 1-byte XOR obfuscation (Key: 0x{k1:X2}, version {decVersion}, confidence {conf.Score}%). Decrypting...");
                var normPath = WriteDecryptedFile(fs, 0, fileLen, new[] { k1 }, tempDir);
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = normPath,
                    Method = MetadataRecoveryMethod.Xor1Byte,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    XorKey = new[] { k1 },
                    Version = decVersion,
                    WasNormalized = true
                };
            }
        }

        // 4-Byte XOR candidate derivation
        var k4 = magic ^ MetadataFingerprintRegistry.CanonicalMagic;
        if (k4 != 0)
        {
            var k4Bytes = BitConverter.GetBytes(k4);
            var testHeader = new byte[headerSlice.Length];
            for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ k4Bytes[i % 4]);

            var conf = EvaluateHeader(testHeader, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                logger?.Invoke($"[Recovery] Detected 4-byte XOR obfuscation (Key: 0x{k4:X8}, version {decVersion}, confidence {conf.Score}%). Decrypting...");
                var normPath = WriteDecryptedFile(fs, 0, fileLen, k4Bytes, tempDir);
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = normPath,
                    Method = MetadataRecoveryMethod.Xor4Byte,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    XorKey = k4Bytes,
                    Version = decVersion,
                    WasNormalized = true
                };
            }
        }

        // Exhaustive 1-byte search fallback (if magic itself was modified before XOR)
        for (var candidateK = 1; candidateK <= 255; candidateK++)
        {
            if (candidateK == k1) continue; // Already tested
            var testHeader = new byte[headerSlice.Length];
            for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ candidateK);

            var conf = EvaluateHeader(testHeader, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                logger?.Invoke($"[Recovery] Recovered 1-byte XOR obfuscation via structural analysis (Key: 0x{candidateK:X2}, version {decVersion}, confidence {conf.Score}%). Decrypting and restoring canonical magic...");
                var normPath = WriteDecryptedFile(fs, 0, fileLen, new[] { (byte)candidateK }, tempDir, restoreMagic: true);
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = normPath,
                    Method = MetadataRecoveryMethod.Xor1Byte,
                    ConfidenceScore = conf.Score,
                    Offset = 0,
                    XorKey = new[] { (byte)candidateK },
                    Version = decVersion,
                    WasNormalized = true
                };
            }
        }

        // ----------------------------------------------------
        // Path 5: Deep Envelope Scan (Offset > 0)
        // ----------------------------------------------------
        for (var i = 1; i <= read - 4; i++)
        {
            if (buffer[i] == CanonicalMagicBytes[0] &&
                buffer[i + 1] == CanonicalMagicBytes[1] &&
                buffer[i + 2] == CanonicalMagicBytes[2] &&
                buffer[i + 3] == CanonicalMagicBytes[3])
            {
                var candidateHeader = buffer.AsSpan(i, Math.Min(read - i, 288));
                var conf = EvaluateHeader(candidateHeader, fileLen, options, fs, i);
                if (conf.IsAcceptable)
                {
                    var candVersion = candidateHeader.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(candidateHeader.Slice(4, 4)) : 0;
                    logger?.Invoke($"[Recovery] Found valid IL2CPP metadata at offset 0x{i:X} ({i} bytes prefix, version {candVersion}, confidence {conf.Score}%). Unwrapping envelope...");
                    var normPath = WriteNormalizedFile(fs, i, fileLen, restoreMagic: false, xorKey: null, tempDir);
                    return new MetadataRecoveryResult
                    {
                        Success = true,
                        ResultPath = normPath,
                        Method = MetadataRecoveryMethod.PrefixedPayload,
                        ConfidenceScore = conf.Score,
                        Offset = i,
                        Version = candVersion,
                        WasNormalized = true
                    };
                }
            }
        }

        if (fileLen < 252)
        {
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Diagnostic = "global-metadata.dat is too small or truncated (< 252 bytes)."
            };
        }

        // ----------------------------------------------------
        // Path 6: Known Fingerprint Diagnostics
        // ----------------------------------------------------
        if (MetadataFingerprintRegistry.TryGetDiagnostic(magic, out var knownDiag))
        {
            var method = magic == MetadataFingerprintRegistry.BigEndianMagic
                ? MetadataRecoveryMethod.ByteSwapped
                : MetadataRecoveryMethod.None;

            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Method = method,
                ConfidenceScore = 0,
                Diagnostic = knownDiag
            };
        }

        return new MetadataRecoveryResult
        {
            Success = false,
            ResultPath = metadataPath,
            Method = MetadataRecoveryMethod.None,
            ConfidenceScore = 0,
            Diagnostic = $"global-metadata.dat is encrypted or obfuscated (Magic: 0x{magic:X8} instead of 0xFAB11BAF).\n" +
                         "Use a runtime memory dumper (or the bundled Frida script) to dump the decrypted metadata from RAM at runtime."
        };
    }

    private static string WriteNormalizedFile(
        Stream srcStream,
        long offset,
        long totalLength,
        bool restoreMagic,
        byte[]? xorKey,
        string? tempDir)
    {
        var targetDir = !string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir)
            ? tempDir
            : Path.GetTempPath();

        var outPath = Path.Combine(targetDir, $"normalized_metadata_{Guid.NewGuid():N}.dat");

        srcStream.Position = offset;
        using var outFs = File.Create(outPath);

        if (restoreMagic)
        {
            outFs.Write(CanonicalMagicBytes, 0, CanonicalMagicBytes.Length);
            srcStream.Position = offset + 4;
        }

        srcStream.CopyTo(outFs);
        return outPath;
    }

    private static string WriteDecryptedFile(
        Stream srcStream,
        long offset,
        long totalLength,
        byte[] key,
        string? tempDir,
        bool restoreMagic = false)
    {
        var targetDir = !string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir)
            ? tempDir
            : Path.GetTempPath();

        var outPath = Path.Combine(targetDir, $"normalized_metadata_{Guid.NewGuid():N}.dat");

        srcStream.Position = offset;
        using var outFs = File.Create(outPath);

        var buffer = new byte[64 * 1024];
        int read;
        var totalWritten = 0L;

        while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (key.Length == 1)
            {
                var k = key[0];
                for (var i = 0; i < read; i++) buffer[i] ^= k;
            }
            else if (key.Length == 4)
            {
                var k32 = BinaryPrimitives.ReadUInt32LittleEndian(key);
                var fullWords = read / 4;
                for (var i = 0; i < fullWords; i++)
                {
                    var val = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4, 4));
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 4, 4), val ^ k32);
                }

                for (var i = fullWords * 4; i < read; i++)
                {
                    buffer[i] ^= key[i % 4];
                }
            }

            if (totalWritten == 0 && restoreMagic)
            {
                Array.Copy(CanonicalMagicBytes, 0, buffer, 0, CanonicalMagicBytes.Length);
            }

            outFs.Write(buffer, 0, read);
            totalWritten += read;
        }

        return outPath;
    }
}
