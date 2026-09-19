# SeekAI V1

A small Windows tray launcher for **filename, full-text, and local semantic search**. Native WPF / .NET 10, SQLite FTS5, and Ollama `nomic-embed-text`. No cloud AI, accounts, analytics, or telemetry.

## Run

Double-click `Launch-SeekAI.cmd`, or run the portable build:

```powershell
cd C:\Users\User\Desktop\Repositorios\seekai
.\artifacts\SeekAI\SeekAI.exe
```

The published Windows x64 folder includes .NET; keep its files together. A portable ZIP is in `artifacts/SeekAI-win-x64.zip`. No installer or administrator access is needed.

1. In Settings, **Add folder** and wait for the index status to say Ready.
2. Press **Ctrl + Space** from any app. If that shortcut is occupied, select **Ctrl + Alt + Space** in Settings. Tray > Search always works.
3. Type a filename, phrase, or idea. Filename/text results appear first, followed by semantic results.
4. **Up / Down** selects, **Enter** opens in the Windows default app, **Esc** hides. Clicking a result also opens it.
5. Use tray > Settings / Reindex / Quit. Closing Settings keeps SeekAI running. The launcher has a taskbar entry only while visible.

The included `tests/corpus` is indexed on this machine as a demonstration. Add your own folders through Settings; the app never automatically scans your drives.

## Local AI

Start Ollama, then use **Download embedding model** in Settings, or:

```powershell
ollama pull nomic-embed-text
```

The model was installed and verified during development on this machine. See [Ollama's embedding API](https://docs.ollama.com/api/embed). SeekAI sends embedding requests exclusively to `http://127.0.0.1:11434`, bypassing HTTP proxies. Model downloads require internet access but contain no file data. Normal searches do not invoke a generative LLM. Without Ollama/model availability, filename and text search continue; Reindex fills missing embeddings when it returns.

## Build and test

Requires the .NET 10 SDK; package restore needs internet on the first build.

```powershell
cd C:\Users\User\Desktop\Repositorios\seekai
dotnet build -c Release
dotnet run --project tests/SeekAI.Tests
dotnet run --project tests/SeekAI.Tests -- --live
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

`--live` requires local Ollama and the model. Tests use isolated temporary databases and a small synthetic corpus. The 22 live-suite checks cover filename/text retrieval, semantic paraphrases, incremental changes, restart persistence, offline fallback, embedding backfill, exclusions, deletion, folder removal, chunking, PDF extraction, and settings. See `TESTING.md` for desktop verification.

## Storage and behavior

- Index/settings: `%LOCALAPPDATA%\SeekAI` (`index.db`, `settings.json`). `--data <directory>` provides an isolated profile.
- Supported: `.txt`, `.md`, `.ts`, `.tsx`, `.js`, `.jsx`, `.py`, `.cs`, `.rs`, `.json`, `.yaml`, `.yml`, `.html`, `.css`, `.sql`, `.ps1`, `.sh`, `.toml`, `.xml`, `.csv`, `.log`, text-based `.pdf`.
- SQLite stores paths, names, UTC modification ticks, sizes, overlapping 1,600-character text chunks, FTS entries, and float embeddings. Queries combine filename boosts, lexical coverage, and cosine similarity; each result shows its strongest source and a snippet.
- Startup and Reindex scan incrementally using modification time plus size. Unchanged files are not read or embedded again. Changed files replace their text and embeddings atomically at the file/text level. Interrupted embeddings resume on the next scan.
- Removing a folder purges its indexed content, except files still covered by another selected root. Deleted files disappear on the next scan. Inaccessible roots retain their index until reachable; skipped counts indicate problems.
- Ignores `node_modules`, `.git`, `build`, `dist`, `target`, `bin`, `obj`, `.venv`, `venv`, `__pycache__`, `.next`, `.cache`; linked subfolders/files; binary-looking text; files over 5 MB or 250,000 extracted characters.
- Your source files are never modified. The local database contains extracted text in plaintext under your Windows profile. Removing an index folder removes logical database entries; this is not secure erasure of SQLite free pages/backups.

## V1 limits

No filesystem watcher: use Reindex or restart after edits. No OCR; scanned/encrypted or malformed PDFs may be skipped or have no extractable content. PDF layout reading order depends on the source PDF. Filename search covers only the supported indexed file types. Semantic search scans stored vectors locally and is intended for modest personal collections, not millions of files. Its relevance is approximate and cold-model startup can be slower; text results remain usable while it loads. No automatic Windows login startup or installer/updater. Index change detection assumes applications update the modification timestamp or file size. Keep the fixed embedding model unchanged; delete the local index and restart if you manually replace its underlying model weights.

Canonical source: `C:\Users\User\Desktop\Repositorios\seekai`. The central workspace entry is a directory junction, not a duplicate repository.
