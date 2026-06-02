#!/usr/bin/env python3
import argparse
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PUBLISH_DIR = ROOT / "publish"
DIST_DIR = ROOT / "dist"
WAP_DIR = ROOT / "Protractor.Package"
APPPACKAGES_DIR = WAP_DIR / "AppPackages"


def cmd_build(args):
    """Run dotnet publish then zip the output."""
    print("==> dotnet publish -c Release -o publish")
    result = subprocess.run(
        ["dotnet", "publish", "-c", "Release", "-o", str(PUBLISH_DIR)],
        cwd=ROOT,
    )
    if result.returncode != 0:
        sys.exit(result.returncode)

    zip_name = f"Protractor-{args.version}.zip" if args.version else "Protractor.zip"
    DIST_DIR.mkdir(parents=True, exist_ok=True)
    zip_path = DIST_DIR / zip_name

    print(f"==> Creating {zip_path}")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for file in sorted(PUBLISH_DIR.iterdir()):
            zf.write(file, arcname=file.name)

    print(f"==> Done: {zip_path} ({_size_str(zip_path)})")


def cmd_msix(args):
    """Build and sign an MSIX package using MakeAppx + SignTool (Windows SDK)."""
    cert_path = WAP_DIR / "Cert.pfx"
    config = args.configuration or "Release"

    # 1) Generate self-signed certificate if missing
    if not cert_path.exists():
        print("==> No Cert.pfx found, generating self-signed certificate...")
        ps_cmd = (
            f'$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=ScreenProtractor" '
            f'-KeyUsage DigitalSignature -TextExtension @("2.5.29.37={{text}}1.3.6.1.5.5.7.3.3") '
            f'-CertStoreLocation "Cert:\\CurrentUser\\My"; '
            f'$pwd = ConvertTo-SecureString -String "temp1234" -Force -AsPlainText; '
            f'Export-PfxCertificate -Cert $cert -FilePath "{cert_path}" -Password $pwd'
        )
        subprocess.run(
            ["powershell", "-NoProfile", "-Command", ps_cmd], cwd=ROOT, check=True
        )

    # 2) dotnet publish
    publish_dir = ROOT / "msix-content"
    if publish_dir.exists():
        shutil.rmtree(publish_dir)

    print(f"==> dotnet publish -c {config} -o {publish_dir}")
    subprocess.run(
        [
            "dotnet",
            "publish",
            "Protractor.csproj",
            "-c",
            config,
            "-o",
            str(publish_dir),
        ],
        cwd=ROOT,
        check=True,
    )

    # 3) Copy manifest + icons into publish output
    shutil.copy2(
        ROOT / "Protractor.Package" / "Package.appxmanifest",
        publish_dir / "AppxManifest.xml",
    )
    assets_src = WAP_DIR / "Assets"
    if assets_src.exists():
        shutil.copytree(assets_src, publish_dir / "Assets", dirs_exist_ok=True)

    # 4) MakeAppx: pack into .msix
    msix_path = ROOT / "Protractor.msix"
    kits_root = (
        Path(os.environ.get("ProgramFiles(x86)", "C:\\Program Files (x86)"))
        / "Windows Kits"
        / "10"
    )
    makeappx = next(kits_root.rglob("makeappx.exe"), None)
    if not makeappx:
        print("Error: makeappx.exe not found in Windows Kits")
        sys.exit(1)
    print(f"==> Packing MSIX with {makeappx}")
    subprocess.run(
        [str(makeappx), "pack", "/d", str(publish_dir), "/p", str(msix_path), "/l"],
        cwd=ROOT,
        check=True,
    )

    # 5) SignTool: sign the .msix
    signtool = makeappx.parent / "signtool.exe"
    print(f"==> Signing MSIX with {signtool}")
    subprocess.run(
        [
            str(signtool),
            "sign",
            "/fd",
            "SHA256",
            "/a",
            "/f",
            str(cert_path),
            "/p",
            "temp1234",
            str(msix_path),
        ],
        cwd=ROOT,
        check=True,
    )

    print(f"==> MSIX package created: {msix_path} ({_size_str(msix_path)})")


def cmd_clean(args):
    """Remove publish/, dist/, msix-content/ and generated .msix files."""
    for d in [PUBLISH_DIR, DIST_DIR, ROOT / "msix-content", APPPACKAGES_DIR]:
        if d.exists():
            shutil.rmtree(d)
            print(f"Removed {d}")
    for f in ROOT.glob("*.msix"):
        f.unlink()
        print(f"Removed {f}")


def _size_str(path: Path) -> str:
    size = path.stat().st_size
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024:
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"


def main():
    parser = argparse.ArgumentParser(description="Protractor build script")
    parser.add_argument(
        "command", choices=["build", "msix", "clean"], help="Command to run"
    )
    parser.add_argument(
        "--version",
        "-v",
        help="Version tag for the zip filename (e.g. 1.0.0)",
    )
    parser.add_argument(
        "--configuration",
        "-c",
        default="Release",
        help="Build configuration (Debug/Release, default: Release)",
    )
    args = parser.parse_args()

    if args.command == "build":
        cmd_build(args)
    elif args.command == "msix":
        cmd_msix(args)
    elif args.command == "clean":
        cmd_clean(args)


if __name__ == "__main__":
    main()
