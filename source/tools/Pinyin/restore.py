#!/usr/bin/env python3
"""Restore the exact official ToolGood package locally; no NuGet/global install."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent
VERSION = "3.1.0.3"
URL = f"https://api.nuget.org/v3-flatcontainer/toolgood.words/{VERSION}/toolgood.words.{VERSION}.nupkg"
PACKAGE_SHA256 = "b763f2c87847614020a6df23735e95f71313d840bdf8b308978384dec19c6354"
DLL_SHA256 = "4ad505939e924049a4d89f0d275090fbf6b2988a74e4bbaa30257ae67e77dcc2"
DLL_MEMBER = "lib/netstandard2.1/ToolGood.Words.dll"
MEMBERS = (DLL_MEMBER, "lib/netstandard2.1/ToolGood.Words.xml", "ToolGood.Words.nuspec", "LICENSE", ".signature.p7s")


def verify(data, expected, label):
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected:
        raise SystemExit(f"SHA256 mismatch for {label}: expected {expected}, got {actual}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--offline", action="store_true", help="Use the cached package only; fail if absent")
    parser.add_argument("--verify-only", action="store_true", help="Check cached package and extracted DLL, with no writes/network")
    args = parser.parse_args()
    package = ROOT / "packages" / f"toolgood.words.{VERSION}.nupkg"
    if not package.exists():
        if args.offline or args.verify_only:
            raise SystemExit(f"Missing cached package: {package}")
        with urllib.request.urlopen(URL, timeout=120) as response:
            data = response.read()
            final_url = response.url
        verify(data, PACKAGE_SHA256, "download")
        package.parent.mkdir(parents=True, exist_ok=True)
        temp = package.with_suffix(".download")
        temp.write_bytes(data)
        os.replace(temp, package)
        print(f"Downloaded {URL}; final URL: {final_url}")
    verify(package.read_bytes(), PACKAGE_SHA256, "package")
    directory = ROOT / "packages" / VERSION
    if args.verify_only:
        verify((directory / DLL_MEMBER).read_bytes(), DLL_SHA256, "DLL")
    else:
        with zipfile.ZipFile(package) as archive:
            verify(archive.read(DLL_MEMBER), DLL_SHA256, "embedded DLL")
            # Fixed members only: no arbitrary archive paths or native/other-TFM files.
            for member in MEMBERS:
                path = directory / member
                path.parent.mkdir(parents=True, exist_ok=True)
                data = archive.read(member)
                if not path.exists() or path.read_bytes() != data:
                    temp = path.with_suffix(path.suffix + ".restore")
                    temp.write_bytes(data)
                    os.replace(temp, path)
        verify((directory / DLL_MEMBER).read_bytes(), DLL_SHA256, "restored DLL")
    print(json.dumps({"version": VERSION, "package": str(package), "package_sha256": PACKAGE_SHA256,
                      "dll": str(directory / DLL_MEMBER), "dll_sha256": DLL_SHA256}, indent=2))


if __name__ == "__main__":
    main()
