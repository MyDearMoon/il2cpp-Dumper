using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AssetRipper.Primitives;
using LibCpp2IL;

namespace Il2CppDumper.Core.Containers;

public static class UnityVersionDetector
{
    private static readonly Regex VersionRegex = new(@"\b(20\d\d\.\d+\.\d+[a-z0-9]*)\b", RegexOptions.Compiled);

    public static UnityVersion Detect(string? gameDirectory, string? binaryPath, string? metadataPath, Action<string>? logger = null)
    {
        // 1. Check UnityPlayer.dll / UnityPlayer.so
        var searchDirs = new List<string>();
        if (!string.IsNullOrEmpty(gameDirectory) && Directory.Exists(gameDirectory)) searchDirs.Add(gameDirectory);
        if (!string.IsNullOrEmpty(binaryPath))
        {
            var binDir = Path.GetDirectoryName(binaryPath);
            if (!string.IsNullOrEmpty(binDir) && !searchDirs.Contains(binDir)) searchDirs.Add(binDir);
            var parentDir = Path.GetDirectoryName(binDir);
            if (!string.IsNullOrEmpty(parentDir) && !searchDirs.Contains(parentDir)) searchDirs.Add(parentDir);
        }

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
                catch
                {
                    // Ignore
                }
            }
        }

        // 2. Check globalgamemanagers or data.unity3d
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
                    catch
                    {
                        // Ignore
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

            // Strip trailing letter/numbers like f1, b2
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
            // Ignore
        }

        version = default;
        return false;
    }
}
