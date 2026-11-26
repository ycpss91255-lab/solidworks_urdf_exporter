"""Live test: limit mates whose value is driven by equations and global variables (issue 170).

What it does, start to finish:
  1. starts its OWN SolidWorks instance with your guarded launcher (--launcher, or the environment variable
     SW2URDF_SW_LAUNCHER), or binds to an instance you own and name with --pid;
  2. builds the small LIMIT_EQUATIONS scratch model (see make_limit_equation_fixture.py) in a temporary folder;
  3. compiles scripts/LimitEquationsLiveProbe.cs against a built SW2URDF.dll and runs it. The probe reads the
     limit mates the way the exporter does and compares the variations with the exact doubles computed from the
     typed numbers: global variables, a plain equation, a variable change plus rebuild, two configurations;
  4. prints PASS/FAIL per check, closes the instance it started (never one given with --pid) and deletes the
     temporary folder.

Usage:
    python scripts/test_limit_equations_live.py --dll <folder with a built SW2URDF.dll> --launcher <launch_solidworks.py>
    python scripts/test_limit_equations_live.py --dll <folder> --pid <pid of an instance with no open documents>

Build the DLL without registering it:  powershell -File scripts\\test_build.ps1 -OutputDirectory <folder>
(sign it first if your policy requires signed binaries). Needs pywin32 and the .NET Framework 4 csc.exe.
Exit code 0 when every check passes.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

import pythoncom

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_limit_equation_fixture as fixture  # noqa: E402
from test_configuration_live import csc_path, exit_instance, launch, solidworks_dir  # noqa: E402

PROBE = "LimitEquationsLiveProbe"


def compile_probe(dll_dir, thumbprint):
    sw = solidworks_dir()
    for name in ("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll"):
        if not os.path.isfile(os.path.join(dll_dir, name)) and sw:
            shutil.copy(os.path.join(sw, name), dll_dir)
    exe = os.path.join(dll_dir, PROBE + ".exe")
    framework = os.path.dirname(csc_path())
    references = ["System.Windows.Forms.dll", "System.Drawing.dll", "System.Core.dll",
                  os.path.join(framework, "WPF", "WindowsBase.dll"), os.path.join(framework, "WPF", "PresentationFramework.dll"),
                  os.path.join(dll_dir, "SW2URDF.dll")]
    # SW2URDF.dll embeds the SolidWorks interop types, so the probe must embed them too (/link)
    interop = [os.path.join(dll_dir, n) for n in ("SolidWorks.Interop.sldworks.dll", "SolidWorks.Interop.swconst.dll")]
    command = [csc_path(), "/nologo", "/target:exe", "/platform:x64", "/out:" + exe]
    command += ["/r:" + r for r in references] + ["/link:" + i for i in interop]
    command.append(os.path.join(HERE, PROBE + ".cs"))
    subprocess.run(command, check=True)
    if thumbprint:
        sign = ("$c = Get-Item Cert:/CurrentUser/My/%s; $r = Set-AuthenticodeSignature -FilePath '%s' -Certificate $c "
                "-TimestampServer http://timestamp.digicert.com; if ($r.Status -ne 'Valid') { exit 1 }" % (thumbprint, exe))
        subprocess.run(["powershell", "-NoProfile", "-Command", sign], check=True)
    return exe


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dll", required=True, help="folder containing a built SW2URDF.dll (the probe is built into it)")
    parser.add_argument("--launcher", default=os.environ.get("SW2URDF_SW_LAUNCHER"),
                        help="guarded launcher script that starts a new SolidWorks (or env SW2URDF_SW_LAUNCHER)")
    parser.add_argument("--pid", type=int, help="use this instance (not closed afterwards); it must hold no documents")
    parser.add_argument("--sign", metavar="THUMBPRINT",
                        help="code signing certificate (CurrentUser/My) used to sign the probe exe, if your policy requires signed binaries")
    parser.add_argument("--keep", action="store_true", help="keep the scratch folder")
    args = parser.parse_args()
    if not args.pid and not args.launcher:
        parser.error("give --launcher (to start an instance) or --pid (to use one you own)")
    dll_dir = os.path.abspath(args.dll)
    if not os.path.isfile(os.path.join(dll_dir, "SW2URDF.dll")):
        parser.error("no SW2URDF.dll in " + dll_dir)

    work = tempfile.mkdtemp(prefix="sw2urdf_limit_eq_live_")
    started = None
    code = 1
    try:
        exe = compile_probe(dll_dir, args.sign)
        pid = args.pid
        if not pid:
            pid = started = launch(args.launcher, work)
        print("Using SolidWorks process %d%s" % (pid, " (started by this script)" if started else ""))
        assembly = fixture.build(fixture.bind(pid), os.path.join(work, "models"))
        # the exporter writes its log under the user profile folder; keep this run's log private
        home = os.path.join(work, "home")
        os.makedirs(home)
        environment = dict(os.environ, HOMEDRIVE=os.path.splitdrive(home)[0], HOMEPATH=os.path.splitdrive(home)[1])
        code = subprocess.run([exe, str(pid), assembly], cwd=dll_dir, env=environment).returncode
    finally:
        if started:
            exit_instance(started)
        try:
            os.remove(os.path.join(dll_dir, PROBE + ".exe"))
        except OSError:
            pass
        if args.keep:
            print("Scratch folder kept: " + work)
        else:
            shutil.rmtree(work, ignore_errors=True)
    return code


if __name__ == "__main__":
    pythoncom.CoInitialize()
    sys.exit(main())
