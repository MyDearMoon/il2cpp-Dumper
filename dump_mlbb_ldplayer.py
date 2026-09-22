import os
import sys
import subprocess
import shutil
from pathlib import Path
from datetime import datetime

# ANSI Colors for terminal output
CYAN = "\033[96m"
GREEN = "\033[92m"
YELLOW = "\033[93m"
RED = "\033[91m"
BOLD = "\033[1m"
RESET = "\033[0m"

def log(msg, color=CYAN):
    print(f"{color}[*] {msg}{RESET}")

def log_success(msg):
    print(f"{GREEN}{BOLD}[+] {msg}{RESET}")

def log_warn(msg):
    print(f"{YELLOW}[!] {msg}{RESET}")

def log_error(msg):
    print(f"{RED}{BOLD}[-] {msg}{RESET}")

def find_adb():
    # 1. Check common LDPlayer paths
    candidates = [
        r"C:\LDPlayer\LDPlayer9\adb.exe",
        r"D:\LDPlayer\LDPlayer9\adb.exe",
        r"E:\LDPlayer\LDPlayer9\adb.exe",
        r"C:\Program Files\ldplayer9box\adb.exe",
        r"C:\LDPlayer\LDPlayer4\adb.exe",
        r"D:\LDPlayer\LDPlayer4\adb.exe",
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c

    # 2. Check system PATH
    which_adb = shutil.which("adb")
    if which_adb:
        return which_adb

    return None

def find_dumper():
    script_dir = Path(__file__).resolve().parent
    candidates = [
        script_dir / "build" / "Il2CppDumper.exe",
        script_dir / "src" / "Il2CppDumper.Cli" / "bin" / "Release" / "net9.0" / "win-x64" / "Il2CppDumper.exe",
        script_dir / "src" / "Il2CppDumper.Cli" / "bin" / "Debug" / "net9.0" / "Il2CppDumper.exe",
    ]
    for c in candidates:
        if c.is_file():
            return str(c)
    return None

def run_cmd(cmd, capture=True):
    res = subprocess.run(cmd, shell=True, text=True, capture_output=capture)
    return res.returncode, res.stdout.strip(), res.stderr.strip()

def main():
    print(f"{CYAN}{BOLD}")
    print("==========================================================")
    print("      MLBB LDPlayer 1-Click Automated IL2CPP Dumper       ")
    print("==========================================================")
    print(f"{RESET}")

    # 1. Locate ADB
    adb = find_adb()
    if not adb:
        log_error("Could not find adb.exe! Please verify LDPlayer is installed.")
        input("Press Enter to exit...")
        sys.exit(1)
    log(f"Found ADB: {adb}")

    # 2. Locate Il2CppDumper
    dumper = find_dumper()
    if not dumper:
        log_error("Could not find Il2CppDumper.exe! Please run 'dotnet build' first.")
        input("Press Enter to exit...")
        sys.exit(1)
    log(f"Found Dumper: {dumper}")

    # 3. Check ADB devices
    code, out, _ = run_cmd(f'"{adb}" devices')
    lines = [l for l in out.splitlines() if "\tdevice" in l]
    if not lines:
        log_error("No active emulator/device found! Please start LDPlayer and ensure MLBB is installed.")
        input("Press Enter to exit...")
        sys.exit(1)
    device_id = lines[0].split("\t")[0]
    log_success(f"Connected to device: {device_id}")

    # 4. Check MLBB package
    pkg_name = "com.mobile.legends"
    code, out, _ = run_cmd(f'"{adb}" -s {device_id} shell "pm path {pkg_name}"')
    if code != 0 or not out:
        log_error(f"Package '{pkg_name}' not found on emulator! Is MLBB installed?")
        input("Press Enter to exit...")
        sys.exit(1)

    apk_lines = out.splitlines()
    base_apk = ""
    for line in apk_lines:
        if "package:" in line:
            p = line.replace("package:", "").strip()
            if "base.apk" in p:
                base_apk = p
                break
    if not base_apk:
        base_apk = apk_lines[0].replace("package:", "").strip()
    
    app_dir = str(Path(base_apk).parent).replace("\\", "/")
    log(f"Found app installation directory: {app_dir}")

    # Setup directories
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    base_output = Path(os.path.expanduser("~")) / "Documents" / "MLBB_Dumps" / f"MLBB_Dump_{timestamp}"
    staging_dir = base_output / "raw_input"
    dump_output_dir = base_output / "output"

    staging_dir.mkdir(parents=True, exist_ok=True)
    dump_output_dir.mkdir(parents=True, exist_ok=True)

    log(f"Staging folder: {staging_dir}")
    log(f"Output folder:  {dump_output_dir}")

    # 5. Pull libil2cpp.so
    lib_path = f"{app_dir}/lib/arm64/libil2cpp.so"
    log(f"Pulling {lib_path}...")
    code, out, err = run_cmd(f'"{adb}" -s {device_id} pull "{lib_path}" "{staging_dir / "libil2cpp.so"}"')
    if code != 0 or not (staging_dir / "libil2cpp.so").is_file():
        alt_lib = f"{app_dir}/lib/arm/libil2cpp.so"
        log_warn(f"arm64 lib not found, trying {alt_lib}...")
        code, out, err = run_cmd(f'"{adb}" -s {device_id} pull "{alt_lib}" "{staging_dir / "libil2cpp.so"}"')

    if not (staging_dir / "libil2cpp.so").is_file():
        log_error("Failed to extract libil2cpp.so from emulator!")
        input("Press Enter to exit...")
        sys.exit(1)
    
    lib_size = (staging_dir / "libil2cpp.so").stat().st_size
    log_success(f"Successfully pulled libil2cpp.so ({lib_size:,} bytes)")

    # 6. Pull Moonton Partitioned Metadata files
    meta_dirs = [
        f"/sdcard/Android/data/{pkg_name}/files/dragon2017/assets/UnityData_NEW/Managed/Metadata",
        f"/sdcard/Android/data/{pkg_name}/files/il2cpp/Metadata",
        f"/sdcard/Android/data/{pkg_name}/files/dragon2017/assets/bin/Data/Managed/Metadata"
    ]

    meta_files = [
        "global-metadata.dat",
        "global-first-metadata.dat",
        "global-csharp-metadata.dat"
    ]

    for mdir in meta_dirs:
        log(f"Checking metadata directory: {mdir}...")
        found_any = False
        for mf in meta_files:
            remote_f = f"{mdir}/{mf}"
            local_f = staging_dir / mf
            c, o, e = run_cmd(f'"{adb}" -s {device_id} pull "{remote_f}" "{local_f}"')
            if local_f.is_file() and local_f.stat().st_size > 0:
                found_any = True
                log_success(f"Pulled {mf} ({local_f.stat().st_size:,} bytes)")
        if found_any:
            break

    main_meta = staging_dir / "global-metadata.dat"
    if not main_meta.is_file() or main_meta.stat().st_size == 0:
        log_error("Could not find or pull valid global-metadata.dat from emulator!")
        input("Press Enter to exit...")
        sys.exit(1)

    # 7. Execute Il2CppDumper
    log("Running Il2CppDumper on extracted files...")
    dumper_cmd = f'"{dumper}" "{staging_dir / "libil2cpp.so"}" "{main_meta}" "{dump_output_dir}"'
    subprocess.run(dumper_cmd, shell=True)

    log_success(f"Dumping complete! Output saved to:\n  {dump_output_dir}")
    
    # Open Explorer window
    if os.name == "nt":
        os.startfile(str(dump_output_dir))

if __name__ == "__main__":
    main()
