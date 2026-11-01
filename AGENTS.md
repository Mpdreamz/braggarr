# Braggarr

A self-hosted *arr that posts campaigns about the owner's projects to social networks, after the owner reviewed them as pull
requests. `docs/setup.md`, `docs/posts-format.md` and `docs/api.md` describe it from the outside.

## Layout

| Project | What | May depend on |
|---|---|---|
| `src/Braggarr.Domain` | Immutable records (campaign, post, variant, delivery, schedule), their methods, length rules, and the rules as Nullean.Entities constraint sets | Nullean.Entities only |
| `src/Braggarr.Services` | Scheduling and campaign services over the domain, through the ports in `Ports.cs` | the domain, Microsoft.Extensions abstractions |
| `src/Braggarr.Adapters` | The ports' implementations: the posts repo (files, git, GitHub API), Bluesky, Mastodon, ntfy, the secret store | |
| `src/Braggarr.Web` | The server, as a library: the owner's pages (RazorSlices in `Ui/`), `/api/v1` and MCP, setup and sign-in, the scheduler's loop. AOT-safe | |
| `src/Braggarr.Blog` | A blog site: its config, posts and pages, their rules, and the built-in RazorSlices theme that renders them to static files. AOT-safe | Nullean.Entities, Markdig, SharpYaml, RazorSlices; nothing of ours |
| `src/Braggarr.Cli` | `braggarr`, the one tool: the server (`serve`, `healthcheck`) and the blog (`blog init`, `new`, `build`, `serve`), published as Native AOT | the blog, the server |

Tests in `tests/` mirror the first three; `Braggarr.Web.Tests` also covers the adapters, `Braggarr.Blog.Tests` the blog and
`Braggarr.Cli.Tests` the CLI. Dependency tests keep the first two rows and the blog's true.

## Rules of the code

- **Decisions live in the domain.** Who may approve, what a delivery may become, what is due: a method on a record, guarded,
  returning the next value or the same one. Services ask the domain; pages and the API ask services.
- **A rule is a constraint.** Add it to the `[Constraints]` class next to the type, and the AutoTest runs every axis it implies.
  A rule the generated data never reaches fails as vacant: fix the generator or add a counter-example.
  - **Counter-examples break one rule.** Write one by hand when the counter-example report shows a found one that "also
    breaks" another. A base it cannot break that way is returned unchanged, and is skipped.
  - **A lifecycle is a declared diagram.** Use `StateMachine` plus `AsConstraint`, or `Transitions(...).Allow(...)` built from
    one, never a hand-written `(before, after)` rule. That way it is attacked by the axes and every edge must be taken.
  - **Each lifecycle type is proved, not only sampled.** `Deeper()` closes its state space. Each start gets its own spec,
    with the bugs it could have declared as `Fault`s that must be caught. `NoOps.GoesUnnoticed` checks that every operation
    matters.
  - **An intermittent axis failure is a real value.** The seed fixes the first iteration only, so read the shrunk case it
    prints, not the run that happened to pass.
- **Approval is of what a campaign says.** `Approval` holds the `Digest` of the content approved. `IsApproved` and so
  `MayGoOut` hold only while it matches, so an edit stops everything that has not gone out until someone approves again.
  The owner or the judge approves (`Actor.MayApprove`); an agent never does, and the event-sourced proof says so.
- **The judge is a gate and, when let, an approver.** `JudgingService` writes a `Judgement` (fingerprints of the campaign and
  of the criteria, one `Finding` per criterion, post and network) and `CampaignService` refuses to approve without a current
  one, past a blocking failure, or, for the judge, past any failure. The criteria come from `main` only, `Blocks` comes from
  the criterion and never from the model, and a finding the model left out is a failure.
- **A brief is the owner's word.** `projects/<slug>.md` goes into drafting's and the judge's instructions, not their data, so
  `BriefService.SaveAsync` refuses anyone but the owner and nothing in the API writes one.
- **Never post twice.** Record `sending` durably before a network call. A publisher throws `PublishException` only when the post
  is certainly not up (or a retry is idempotent); anything else marks the delivery unknown for the owner.
- **The scheduler's promises are checked twice.** `SchedulerRules` runs over random histories against in-memory fakes (200
  per axis) and against the real `GitCampaignRepository` on real git (`GitSchedulerWorld`, 30 per axis). A crash there throws
  away the repository instance too, so recovery uses only what git committed. Add a rule to `SchedulerRules`, not to one run.
- **Time comes from `TimeProvider`.** Every read of the clock, every wait (`Task.Delay(…, time, ct)`) and every timer (`PeriodicTimer(…, time)`)
  takes it; the console's log timestamp is the one exception. Domain methods take time as a value. Tests run on
  `ManualTimeProvider`. The web app under test runs in 2030, so a real-clock read shows up as wrong. Publisher tests use
  `InstantTime`, which fires at once and records each wait.
- **No database.** Content and the delivery log are git; `/config` holds settings and encrypted secrets.
- **The compiler holds the disk and the clock.** `src/BannedSymbols.txt` (BannedApiAnalyzers) bans `File`, `Directory`,
  `FileInfo`, `DirectoryInfo`, `FileStream`, the `Now`/`UtcNow` reads, `Task.Delay` and `Thread.Sleep` without a
  `TimeProvider`, and `PeriodicTimer` without one. The single exception is the console's log timestamp, suppressed in place
  with its reason; add another only the same way.
- **Disk access goes through `IFileSystem`.** Use the one from `ConfigFileSystem.Create`: a `ScopedFileSystem` rooted at
  `/config` that refuses paths outside it, symbolic links and hidden names (`.git` aside). Never call `File` or `Directory`
  directly. Paths a post or the owner supplies are also held to `CampaignFolderRules` or `VideoLibraryRules`, so a bad one is
  refused by a named rule before the scope throws. Tests use `MockFileSystem`, or the real disk scoped to a temporary folder
  where git has to run.
- **The UI is plain HTML and CSS, from RazorSlices.** JavaScript only adds to a page that already works without it. A page in
  `Ui/` is a model deriving from `UiPage` (its handlers, picked in `Handle`) and a slice; `UiPages` maps it. There are no tag
  helpers: `Forms` writes the anti-forgery token, handler addresses and versioned assets, and `tests/Braggarr.Web.Tests/pages`
  holds what every page answers.
- **Test the tests.** Plant the bug a test is for and watch it fail; then rebuild from clean (`rm -rf .artifacts`): a restored
  file can be older than the build and be skipped.

## Build

`./build.sh test` builds and runs every test project. The CLI must publish as Native AOT without a warning:
`dotnet publish src/Braggarr.Cli -c Release -r osx-arm64` (or your platform). `./build.sh run` starts the server, `braggarr serve`
(set `BRAGGARR_CONFIG` to a scratch directory). Warnings are errors.

Nullean.Entities friction found here goes into `docs/entities-notes.md`.
