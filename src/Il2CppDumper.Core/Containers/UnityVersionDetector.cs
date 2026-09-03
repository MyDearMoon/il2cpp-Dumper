using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AssetRipper.Primitives;
using LibCpp2IL;

namespace Il2CppDumper.Core.Containers;

public static class UnityVersionDetector
{
    private static readonly Regex VersionRegex = new(@"\b([2-9]\d{3}\.\d+\.\d+[a-z0-9]*)\b", RegexOptions.Compiled);
    private static readonly Regex PlistVersionRegex = new(@"<key>UnityVersion</key>\s*<string>([^<]+)</string>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static UnityVersion Detect(string? gameDirectory, string? binaryPath, string? metadataPath, Action<string>? logger = null)
    {
        var searchDirs = new List<string>();
        if (!string.IsNullOrEmpty(gameDirectory) && Directory.Exists(gameDirectory)) searchDirs.Add(gameDirectory);
        if (!string.IsNullOrEmpty(binaryPath))
        {
            var binDir = Path.GetDirectoryName(binaryPath);
            if (!string.IsNullOrEmpty(binDir) && !searchDirs.Contains(binDir)) searchDirs.Add(binDir);
            var parentDir = Path.GetDirectoryName(binDir);
            if (!string.IsNullOrEmpty(parentDir) && !searchDirs.Contains(parentDir)) searchDirs.Add(parentDir);
        }

        // 1. Check UnityPlayer.dll (Windows)
        foreach (var dir in searchDirs)
        {
            var playerDll = Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(playerDll))
            {
                try
                {
                    var fileVer = FileVersionInfo.GetVersionInfo(playerDll).FileVersion;
                    if (!string.IsNullOrEmpty(fileVer) && TryParseVersion(fileVer, out var ver))
                    {
                        logger?.Invoke($"Detected Unity version from UnityPlayer.dll: {ver}");
                        return ver;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Debug] Error reading UnityPlayer.dll version: {ex.Message}");
                }
            }
        }

        // 2. Check libunity.so / UnityPlayer.so (Android / Linux)
        foreach (var dir in searchDirs)
        {
            foreach (var soName in new[] { "libunity.so", "UnityPlayer.so" })
            {
                var soFiles = Directory.GetFiles(dir, soName, SearchOption.AllDirectories);
                foreach (var soFile in soFiles)
                {
                    try
                    {
                        using var fs = File.OpenRead(soFile);
                        var buffer = new byte[Math.Min(fs.Length, 128 * 1024)];
                        fs.ReadExactly(buffer, 0, buffer.Length);
                        var text = Encoding.ASCII.GetString(buffer);
                        var match = VersionRegex.Match(text);
                        if (match.Success && TryParseVersion(match.Value, out var ver))
                        {
                            logger?.Invoke($"Detected Unity version from {soName}: {ver}");
                            return ver;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Debug] Error reading {soName}: {ex.Message}");
                    }
                }
            }
        }

        // 3. Check Info.plist (macOS / iOS)
        foreach (var dir in searchDirs)
        {
            var plistFiles = Directory.GetFiles(dir, "Info.plist", SearchOption.AllDirectories);
            foreach (var plist in plistFiles)
            {
                try
                {
                    var content = File.ReadAllText(plist);
                    var match = PlistVersionRegex.Match(content);
                    if (match.Success && TryParseVersion(match.Groups[1].Value, out var ver))
                    {
                        logger?.Invoke($"Detected Unity version from Info.plist: {ver}");
                        return ver;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Debug] Error reading Info.plist: {ex.Message}");
                }
            }
        }

        // 4. Check globalgamemanagers / data.unity3d
        foreach (var dir in searchDirs)
        {
            var dataDirs = Directory.GetDirectories(dir, "*_Data", SearchOption.AllDirectories);
            foreach (var dataDir in dataDirs)
            {
                var ggm = Path.Combine(dataDir, "globalgamemanagers");
                if (File.Exists(ggm))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(ggm);
                        var ver = LibCpp2IlMain.GetVersionFromGlobalGameManagers(bytes);
                        if (ver != default && ver.Major > 0)
                        {
                            logger?.Invoke($"Detected Unity version from globalgamemanagers: {ver}");
                            return ver;
                        }

                        // Regex scan on first 2KB of globalgamemanagers
                        var text = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
                        var match = VersionRegex.Match(text);
                        if (match.Success && TryParseVersion(match.Value, out var parsed))
                        {
                            logger?.Invoke($"Detected Unity version string from globalgamemanagers: {parsed}");
                            return parsed;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Debug] Error reading globalgamemanagers: {ex.Message}");
                    }
                }
            }
        }

        return default;
    }

    public static bool TryParseVersion(string input, out UnityVersion version)
    {
        try
        {
            var clean = VersionRegex.Match(input);
            var str = clean.Success ? clean.Value : input.Trim();

            // Strip trailing letter/numbers like f1, b2, a0
            var parts = str.Split('.');
            if (parts.Length >= 3)
            {
                var major = ushort.Parse(parts[0]);
                var minor = ushort.Parse(parts[1]);

                var buildStr = parts[2];
                var digits = new string(buildStr.TakeWhile(char.IsDigit).ToArray());
                var build = ushort.Parse(digits);

                version = new UnityVersion(major, minor, build);
                return true;
            }
        }
        catch
        {
            // Parse failure
        }

        version = default;
        return false;
    }
}
