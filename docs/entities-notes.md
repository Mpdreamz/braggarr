# Notes on Nullean.Entities, from using it in Braggarr

Braggarr is a real-world test of Nullean.Entities 0.1.1 and its CsCheck integration. This file collects what was awkward,
surprising or good, to bring back to the library. Each problem is filed on [nullean/entities](https://github.com/nullean/entities/issues),
named at the end of its entry. The issues use generic examples, because that repo is public and this one is not.

## Friction

- **`EventAutoTest` has no `AsDataSource()`.** `AutoTest` gives one test per axis through `AsDataSource()`; the event version
  only has `RunAsync()`, so a service's temporal rules run as one test and a failure names the axis only in the message.
  (`tests/Braggarr.Services.Tests/SchedulingServiceTests.cs`) Since then: `EventAutoTest.Cases` gives one case per axis,
  which Braggarr now uses; `AsDataSource()` would only be consistency. #43
- **One diagram per kind of entity does not fit `AsConstraint` or `ConformTo`.** A delivery to Bluesky and one to Reddit
  follow different lifecycles. `ConformTo` fails when any trigger has no binding, so one combined diagram cannot conform to
  either kind; and `AsConstraint` takes a single machine. Braggarr declares two machines and writes the rule by hand with
  `Transitions((before, after) => ...)`, picking the machine from the entity. A `AsConstraint(entity => machine, selector)`
  overload would remove that. (`src/Braggarr.Domain/DeliveryRules.cs`) #41
- **A hand-written transition rule is never attacked, and the tests stay green.** `CheckTransition` rules are listed under
  "Unclassified" in the report, but `AsDataSource()` yields no case for them, so one test per axis passes while the rule is only
  checked, never exercised. A planted bug ("retry after three attempts") went unnoticed by the axes until the rule was restated
  as `Monotonic` over a count. An unclassified rule could fail the vacancy axis, or at least appear as a case.
  (`src/Braggarr.Domain/ScheduleRules.cs`) #47
- **`ConformTo` cannot see a guard on an edge.** The delivery diagram allows retry; the record used to refuse it after three
  attempts. A random walk long enough to reach the fourth attempt diverged, on a seed the earlier runs had not drawn. The fix
  that made sense was in the domain (the retry budget is the schedule's policy, not the lifecycle's), but a guard or a depth
  bound on a declared edge would let a diagram say what the record does. #42
- **Field order in a `[Constraints]` class matters.** A private helper (`Actor Owner`) declared below the rules that use it is
  still null when they initialise. The nullable warning caught it, but only because the argument was non-nullable. #48
- **A counter-example for a rule guarded by state cannot be applied to every base, and nothing says it may decline.** "An
  approved campaign covers every point" needs an approved campaign to break. The generated bases include rejected ones, and
  no legal transition leads from rejected to approved, so a counter-example that sets `Approved` also breaks the lifecycle
  rule, and the library refused it. What works is returning the base unchanged when it cannot be broken: the library skips a
  base the counter-example leaves valid. That is only visible in the source (`Laws.cs`), not in the `CounterExample` docs. A
  documented "not applicable" return, or a filter on the bases, would say it plainly. (`tests/Braggarr.Domain.Tests/CampaignRulesTests.cs`) #38
- **The README still says the packages are not published**, while 0.1.1 is on nuget.org (checked 2026-10-28). #44

## From the rigor review (2026-10-28)

Pushed every set as far as the library goes:
- `Deeper()` proofs with declared faults;
- counter-examples that break one rule each;
- a check that every operation matters.

What that showed about the library:

- **`Faults()` with several starts reports a fault as uncaught when it can only happen from another start.** It runs every
  declared fault from each start and throws on the first. A fault for a manual delivery never fires from an automatic one, so
  it reads as "no requirement detects it". Workaround: one spec per start (`DeliveryRulesTests`, `CampaignRulesTests`).
  Counting a fault as caught when any start catches it would fix it. #35
- **The automatic "X has no effect" faults are only reported, and nothing counts as catching them**, so every operation of
  Delivery and Campaign showed "caught by NOTHING". Yet `Exhaustive` catches exactly that bug through coverage: a do-nothing
  `Begin` leaves the `Pending -> Sending` edge untaken, and the proof refuses to pass vacuously. `tests/.../NoOps.cs` replaces
  each operation with one that returns its input and expects the proof to fail. It also runs a control first, so it cannot
  pass for the wrong reason. `Faults()` could count an `Allow` edge or a `When` guard that goes unexercised as a catch. #36
- **A counter-example found by search may break other rules too, and Vacancy still passes.** It reports "(also breaks
  lifecycle, …)" and moves on, so a rule can look reachable only through a value that breaks three. Strictness here meant
  writing every one by hand, until the counter-example report showed only single-cause lines. A
  `RequireSingleCause()` option would make that enforceable rather than a habit. #37
