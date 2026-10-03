using System.Buffers.Binary;
using System.Text;

namespace Il2CppDumper.Core.Metadata;

public sealed class XorStream : Stream
{
    private readonly Stream _baseStream;
    private readonly byte[] _key;
    private readonly long _originOffset;
    private readonly long _length;
    private long _position;

    public XorStream(Stream baseStream, byte[] key, long originOffset = 0, long? length = null)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        if (_key.Length == 0) throw new ArgumentException("XOR key cannot be empty", nameof(key));
        _originOffset = originOffset;
        _length = length ?? Math.Max(0, baseStream.Length - originOffset);
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (newPos < 0) throw new IOException("An attempt was made to move the file pointer before the beginning of the file.");
        _position = newPos;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length) return 0;
        int toRead = (int)Math.Min(count, _length - _position);
        _baseStream.Position = _originOffset + _position;
        int read = _baseStream.Read(buffer, offset, toRead);
        for (int i = 0; i < read; i++)
        {
            buffer[offset + i] ^= _key[(_position + i) % _key.Length];
        }
        _position += read;
        return read;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class MetadataCandidate
{
    public long Offset { get; set; }
    public MetadataRecoveryMethod Method { get; set; } = MetadataRecoveryMethod.Standard;
    public int Version { get; set; }
    public MetadataConfidence Confidence { get; set; } = new();
    public byte[]? XorKey { get; set; }
    public uint Magic { get; set; }
    public string? Diagnostic { get; set; }
    public bool RestoreMagic { get; set; }
}

