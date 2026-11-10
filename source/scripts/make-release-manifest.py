#!/usr/bin/env python3
"""Write latest.json for the rebuild release feed (PluginUpdateService.ReleaseManifest).

Usage: make-release-manifest.py <dll> <four-part-version> <output-json>
The download URL sits next to latest.json in the release repository, as the updater requires a
same-origin DLL URL. The installed updater re-verifies SHA-256, size, PE identity and version.
"""
import hashlib, json, re, sys
from pathlib import Path

FEED_BASE = 'https://raw.githubusercontent.com/icekale/StrmAssistantRebuild.Releases/main/'
dll, version, output = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', version):
    sys.exit('version must have four numeric components, e.g. 0.1.145.0')
data = dll.read_bytes()
if not data.startswith(b'MZ') or len(data) > 64 * 1024 * 1024:
    sys.exit('not a PE file or larger than 64 MiB')
manifest = {
    'PluginId': '97d51f81-cd66-4e74-a2f3-e0c3687cf738',
    'AssemblyName': 'StrmAssistantRebuild',
    'FileName': 'StrmAssistantRebuild.dll',
    'Version': version,
    'TargetFramework': '.NETCoreApp,Version=v6.0',
    'Architecture': 'AnyCPU',
    'Channel': 'stable',
    'Sha256': hashlib.sha256(data).hexdigest(),
    'Size': len(data),
    'DownloadUrl': FEED_BASE + 'StrmAssistantRebuild.dll',
}
output.write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps({k: manifest[k] for k in ('Version', 'Sha256', 'Size')}))
