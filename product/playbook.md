# Playbook

How work is done on SystemD.Crystal. **Every contributor follows it, human or coding agent.** The specs, the
engineering documents and this playbook are the shared memory between contributors; nothing important lives only in a
conversation.

## 1. No code without a spec

Every unit of work, whether a feature, a fix with design impact or a refactoring, starts as a **numbered spec**:

```
product/specs/{NNN}-{spec-name}/
├── requirements.md
├── design.md
└── tasks.md
```

- `NNN` is the next zero-padded number (`001`, `002`, …). Look at `product/specs/` for the last one.
- `spec-name` is a short kebab-case description (`001-project-skeleton`, `002-box-catalogue`).
- **One spec, one concern.** Cross-reference other specs where they depend on each other.
- **A spec always has all three documents,** written in this order: requirements, then design, then tasks.

### requirements.md

What is being built, and how we will know it is right.

```markdown
# NNN — Title

> **Status:** drafted | approved | implemented (date). **Depends on:** other specs, if any.

## Introduction
What this spec delivers and why.

## Requirements

### R1 — Title
- **R1.1** A specific, testable statement.
- **R1.2** …

## Out of scope
## Open questions
```

Requirements are **testable**: each one can be shown met or not met.

### design.md

How it will be built: structure, public API, data and key layouts, flows, error handling, dependencies, risks, and
**what the design deliberately does not do**. A design that changes the physical layout says how
[`../engineering/box-layout.md`](../engineering/box-layout.md) changes.

### Reuse before writing

Crystal's foundation already exists, proven in production, in the products built on it (Highway and StowDB, both MIT
and owned by the same company). **When a spec's mechanism exists there, its design names the source files to copy and
what to change, and the implementation copies and adapts them** rather than writing anew. Copied files begin with a
comment naming their origin, and their tests come with them.

### tasks.md

Ordered, small tasks, each naming the requirements it fulfils and its done criterion, with a checkbox:

```markdown
- [ ] **T1** Create the solution file (R1.1). Done when `dotnet build` succeeds.
- [x] **T2** … (completed)
```

When a task is done differently from what was written, say so beside it; do not rewrite it silently.

## 2. Order of work

1. **Spec first.** Write the three documents.
2. **Ask before building.** The maintainer approves the spec and **chooses which tasks to implement**. Do not
   implement all tasks at once unless told to.
3. **Build** the chosen tasks, tests first where they can be.
4. **Mark tasks `- [x]`** as they are done.
5. **Update the documents in the same spec.** A spec that changes how Crystal works updates the matching
   `engineering/` document and, where the product's description changes, `product/intro.md`. The documents describe
   Crystal **as it is now**.

## 3. Records are amended, never rewritten

- **A decision or analysis that turns out wrong is corrected by a dated addendum** that says what changed and why.
  The original stays readable. The history of *why* is worth more than a tidy page.
- **A gap between the code and a document is either a defect or a new decision, never a silent difference.**
- **"Proposal" and "decision" are different words.** A text marked *proposed* binds nobody until the maintainer
  approves it.

## 4. Verification

Crystal is a storage kernel: a defect loses or corrupts data for every product built on it. **Nothing is committed
until it is proven correct by more than one source.**

- **Tests:** unit tests for each package; integration tests against a real RocksDB in a temporary directory, with no
  external infrastructure.
- **Mutation checks** for anything that guards correctness: break the guard on purpose and confirm a test fails. A
  test that passes with its guard removed protects nothing. After restoring a mutated file, make sure the build is
  really rebuilt before trusting a result.
- **Concurrency is tested at the exact interleaving,** with hooks, rather than hoped for with sleeps. Run concurrency
  and failover tests under a starved thread pool, too.
- **A failure that appears only under load is a finding, not noise.** Root-cause it; never just re-run it.
- **Crash safety is tested by killing the process,** not by assuming the write-ahead log works.
- **An independent review** by a second reviewer (another contributor or a fresh agent) for any change to the
  layout, the catalogue, batches, durability, replication or backup.
- **A real run:** build the packages and use them from a separate program before calling a feature done.
- **Numbers come from the benchmark harness only,** with every result checked for correctness and the durability
  mode stated. No performance claim without a reproducible number.

## 5. Commits, branches, releases

- **Never commit, push, tag or publish without the maintainer's explicit go-ahead.** Approval for one action does not
  extend to the next.
- Work on a branch. Commit messages say what changed and why.
- **Publishing packages is outward-facing and irreversible:** a published version can only be unlisted. It always
  needs explicit approval.

## 6. This is a public repository

Never commit or write into any document:
- IP addresses or host names;
- names of companies, customers or deployments;
- incident reports or production traces;
- credentials, certificates or keys;
- paths to anyone's private notes.

Examples use loopback addresses and made-up names. A document that needs a private decision states the fact itself.

## 7. Coding standards

- **.NET 10, C# 14,** nullable reference types on, file-scoped namespaces, warnings as errors.
- **Namespaces and packages** start with `SystemD.Crystal`.
- **RocksDB is touched only by internal types** in the `SystemD.Crystal.Storage` namespace. **No RocksDB type appears
  on any public signature.** Architecture tests enforce it.
- **Crystal references no product.** Nothing in Crystal knows about documents, queries or queues.
- `sealed` by default; records for immutable data; `readonly struct` for small hot-path values.
- Every async API takes a `CancellationToken`. **Say whether an async method yields a thread:** RocksDB calls are
  synchronous native work, so most of Crystal's async methods complete inline, and that must be documented, not
  implied.
- Errors that a caller must handle (a refused format, a gap, a fenced primary) are **typed exceptions with the way
  forward in the message**.
- Logging only through `Microsoft.Extensions.Logging` abstractions; metrics through `System.Diagnostics.Metrics`.
  Crystal never chooses a logging or metrics framework for the application.
- Testing: xUnit with its own assertions; no assertion or mocking library (FluentAssertions 8 is commercially licensed). A spec that needs a mock decides it.
- Test names: `Method_Scenario_ExpectedResult`.

## 8. Agents

Every coding agent follows this playbook. Agent-specific notes:

- **`AGENTS.md`** at the repository root is the one agent file the repository holds; it points here. A tool's own
  instruction or steering files are kept on the contributor's machine and are not published (`.gitignore` lists them);
  they point to `AGENTS.md` or here. A tool whose spec format is requirements, design and tasks maps directly onto § 1.
- **Agents' working files** (reviews, notes, status) are not published either. What matters from them is recorded
  in the spec's `tasks.md`.
- **Agents ask, they do not assume.** When a decision belongs to the maintainer (scope, a public API, a format change,
  anything outward-facing), the agent asks rather than picking for them.
- **Agents report faithfully.** A failing test is reported with its output; a skipped step is said to be skipped; a
  claim of "done" means built, tested and verified.
- **Agents leave a trail.** The spec's `tasks.md` records what was done, what was done differently and what is left, so
  the next contributor, human or agent, can continue without the conversation that produced it.
