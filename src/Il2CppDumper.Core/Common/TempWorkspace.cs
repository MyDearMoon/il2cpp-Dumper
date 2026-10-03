namespace Il2CppDumper.Core.Common;

public sealed class TempWorkspace : IDisposable
{
    private readonly HashSet<string> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trackedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string WorkspaceRoot { get; }

    public TempWorkspace(string? baseDir = null, string prefix = "il2cpp_dumper_ws_")
    {
        var parent = string.IsNullOrEmpty(baseDir) ? Path.GetTempPath() : baseDir;
        WorkspaceRoot = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceRoot);
        _trackedDirectories.Add(WorkspaceRoot);
    }

    public string GetTempFilePath(string fileNameOrExt = ".tmp")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string fileName = fileNameOrExt.StartsWith('.')
            ? $"{Guid.NewGuid():N}{fileNameOrExt}"
            : fileNameOrExt;

        var fullPath = Path.Combine(WorkspaceRoot, fileName);
        _trackedFiles.Add(fullPath);
        return fullPath;
    }

    public string CreateSubdirectory(string subDirName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dirPath = Path.Combine(WorkspaceRoot, subDirName);
        Directory.CreateDirectory(dirPath);
        _trackedDirectories.Add(dirPath);
        return dirPath;
    }

    public void TrackFile(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _trackedFiles.Add(path);
        }
    }

    public void TrackDirectory(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _trackedDirectories.Add(path);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var file in _trackedFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        try
        {
            if (Directory.Exists(WorkspaceRoot))
            {
                Directory.Delete(WorkspaceRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
