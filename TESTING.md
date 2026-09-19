# Verification

Verified on Windows on 2026-09-19.

- `dotnet build`: success, zero warnings/errors.
- `dotnet run --project tests/SeekAI.Tests -- --live`: 22 checks passed using real local nomic-embed-text embeddings.
- Both session/context paraphrases ranked `session-notes.md` first with Semantic as the primary match. An unrelated cooking paraphrase ranked `dinner.txt` first.
- Real embedding restart scan: 4 unchanged files, 0 newly embedded chunks.
- Native desktop verification using Windows computer-use: selected `tests/corpus` with the folder picker, indexed 4 files/4 chunks, saw the correct semantic result and snippet, moved selection with Down, and pressed Enter. Windows opened `forja-release.txt` in its associated VS Code editor.
- Ctrl+Space while a different application was active opened SeekAI and focused its search field. Esc hid the launcher.

Tests use their own temporary directories and print the location for inspection. Offline tests inject an unavailable embedding provider instead of stopping the user's Ollama service. No user documents are included in the test corpus or published repository.
