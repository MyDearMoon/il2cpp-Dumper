using Il2CppDumper.Core.Model;

namespace Il2CppDumper.Core.Exporters;

public class ExportOptions
{
    public bool ExportDumpCs { get; set; } = true;
    public bool ExportScriptJson { get; set; } = true;
    public bool ExportIdaScript { get; set; } = true;
    public bool ExportGhidraScript { get; set; } = true;
    public bool ExportBinjaScript { get; set; } = true;
    public bool ExportDummyDlls { get; set; } = true;
    public bool ExportCppSdk { get; set; } = true;
    public bool ExportFridaScripts { get; set; } = true;

    public static ExportOptions All => new()
    {
        ExportDumpCs = true,
        ExportScriptJson = true,
        ExportIdaScript = true,
        ExportGhidraScript = true,
        ExportBinjaScript = true,
        ExportDummyDlls = true,
        ExportCppSdk = true,
        ExportFridaScripts = true
    };
}

public sealed class ExportResult
{
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public List<string> GeneratedFiles { get; set; } = new();
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public interface IExporter
{
    string Name { get; }
    ExportResult Export(DumpContext context, string outputDirectory, ExportOptions options, Action<string>? logger = null);
}