- **The counter-example report says "NONE FOUND, write one" for `Idempotent` rules**, which cannot have one while the
  code is right. Mutation is what shows such a rule has teeth. The report could say so instead. #39
- **`Probe()` over a valid generator says every rule is "never violated".** That is correct, but it reads like a list of
  tautologies. It is meant for raw generators; the docs could say so where `Probe` is introduced. #44
- **Weak sampling passes quietly.** "Moved a value in 4 of 200 runs" for a monotonic rule is a pass. Only a richer generator
  (stories of real delivery histories next to random walks) raised it. A minimum, or a warning under some share, would
  surface a generator that barely exercises a rule. #40
- **A failure can appear once in many runs.** The seed fixes only the first iteration, and the rest are drawn fresh, so a
  rule broken by a rare value (`OpenManual(0)`, an issue number GitHub never gives) failed once and passed the next 18 times.
  The shrunk case it printed was exact, and the fix was a guard in the domain. Worth saying in the docs: rerunning until it
  passes hides a bug. #44 for the docs, #49 for a fully repeatable mode.
- **`UseFileSystem()` worlds put their files under their own root (`/docs`).** Every counter-example found for a rule
  over a model with its own root therefore also breaks `StayInside`. The single-cause ones had to be written by hand, adding
  a file inside the value's own world. #45
- **A `Gen.Const` world is shared, and mutable.** A file-system counter-example changes the `MockFileSystem` of the one base
  value. Laws apply it more than once, and Invariant and Vacancy run at the same time, so adding a link raced ("the file
  already exists"). Changing the base's own file in place made other checks fail now and then. The fix was a helper that adds
  each file once, under a lock, and never edits what is there. Cloning the world per counter-example would remove the trap. #46
- **What was good:**
  - The Idempotency axis refuses a rule whose action changes no generated value ("passed without proving anything"). That
    caught a meaningless "syncing twice is syncing once".
  - Two diagrams' edges as one `Transitions(selector).Allow(...)` list, with invariants keeping each kind of delivery in its own
    states, turned the hand-written, unclassified lifecycle into a classified one. Its edges are now attacked and must all be
    taken.

## From the blog (2026-10-30)

Not filed yet.

- **A state invariant that a transition rule already implies cannot get a counter-example.** The delivery lifecycle became one
  `Transitions((kind, state))` allow-list, so no kind's edge is another's (a blog post may go from sending back to waiting; a
  Bluesky post never). The invariants "only Reddit and Hacker News wait in an issue", "they are never sent" and "only the blog
  commits" then had only counter-examples that are also illegal transitions from the value they derive from, so Invariant and
  Vacancy refused them as two-cause. They were dropped, the lifecycle says the same for every reachable state. A counter-example
  given as a value, not derived from a generated one, would have kept them for data read from disk.
  (`src/Braggarr.Domain/DeliveryRules.cs`)

- **The packages ship no XML docs.** Overloads like `NoUnknownKeys(Func<T, IEnumerable<string>>, string, string)` cannot be
  read from IntelliSense; finding what the two strings are took reflection. (`src/Braggarr.Blog/SiteConfig.cs` uses a plain
  `Always` instead.)
- **`Sorted` has no descending order, and its third positional argument is `bool strict`.** Newest-first needs a reversed
  comparer, and the one at hand is the code's own: a rule built on it agrees with whatever order the code has. A planted bug
  (oldest first) passed every axis until the rule was restated over the dates. A `descending:` flag would make the honest
  version the easy one. (`src/Braggarr.Blog/Site.cs`, `PublishedIsNewestFirst`)
- **`OrderIndependent` compares whole results with `Equals`**, so a result that carries the reordered input (a plan that holds
  its site) is never equal. The projection has to drop it. Worth a line in the docs.
- **What was good:** the Confluence axis caught a missing tie-break in the order of posts on the same moment, once the
  generator produced such posts, and Vacancy caught rules made toothless on purpose.

## What worked well

- **A counter-example must break one rule only.** The first counter-example for "images have alt text" also broke "approved
  campaigns are complete", and the library refused it, saying which other rule it broke. That showed a real coupling in the
  domain: image checks were folded into text completeness. Splitting them (`IsComplete` for text, `IsReady` for both) made each
  rule stand on its own.

- **Vacancy found three real gaps in the first run:** a generator producing approved campaigns that were not valid, rules
  with no counter-example, and a monotonic rule no action ever moved. Each message said what to do next.
- **`NeverTwice<T>(key)` is exactly "never post twice"**, and `EntitySpec.EventSourced` plus `Always<Approved>` proved
  "only the owner approves" over every command sequence without a hand-written scenario.
- **Planted bugs were caught by the generated axes**, not only by the hand-written tests: resetting attempts on retry, a sync
  dropping a published delivery, a restart resending, a timeout treated as a refusal.
