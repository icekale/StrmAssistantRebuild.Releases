#!/usr/bin/env python3
"""Restore the pinned packages and macOS/arm64 test runtime inside this directory only."""
import hashlib
import pathlib
import platform
import tarfile
import urllib.request
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent
PACKAGES = ROOT / "packages"
PACKAGES.mkdir(exist_ok=True)

def download(url, path, digest):
    if not path.exists():
        partial = path.with_suffix(path.suffix + ".download")
        try:
            urllib.request.urlretrieve(url, partial)
            if hashlib.sha256(partial.read_bytes()).hexdigest() != digest:
                raise ValueError("Download digest differs: " + url)
            partial.replace(path)
        finally:
            partial.unlink(missing_ok=True)
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise ValueError("Stored digest differs: " + str(path))
    print("VERIFIED", path.name)

for name, version, directory, digest in [
    ("lib.harmony", "2.4.2", "2.4.2", "d64592e53090464559fce48612c9ca7c8dc73113841376b7aa3455f46fc5d579"),
    ("dotnet-ilrepack", "2.0.48", "dotnet-ilrepack", "a6cf6386a763f94c270ba2d06e64cb6128fab39da6760f41967ed7a2a4c7b28e"),
]:
    package = PACKAGES / (name + "." + version + ".nupkg")
    download(f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg", package, digest)
    with zipfile.ZipFile(package) as archive:
        archive.extractall(PACKAGES / directory)

# This is the exact isolated test environment, not a machine-wide dotnet installation.
if platform.system() != "Darwin" or platform.machine() != "arm64":
    raise SystemExit("Packages restored. runtime6 fixture is pinned to macOS arm64; select a matching isolated runtime for other hosts.")
archive = ROOT / "runtime6.tar.gz"
download("https://builds.dotnet.microsoft.com/dotnet/Runtime/6.0.36/dotnet-runtime-6.0.36-osx-arm64.tar.gz", archive,
         "c4ea0b90326de8d90ee7d821f722add96a2f7c35f02c67f3ae0b697764290a4e")
(ROOT / "runtime6").mkdir(exist_ok=True)
with tarfile.open(archive) as tar:
    tar.extractall(ROOT / "runtime6", filter="data")
print("Ready: local .NET 6.0.36 arm64 runtime; building ILRepack tools requires the existing .NET 10 SDK.")
