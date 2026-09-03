using System.Text;
using Il2CppDumper.Core.Model;

namespace Il2CppDumper.Core.Exporters;

public sealed class ScriptExporter : IExporter
{
    public string Name => "Disassembler Scripts (IDA, Ghidra, Binary Ninja)";

    public void Export(DumpContext context, string outputDirectory, ExportOptions options, Action<string>? logger = null)
    {
        Directory.CreateDirectory(outputDirectory);

        if (options.ExportIdaScript)
        {
            ExportIdaScript(context, outputDirectory, logger);
        }

        if (options.ExportGhidraScript)
        {
            ExportGhidraScript(context, outputDirectory, logger);
        }

        if (options.ExportBinjaScript)
        {
            ExportBinjaScript(context, outputDirectory, logger);
        }
    }

    private static void ExportIdaScript(DumpContext context, string outputDirectory, Action<string>? logger)
    {
        var path = Path.Combine(outputDirectory, "ida.py");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);

        writer.WriteLine("# Auto-generated IDA Pro Python script by Il2Cpp-Dumper (All-in-One)");
        writer.WriteLine("# Run in IDA Pro: File -> Script file... -> Select this ida.py");
        writer.WriteLine("import idaapi");
        writer.WriteLine("import idc");
        writer.WriteLine("import idautils");
        writer.WriteLine("import json");
        writer.WriteLine("import os");
        writer.WriteLine();
        writer.WriteLine("def set_name(ea, name):");
        writer.WriteLine("    idc.set_name(ea, name, idc.SN_CHECK | idc.SN_NOWARN)");
        writer.WriteLine();
        writer.WriteLine("def set_comment(ea, comment):");
        writer.WriteLine("    idc.set_cmt(ea, comment, 1)");
        writer.WriteLine();
        writer.WriteLine("def run():");
        writer.WriteLine("    base = idaapi.get_imagebase()");
        writer.WriteLine("    script_dir = os.path.dirname(os.path.abspath(__file__))");
        writer.WriteLine("    json_path = os.path.join(script_dir, 'script.json')");
        writer.WriteLine("    if not os.path.exists(json_path):");
        writer.WriteLine("        print(f'[-] script.json not found at {json_path}')");
        writer.WriteLine("        return");
        writer.WriteLine();
        writer.WriteLine("    print('[+] Loading script.json...')");
        writer.WriteLine("    with open(json_path, 'r', encoding='utf-8') as f:");
        writer.WriteLine("        data = json.load(f)");
        writer.WriteLine();
        writer.WriteLine("    methods = data.get('ScriptMethods', [])");
        writer.WriteLine("    print(f'[+] Applying {len(methods)} method symbols to IDA...')");
        writer.WriteLine("    count = 0");
        writer.WriteLine("    for m in methods:");
        writer.WriteLine("        rva = m.get('Rva', 0)");
        writer.WriteLine("        if rva == 0:");
        writer.WriteLine("            continue");
        writer.WriteLine("        ea = base + rva");
        writer.WriteLine("        name = m.get('Name', '').replace(' ', '_').replace('.', '_').replace('$', '_').replace('<', '_').replace('>', '_')");
        writer.WriteLine("        sig = m.get('Signature', '')");
        writer.WriteLine("        set_name(ea, name)");
        writer.WriteLine("        if sig:");
        writer.WriteLine("            set_comment(ea, sig)");
        writer.WriteLine("        count += 1");
        writer.WriteLine();
        writer.WriteLine("    print(f'[+] Renamed {count} functions successfully in IDA.')");
        writer.WriteLine();
        writer.WriteLine("if __name__ == '__main__':");
        writer.WriteLine("    run()");

