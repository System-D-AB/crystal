# 001 — Project skeleton: tasks

> The maintainer chooses which tasks to start. T1 needs open question 5 answered; T9 needs open question 1.
>
> **Progress 2026-10-09:** T2–T10 implemented and verified (build clean with warnings as errors, 9 tests pass,
> pack produces the package + symbols). **T1 deliberately not run** — the maintainer is removing content before
> the first push, so no git repository or commit exists yet. See the addendum in `requirements.md`.

- [ ] **T1** Initialize the git repository and `.gitignore`; first commit only on the maintainer's go-ahead (R2.4,
  open question 5). Done when `git status` is clean apart from the intended files.
  _Not run: git deliberately deferred at the maintainer's request. `.gitignore` is written; `git init` and the
  first commit are left for after content is removed and the repository name (OQ5) is chosen._
- [x] **T2** `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig` (R2.1–R2.4). Done when
  an empty project builds with them. _Done. SDK pinned to 10.0.100 (rollForward latestFeature); RocksDB pinned
  exactly to [11.8.1.5032]._
- [x] **T3** `SystemD.Crystal.slnx` and `src/SystemD.Crystal` with the namespaces of design § 2 (R1.1, R1.2, R1.4).
  Done when `dotnet build` succeeds with warnings as errors. _Done. Public surface is `CrystalVersion` and the
  `Encoding.KeyEncoding` anchor; `Storage.RocksStore` is the only internal RocksDB type._ *(2026-10-09: `RocksStore` removed by `002`, as its own comment planned; the smoke test now uses `RocksInstance`.)*
- [x] **T4** The two test projects (R1.3). Done when `dotnet test` runs and reports zero tests failed. _Done._
- [x] **T5** Architecture tests, each mutation-checked once (R3.1–R3.4). Done when all pass and each was seen to fail
  with a deliberate violation. _Done. Mutation checks are permanent planted-violation tests (see addendum), not a
  manual mutate-and-revert; each rule function is shown to catch its violation._
- [x] **T6** The native smoke test (R4.1). Done when it passes locally on Windows. _Done: opens RocksDB in a temp
  dir through the internal seam, writes key [00 00 00 10 01]="x", reads it back, deletes the dir. Passes on Windows._
- [ ] **T7** CI workflow on Windows and Linux (R6.1, R6.2). Done when both legs are green, and the smoke test has run
  on both (R4.2). _Workflow written (`.github/workflows/build.yml`): restore, build, test (trx artifacts), pack,
  upload. Not yet observed green — no repository exists to run it. Reopen when T1 is run._
- [x] **T8** Package metadata; `dotnet pack` produces the one `SystemD.Crystal` package with symbols (R5.1, R5.2). Done
  when the package builds and nothing is published. _Done: `0.1.0-preview.1`, MIT expression, README embedded,
  `.nupkg` + `.snupkg`, RocksDB pinned in the dependency group. Nothing published. RepositoryUrl is a placeholder
  (TODO(repo)) pending OQ5._
- [x] **T9** `README.md` and `LICENSE` (R7.1, R7.2; open question 1). Done when the README's build steps work on a
  clean clone. _Done: MIT `LICENSE`; `README.md` with working build/test/pack steps._
- [x] **T10** Record the open questions' answers in this spec's requirements, by dated addendum. _Done: addendum
  2026-10-09 in `requirements.md` records OQ1 (MIT), OQ3 (no FluentAssertions), OQ5 (git deferred), and notes._
