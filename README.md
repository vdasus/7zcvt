# 7zcvt

by vdasus

Repacks archives into 7z, including archives nested inside them — an rcvt-style converter with 7z as the target format.

```
7zcvt aa.zip          ->  aa.7z
7zcvt -r -f D:\arc    ->  a whole tree repacked in place
```

## What it does

1. Extracts the source archive with 7-Zip (zip, rar, arj, lzh, cab, tar, gz, bz2, xz, zst, ...).
2. Walks the extracted tree and repacks every archive it finds there as `.7z`, up to `-d` levels deep.
3. Packs everything back into one `.7z`, then verifies it with `7z t`.
4. Reports the size delta. The source is kept unless `--delete` is given.

A single-stream container is peeled fully: `aa.tar.gz` becomes an `aa.7z` of the files, not of an intermediate `aa.tar`.

Encrypted or damaged nested archives are left untouched instead of failing the whole run; a source that cannot be extracted is reported and the next input is processed.

## Data safety

The source is only ever removed after the result has been proven complete. Every rule below is exercised by `--selftest`.

- **Extraction failure stops the archive.** A wrong password, a truncated file or a format 7-Zip cannot read leaves the source exactly as it was and produces no result. The run continues with the next input and exits with code `1`.
- **The result is verified twice.** First `7z t` (CRC of everything stored), then a parity check: every file extracted from the source must be present in the result with the same size. A mismatch never deletes or overwrites anything — the source is kept, and the result is kept beside it with a `warn` line.
- **`--delete` runs last.** It requires a verified, parity-checked result that exists on disk with the expected size. Anything less keeps the source.
- **The result is written by rename, not by copy.** It is staged in the destination folder as `.<name>.7z.7zcvt<pid>` and renamed into place, so the previous file is replaced atomically on the same volume. An interrupted run leaves a stray staging file, never a half-written archive.
- **No two inputs can collide.** `aa.zip` and `aa.tar.gz` both want `aa.7z`; the second one is written as `aa.tar.gz.7z` instead. A result never overwrites another input or a result produced earlier in the same run, `-f` included.
- **Scratch space is outside the extracted tree.** Temporary folders live under `%TEMP%\7zcvt-*`, so a folder inside an archive can never be mistaken for one of ours and deleted.
- **Nested archives fail closed.** A nested archive that cannot be extracted, packed, verified or parity-checked is kept as it is; only a proven replacement removes the original.

`-f` does what it says: it overwrites an existing `.7z` of the same name, including recompressing an archive in place. In-place replacement still passes the full verify-and-parity chain first.

## Options

| Option | Meaning |
| --- | --- |
| `-o DIR` | write results to `DIR` (default: next to the source) |
| `-r` | scan input directories recursively for archives |
| `-d N` | repack nested archives up to N levels deep (default 8, `0` = off) |
| `-mxN` | compression level 0..9 (default 9) |
| `-f` | overwrite an existing `.7z` |
| `--delete` | delete the source after the result is verified |
| `--only-smaller` | discard the result when it is not smaller than the source |
| `--max-size SIZE` | abort an archive whose contents exceed SIZE (default `10g`) |
| `--7z PATH` | path to the 7z executable to use |
| `-q` | quiet |
| `--selftest` | build a nested archive, convert it, verify the contents survive |

Exit codes: `0` success, `1` at least one archive failed, `2` bad arguments, `3` no 7-Zip engine.

## 7-Zip engine

The tool drives a 7-Zip executable rather than reimplementing the formats. It looks for one in this order:

1. `--7z PATH`
2. `7z.exe` on `PATH`
3. `%ProgramFiles%\7-Zip\7z.exe`, the x86 variant, `%LOCALAPPDATA%\Programs\7-Zip\7z.exe`
4. the copy bundled into the executable, unpacked once into `%LOCALAPPDATA%\7zcvt\engine-<version>\`

So an installed 7-Zip is used when present (usually newer), and the tool still works on a machine without one.

## Versioning

The version lives in one place: `<Version>` in `src/7zcvt.csproj`. Bump it on every build that is handed out, following semver:

- **patch** (`0.2.0` -> `0.2.1`) — bug fixes, no change to the command line
- **minor** (`0.2.0` -> `0.3.0`) — new options or behaviour, existing commands keep working
- **major** (`0.2.0` -> `1.0.0`) — a removed or repurposed option, or a different default

The version also names the bundled engine folder (`%LOCALAPPDATA%\7zcvt\engine-<version>`), so a bump ships a fresh copy of the engine as well.

## Build

Requires the .NET 10 SDK.

```powershell
# Everything at once: finds the MSVC toolset, publishes AOT into dist\, drops the .pdb.
.\build.cmd

# The same by hand. Native AOT, ~4 MB, no runtime needed.
# Needs the "Desktop development with C++" workload (MSVC linker) installed.
dotnet publish src\7zcvt.csproj -c Release -r win-x64 -o dist

# Fallback without the C++ workload: single-file self-contained, ~11 MB.
dotnet publish src\7zcvt.csproj -c Release -r win-x64 --self-contained -o dist `
  -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true
```

`assets/7z.exe` and `assets/7z.dll` are the bundled engine (7-Zip 25.01 x64, taken from the official installer). They are embedded only if the `assets` folder is present; without it the build still works and requires an installed 7-Zip. 7-Zip is by Igor Pavlov and is licensed under the GNU LGPL — see `assets/License.txt`.

## Origin and disclaimer

This tool was written by Claude (Anthropic's Claude Code) under my supervision: I set the requirements, reviewed the decisions and tested the result.

It was built for my own use. There is no warranty of any kind, express or implied, and no liability for any damage or data loss arising from using it. Use it at your own risk, and keep a backup of anything you cannot afford to lose.

`assets/7z.exe` and `assets/7z.dll` are unmodified official 7-Zip 25.01 x64 binaries by Igor Pavlov, redistributed under the GNU LGPL; see `assets/License.txt` and <https://www.7-zip.org>.