public static class MetadataRecoveryEngine
{
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
                var status = MetadataVersionProfile.ClassifyVersion(version, out var diag);
                if (status is MetadataVersionStatus.Recognized or MetadataVersionStatus.StructurallyPlausible)
                {
                    confidence.Score = 75;
                    confidence.Diagnostic = "Valid magic and version detected, but file is truncated (< 252 bytes).";
                    return confidence;
                }
            }

            confidence.Score = 0;
            confidence.Diagnostic = "File is truncated and does not contain valid metadata.";
            return confidence;
        }

        // 1. Version Classification via MetadataVersionProfile
        var versionStatus = MetadataVersionProfile.ClassifyVersion(version, out var versionDiag);
        if (versionStatus == MetadataVersionStatus.Invalid)
        {
            confidence.Score = 0;
            confidence.Diagnostic = versionDiag;
            return confidence;
        }

        if (versionStatus == MetadataVersionStatus.Recognized)
        {
            confidence.IsRecognizedVersion = true;
            confidence.Score += 20;
        }
        else if (versionStatus == MetadataVersionStatus.StructurallyPlausible)
        {
            confidence.IsPlausibleVersion = true;
            confidence.Score += 10;
            confidence.Diagnostic = versionDiag;
        }
        else
        {
            confidence.Score = 0;
            confidence.Diagnostic = versionDiag;
            return confidence;
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
        MetadataVersionProfile.TryGetProfile(version, out var profile);
        var expectedHeaderSize = profile?.HeaderSize ?? (isMoonton ? 264 : 252);

        var sectionPairs = new List<(int Offset, int Size)>();
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
                // Invariant: Non-overlapping with mandatory header
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

                // Element alignment check if profile defines it
                if (profile?.SectionElementSizes.TryGetValue(idx, out var elemSize) == true && elemSize > 0)
                {
                    if (secSize % elemSize == 0)
                    {
                        confidence.Score += 2;
                    }
                    var elementCount = secSize / elemSize;
                    // Absurd count protection
                    if (elementCount > 2_000_000)
                    {
                        offsetsValid = false;
                        break;
                    }
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
            confidence.Diagnostic = "Section offsets or sizes are negative, point within header, exceed file bounds, or misaligned.";
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

        // 5. String Table Validation (against stream/memory view)
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

                if ((double)printableCount / bytesRead >= 0.75)
                {
                    confidence.StringTableValid = true;
                    confidence.Score += 10;
                }
            }
        }

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

        var headerBuffer = new byte[Math.Min(288, (int)fileLen)];
        fs.Position = 0;
        var headerRead = fs.Read(headerBuffer, 0, headerBuffer.Length);
        var headerSlice = headerBuffer.AsSpan(0, headerRead);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerSlice[..4]);
        var version = headerSlice.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(headerSlice.Slice(4, 4)) : 0;

        // ----------------------------------------------------
        // Path 1: Canonical Magic Fast Path (Part 20)
        // ----------------------------------------------------
        if (magic == MetadataFingerprintRegistry.CanonicalMagic)
        {
            var conf = EvaluateHeader(headerSlice, fileLen, options, fs, 0);
            if (conf.Score >= 90 && conf.IsRecognizedVersion)
            {
                logger?.Invoke($"[Recovery] Canonical IL2CPP metadata verified at offset 0 (Version: {version}, Confidence: {conf.Score}%). Fast-path engaged.");
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
        // Path 2: HoYoverse encrypted metadata check
        // ----------------------------------------------------
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

        if (magic == MetadataFingerprintRegistry.BigEndianMagic)
        {
            MetadataFingerprintRegistry.TryGetDiagnostic(magic, out var diag);
            return new MetadataRecoveryResult
            {
                Success = false,
                ResultPath = metadataPath,
                Method = MetadataRecoveryMethod.ByteSwapped,
                ConfidenceScore = 0,
                Diagnostic = diag
            };
        }

        var candidates = new List<MetadataCandidate>();

        // ----------------------------------------------------
        // Evaluate Offset 0 Candidates (Standard / Tampered / Custom)
        // ----------------------------------------------------
        {
            var conf = EvaluateHeader(headerSlice, fileLen, options, fs, 0);
            if (conf.IsAcceptable)
            {
                MetadataFingerprintRegistry.TryGetDiagnostic(magic, out var diag);
                var isTampered = magic != MetadataFingerprintRegistry.CanonicalMagic;
                candidates.Add(new MetadataCandidate
                {
                    Offset = 0,
                    Method = isTampered ? MetadataRecoveryMethod.TamperedMagic : MetadataRecoveryMethod.Standard,
                    Version = version,
                    Confidence = conf,
                    Magic = magic,
                    Diagnostic = diag,
                    RestoreMagic = isTampered
                });
            }
        }

        // ----------------------------------------------------
        // Evaluate Offset 0 XOR Candidates (1-byte & 4-byte)
        // ----------------------------------------------------
        // 1-Byte XOR candidate derivation
        var k1 = (byte)(headerBuffer[0] ^ CanonicalMagicBytes[0]);
        if (k1 != 0 &&
            (headerBuffer[1] ^ CanonicalMagicBytes[1]) == k1 &&
            (headerBuffer[2] ^ CanonicalMagicBytes[2]) == k1 &&
            (headerBuffer[3] ^ CanonicalMagicBytes[3]) == k1)
        {
            var testHeader = new byte[headerSlice.Length];
            for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ k1);

            using var xorStream = new XorStream(fs, new[] { k1 }, 0);
            var conf = EvaluateHeader(testHeader, fileLen, options, xorStream, 0);
            if (conf.IsAcceptable)
            {
                var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                candidates.Add(new MetadataCandidate
                {
                    Offset = 0,
                    Method = MetadataRecoveryMethod.Xor1Byte,
                    Version = decVersion,
                    Confidence = conf,
                    XorKey = new[] { k1 },
                    Magic = MetadataFingerprintRegistry.CanonicalMagic,
                    RestoreMagic = false
                });
            }
        }

        // 4-Byte XOR candidate derivation
        var k4 = magic ^ MetadataFingerprintRegistry.CanonicalMagic;
        if (k4 != 0)
        {
            var k4Bytes = BitConverter.GetBytes(k4);
            var testHeader = new byte[headerSlice.Length];
            for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ k4Bytes[i % 4]);

            using var xorStream4 = new XorStream(fs, k4Bytes, 0);
            var conf = EvaluateHeader(testHeader, fileLen, options, xorStream4, 0);
            if (conf.IsAcceptable)
            {
                var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                candidates.Add(new MetadataCandidate
                {
                    Offset = 0,
                    Method = MetadataRecoveryMethod.Xor4Byte,
                    Version = decVersion,
                    Confidence = conf,
                    XorKey = k4Bytes,
                    Magic = MetadataFingerprintRegistry.CanonicalMagic,
                    RestoreMagic = false
                });
            }
        }

        // Exhaustive 1-byte XOR sweep if no high confidence candidate yet
        if (!candidates.Any(c => c.Confidence.Score >= 80))
        {
            for (var candidateK = 1; candidateK <= 255; candidateK++)
            {
                if (candidateK == k1) continue;
                var testHeader = new byte[headerSlice.Length];
                for (var i = 0; i < testHeader.Length; i++) testHeader[i] = (byte)(headerSlice[i] ^ candidateK);

                using var sweepXorStream = new XorStream(fs, new[] { (byte)candidateK }, 0);
                var conf = EvaluateHeader(testHeader, fileLen, options, sweepXorStream, 0);
                if (conf.IsAcceptable)
                {
                    var decVersion = BinaryPrimitives.ReadInt32LittleEndian(testHeader.AsSpan(4, 4));
                    candidates.Add(new MetadataCandidate
                    {
                        Offset = 0,
                        Method = MetadataRecoveryMethod.Xor1Byte,
                        Version = decVersion,
                        Confidence = conf,
                        XorKey = new[] { (byte)candidateK },
                        Magic = MetadataFingerprintRegistry.CanonicalMagic,
                        RestoreMagic = true
                    });
                }
            }
        }

        // ----------------------------------------------------
        // Chunked Deep Scan for Prefixed Envelopes / Candidates (Part 6)
        // ----------------------------------------------------
        // scanDepth: 0 or negative = scan complete file; positive = scan up to scanDepth
        long maxScanDepth = options.ScanDepth <= 0 ? fileLen : Math.Min((long)options.ScanDepth, fileLen);
        if (maxScanDepth > 4)
        {
            const int ChunkSize = 65536; // 64 KB
            const int Overlap = 288;
            var chunk = new byte[ChunkSize];
            long currentOffset = 0;

            while (currentOffset < maxScanDepth)
            {
                fs.Position = currentOffset;
                var bytesToRead = (int)Math.Min(ChunkSize, maxScanDepth - currentOffset);
                var bytesRead = fs.Read(chunk, 0, bytesToRead);
                if (bytesRead < 4) break;

                for (int i = 0; i <= bytesRead - 4; i++)
                {
                    var globalCandidateOffset = currentOffset + i;
                    if (globalCandidateOffset == 0) continue; // Already tested offset 0

                    if (chunk[i] == CanonicalMagicBytes[0] &&
                        chunk[i + 1] == CanonicalMagicBytes[1] &&
                        chunk[i + 2] == CanonicalMagicBytes[2] &&
                        chunk[i + 3] == CanonicalMagicBytes[3])
                    {
                        // Found canonical magic signature! Read candidate header from stream
                        var candHeader = new byte[Math.Min(288, (int)(fileLen - globalCandidateOffset))];
                        fs.Position = globalCandidateOffset;
                        var candRead = fs.Read(candHeader, 0, candHeader.Length);
                        if (candRead >= 8)
                        {
                            var conf = EvaluateHeader(candHeader.AsSpan(0, candRead), fileLen, options, fs, globalCandidateOffset);
                            if (conf.IsAcceptable)
                            {
                                var candVersion = BinaryPrimitives.ReadInt32LittleEndian(candHeader.AsSpan(4, 4));
                                candidates.Add(new MetadataCandidate
                                {
                                    Offset = globalCandidateOffset,
                                    Method = MetadataRecoveryMethod.PrefixedPayload,
                                    Version = candVersion,
                                    Confidence = conf,
                                    Magic = MetadataFingerprintRegistry.CanonicalMagic,
                                    RestoreMagic = false
                                });
                            }
                        }
                    }
                }

                if (bytesRead < ChunkSize) break;
                currentOffset += bytesRead - Overlap;
            }
        }

        // ----------------------------------------------------
        // Candidate Ranking & Selection (Part 5)
        // ----------------------------------------------------
        if (candidates.Count > 0)
        {
            // Pick strongest candidate
            var best = candidates.OrderByDescending(c => c.Confidence.Score).ThenBy(c => c.Offset).First();

            logger?.Invoke($"[Recovery] Selected best metadata candidate: Method={best.Method}, Offset=0x{best.Offset:X}, Version={best.Version}, Confidence={best.Confidence.Score}% (Total candidates evaluated: {candidates.Count})");

            if (best.Method == MetadataRecoveryMethod.Standard && best.Offset == 0 && !best.RestoreMagic)
            {
                return new MetadataRecoveryResult
                {
                    Success = true,
                    ResultPath = metadataPath,
                    Method = MetadataRecoveryMethod.Standard,
                    ConfidenceScore = best.Confidence.Score,
                    Offset = 0,
                    Version = best.Version,
                    WasNormalized = false
                };
            }

            string normPath;
            if (best.XorKey != null && best.XorKey.Length > 0)
            {
                normPath = WriteDecryptedFile(fs, best.Offset, fileLen, best.XorKey, tempDir, best.RestoreMagic);
            }
            else
            {
                normPath = WriteNormalizedFile(fs, best.Offset, fileLen, best.RestoreMagic, null, tempDir);
            }

            return new MetadataRecoveryResult
            {
                Success = true,
                ResultPath = normPath,
                Method = best.Method,
                ConfidenceScore = best.Confidence.Score,
                Offset = best.Offset,
                XorKey = best.XorKey,
                Version = best.Version,
                Diagnostic = best.Diagnostic,
                WasNormalized = true
            };
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

        // Known diagnostics if no candidate met threshold
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

        var outPath = Path.Combine(targetDir, $"decrypted_metadata_{Guid.NewGuid():N}.dat");

        using var xorStream = new XorStream(srcStream, key, offset);
        using var outFs = File.Create(outPath);

        if (restoreMagic)
        {
            outFs.Write(CanonicalMagicBytes, 0, CanonicalMagicBytes.Length);
            xorStream.Position = 4;
        }

        xorStream.CopyTo(outFs);
        return outPath;
    }
}