        logger?.Invoke($"Wrote: {path}");
    }

    private static void ExportGhidraScript(DumpContext context, string outputDirectory, Action<string>? logger)
    {
        var path = Path.Combine(outputDirectory, "ghidra.py");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);

        writer.WriteLine("# Auto-generated Ghidra Python script by Il2Cpp-Dumper (All-in-One)");
        writer.WriteLine("# @category Il2Cpp");
        writer.WriteLine("import json");
        writer.WriteLine("import os");
        writer.WriteLine();
        writer.WriteLine("def run():");
        writer.WriteLine("    base = currentProgram.getImageBase().getOffset()");
        writer.WriteLine("    script_dir = os.path.dirname(os.path.abspath(__file__))");
        writer.WriteLine("    json_path = os.path.join(script_dir, 'script.json')");
        writer.WriteLine("    if not os.path.exists(json_path):");
        writer.WriteLine("        print('[-] script.json not found')");
        writer.WriteLine("        return");
        writer.WriteLine();
        writer.WriteLine("    with open(json_path, 'r', encoding='utf-8') as f:");
        writer.WriteLine("        data = json.load(f)");
        writer.WriteLine();
        writer.WriteLine("    methods = data.get('ScriptMethods', [])");
        writer.WriteLine("    print('Applying {} symbols in Ghidra...'.format(len(methods)))");
        writer.WriteLine("    listing = currentProgram.getListing()");
        writer.WriteLine("    for m in methods:");
        writer.WriteLine("        rva = m.get('Rva', 0)");
        writer.WriteLine("        if rva == 0: continue");
        writer.WriteLine("        addr = currentAddress.getAddress(hex(base + rva))");
        writer.WriteLine("        name = m.get('Name', '').replace(' ', '_').replace('.', '_').replace('$', '_').replace('<', '_').replace('>', '_')");
        writer.WriteLine("        sig = m.get('Signature', '')");
        writer.WriteLine("        func = getFunctionAt(addr)");
        writer.WriteLine("        if func is None:");
        writer.WriteLine("            func = createFunction(addr, name)");
        writer.WriteLine("        else:");
        writer.WriteLine("            func.setName(name, ghidra.program.model.symbol.SourceType.USER_DEFINED)");
        writer.WriteLine("        if sig and func is not None:");
        writer.WriteLine("            func.setRepeatableComment(sig)");
        writer.WriteLine();
        writer.WriteLine("    print('Ghidra symbol import complete.')");
        writer.WriteLine();
        writer.WriteLine("if __name__ == '__main__':");
        writer.WriteLine("    run()");

        logger?.Invoke($"Wrote: {path}");
    }

    private static void ExportBinjaScript(DumpContext context, string outputDirectory, Action<string>? logger)
    {
        var path = Path.Combine(outputDirectory, "binja.py");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);

        writer.WriteLine("# Auto-generated Binary Ninja Python script by Il2Cpp-Dumper (All-in-One)");
        writer.WriteLine("import json");
        writer.WriteLine("import os");
        writer.WriteLine("from binaryninja import Symbol, SymbolType");
        writer.WriteLine();
        writer.WriteLine("def run(bv):");
        writer.WriteLine("    base = bv.start");
        writer.WriteLine("    script_dir = os.path.dirname(os.path.abspath(__file__))");
        writer.WriteLine("    json_path = os.path.join(script_dir, 'script.json')");
        writer.WriteLine("    if not os.path.exists(json_path):");
        writer.WriteLine("        print('[-] script.json not found')");
        writer.WriteLine("        return");
        writer.WriteLine();
        writer.WriteLine("    with open(json_path, 'r', encoding='utf-8') as f:");
        writer.WriteLine("        data = json.load(f)");
        writer.WriteLine();
        writer.WriteLine("    methods = data.get('ScriptMethods', [])");
        writer.WriteLine("    count = 0");
        writer.WriteLine("    for m in methods:");
        writer.WriteLine("        rva = m.get('Rva', 0)");
        writer.WriteLine("        if rva == 0: continue");
        writer.WriteLine("        ea = base + rva");
        writer.WriteLine("        name = m.get('Name', '').replace(' ', '_').replace('.', '_').replace('$', '_').replace('<', '_').replace('>', '_')");
        writer.WriteLine("        bv.define_user_symbol(Symbol(SymbolType.FunctionSymbol, ea, name))");
        writer.WriteLine("        sig = m.get('Signature', '')");
        writer.WriteLine("        if sig:");
        writer.WriteLine("            bv.set_comment_at(ea, sig)");
        writer.WriteLine("        count += 1");
        writer.WriteLine();
        writer.WriteLine("    print(f'[+] Imported {count} symbols into Binary Ninja')");
        writer.WriteLine();
        writer.WriteLine("if __name__ == '__main__':");
        writer.WriteLine("    # When run inside Binary Ninja console: run(bv)");
        writer.WriteLine("    pass");

        logger?.Invoke($"Wrote: {path}");
    }
}
