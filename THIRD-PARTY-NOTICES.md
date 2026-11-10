# Third-party notices

- The supplied StrmAssistant OSS baseline is retained under its [GPL-3.0 license](<LICENSE>). Dashboard SDK, shortcut command source and localization text are derived from that baseline; this reconstruction is visibly named Strm Assistant Rebuild and does not reuse its PluginId.
- Harmony 2.4.2 is merged and internalized for fingerprint-checked public thumbnail compatibility and default exclusive media control. Original [license](<notices/Harmony-LICENSE.txt>), [package](<tools/HarmonyCompatibility/packages/lib.harmony.2.4.2.nupkg>) and provenance are retained. No obfuscation or renamed obfuscated identifiers are applied.
- ToolGood.Words 3.1.0.3 is merged and internalized for dictionary-backed pinyin and Chinese conversion; its [Apache-2.0 license](<notices/ToolGood.Words-LICENSE.txt>) and [fixed package provenance](<artifacts/rebuild/d6-pinyin-notes.md>) are retained.
- ILRepack 2.0.48 is a build-time tool; it is not a runtime delivery dependency. Pinned package restoration is in [restore.py](<tools/HarmonyCompatibility/restore.py>).
- Emby assemblies are host references with `Private=false`, not redistributed in the deliverable DLL. Native dashboard fixtures are supplied-host bytes used for pinned transformations and runtime compatibility fallback; separate from reconstructed behavior.
