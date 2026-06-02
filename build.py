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


def cmd_clean(args):
    """Remove publish/ and dist/ directories."""
    for d in [PUBLISH_DIR, DIST_DIR]:
        if d.exists():
            shutil.rmtree(d)
            print(f"Removed {d}")


def _size_str(path: Path) -> str:
    size = path.stat().st_size
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024:
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"


def main():
    parser = argparse.ArgumentParser(description="Protractor build script")
    parser.add_argument("command", choices=["build", "clean"], help="Command to run")
    parser.add_argument(
        "--version",
        "-v",
        help="Version tag for the zip filename (e.g. 1.0.0)",
    )
    args = parser.parse_args()

    if args.command == "build":
        cmd_build(args)
    elif args.command == "clean":
        cmd_clean(args)


if __name__ == "__main__":
    main()
