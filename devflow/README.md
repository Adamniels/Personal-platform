# devflow

Dev flow is the platform's feature for automating development on connected repositories: the owner
plans, agents build, and he follows and answers all of it from its pages. What it is and how it is
meant to grow is in `docs/devflow-architecture.md`, and its scope and slices are in its feature
issue, #268. "Dev flow" is its name in the nav and in headings, and `devflow` everywhere technical.

It is a C# feature on .NET 10, an ASP.NET Core minimal API app, built the way `review/` is. Today it
is the service, its page, its database, which holds the vault, the code host's interface with its
fake and its GitHub adapter, which reads as the app and can write as it with a key narrowed to one
purpose, linking a user's GitHub account, so Dev
flow holds a token that acts as them, connecting a repository, which makes it a project, and
reading every project about once a minute into a stored list of what waits for the owner, which
the API answers and the platform's front page counts, and each project's worker key, with the
worker API a worker calls with it, which hands out a project's cards as jobs with a brief and
records what comes back, the pull request it opens as the app for a card that built and passed its
check, the review a fresh reviewer agent makes of it, kept for its commit and posted on the pull
request, the fix rounds a `changes` verdict or the owner's comment sends it to, after which it
waits for the owner on "Waiting for you", each card's own page, and the worker
program that calls it: everything else the tabs will show arrives with later slices.

## What it serves

- `GET /devflow/api/health`, `{"status": "ok"}`, without a token. It answers whenever the process
  serves, and says nothing about what Dev flow depends on.
- `GET /devflow/api/whoami`, `{"account_id": "<sub>"}`, the account the request's token names.
  Without a valid token it answers `401` with an empty body.
- `GET /devflow/api/status`, `{"code_host": "fake", "github": {"state": "none", "login": null}}`:
  which code host Dev flow talks to, `fake` or `github`, see "The code host", and the caller's
  GitHub link, its `state` `none`, `linked` or `needs_relink`, with the `login` it was linked as,
  null with `none`, see "Linking GitHub". The page reads it to show its banner and the link.
  Without a valid token, `401` with an empty body.
- `POST /devflow/api/github/link`, `{"authorize_url": "..."}`, starts linking the caller's GitHub
  account, and `DELETE /devflow/api/github/link`, `204`, unlinks it. Without a valid token, `401`
  with an empty body.
- `GET /devflow/api/github/repositories`, what the caller can connect, and
  `GET /devflow/api/projects`, `POST /devflow/api/projects` and
  `DELETE /devflow/api/projects/{id}`, their projects, see "Connecting a repository". Without a
  valid token, `401` with an empty body.
- `GET /devflow/api/waiting`, what waits for the caller as last read, and
  `POST /devflow/api/projects/{id}/refresh`, which reads one of their projects now, see "What
  waits". Without a valid token, `401` with an empty body.
- `GET /devflow/api/waiting/{id}/screenshots/{name}`, one of the screenshots a waiting item's pull
  request shows, as a PNG read from GitHub as the app, see "What waits". Without a valid token,
  `401` with an empty body.
- `POST /devflow/api/waiting/{id}/merge` and `POST /devflow/api/waiting/{id}/send-back`, which
  merge a waiting pull request and send one back with a comment, as the caller on GitHub, see
  "Acting from the page". Without a valid token, `401` with an empty body.
- `POST /devflow/api/projects/{id}/resume`, which resumes a project stopped because something
  landed on a branch it watches that Dev flow did not put there, see "Watching branches".
  Without a valid token, `401` with an empty body.
- `GET /devflow/api/projects/{id}/settings` and `PUT /devflow/api/projects/{id}/settings`, a
  project's settings, and `GET /devflow/api/projects/{id}/cards`, its cards, see "Cards". Without a
  valid token, `401` with an empty body.
- `GET /devflow/api/projects/{id}/cards/{number}`, a card's whole story so far, see "A card's
  page", `GET /devflow/api/projects/{id}/cards/{number}/screenshots/{name}`, one of the pictures
  the worker took of its head commit, as a PNG read from GitHub as the app, see "Screenshots", and
  `POST /devflow/api/projects/{id}/cards/{number}/merge`, which merges a card's pull
  request that waits, as the caller on GitHub, see "Acting from the page". Without a valid token,
  `401` with an empty body.
- `GET /devflow/api/projects/{id}/features/{number}`, a feature's whole story so far, and
  `POST /devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/fine`, which
  answers a decision of one of its cards "Fine as it is", in Dev flow alone, see "A feature's
  page"; and `GET` and `POST /devflow/api/projects/{id}/features/{number}/merge`, what merging the
  feature confirms and the merge itself, into the default branch as the caller on GitHub, see
  "Acting from the page" and "Merging a feature". Without a valid token, `401` with an empty body.
- `POST /devflow/api/projects/{id}/worker-key`, which makes a project's worker key and answers it
  once, and `GET /devflow/api/projects/{id}/workers`, the workers that have said hello with it,
  see "Workers". Without a valid token, `401` with an empty body.
- `GET /devflow/api/claude-login`, `PUT /devflow/api/claude-login` and
  `DELETE /devflow/api/claude-login`, whether the caller's Claude login is set and when it is due,
  giving or replacing it, and removing it, see "Claude login". None ever answers the token. Without
  a valid token, `401` with an empty body.
- `POST /devflow/api/worker/hello` and `POST /devflow/api/worker/jobs/next`, the worker API, which
  takes a project's worker key in `Authorization: Bearer dfw_…` and never a user's token, see
  "Workers", and a running job's `POST /devflow/api/worker/jobs/{id}/push-key`, `…/progress` and
  `…/result`, see "Jobs". Without a key the project has now, `401` with an empty body.
- `GET /devflow/api/display`, what the platform's front page shows of Dev flow,
  `{"waiting": 2, "stopped": 1, "projects": 1, "stale": false, "needs_relink": false, "claude_login": "ok"}`:
  the items of kind `waiting` and of kind `stopped` that are not sent back, `stopped` with the
  caller's stopped projects added, see "Watching branches", how many projects the
  caller has connected, whether any of them is stale or failing, whether their GitHub link has
  expired, and where their Claude login stands, `none`, `ok`, `due_soon` or `expired`, as
  `GET /devflow/api/claude-login` says it, which the core says apart as it does the link.
  It reads the same stored picture as the waiting list, never GitHub, so it answers well inside the
  core's deadline, and it sends no words or order meant for display. The core calls it with the
  user's token in the `Authorization` header; `contracts/display/devflow.json` pins its shape, see
  "What a feature exposes for the front page" in `Core/docs/core-architecture.md`. Without a valid
  token, `401` with an empty body.
- `GET /devflow/api/attention`, `{"attention": true}` or `{"attention": false}`, Dev flow's answer
  to the nav bar, which draws the dot on Dev flow's link while it is true: true when the caller has
  an item, waiting or stopped, that is not sent back, a stopped project, or a GitHub link that has
  expired, which is
  what the waiting list's `count` counts, or a Claude login `due_soon` or `expired`, which it does
  not, since the login is not on that tab. It reads the same stored picture and the login, never
  GitHub. Both
  registries name it as Dev flow's `attention`; the contract every feature's answers to is "The
  attention endpoint" in `High-level/docs/feature-contract.md`. Without a valid token, `401` with an
  empty body.
- `GET /devflow/github/callback`, where the code host sends the browser back, answering `303` to
  `/devflow/projects`, or to `/devflow/projects?link=failed` when it did not link. Without a valid
  token, `303` to the refresh endpoint as a page answers, see "Linking GitHub".
- In the fake mode only, the fake's addresses under `/devflow/api/fake/`, see "Staging a world",
  its sign in page, `/devflow/fake-github/authorize`, and its install page,
  `/devflow/fake-github/install`, see "The fake".
- `GET /devflow`, `/devflow/projects`, `/devflow/projects/{id}`, a project's page, and
  `/devflow/projects/{id}/cards/{number}`, a card's page, and
  `/devflow/projects/{id}/features/{number}`, a feature's page, with any query, the page:
  `web/dist/index.html`, revalidated on every load; `{id}` must be a UUID and `{number}` a number. Without a valid token each answers `303` to
  `/auth/refresh?return_to=<the path and query as received, URL encoded>`.
- `GET /devflow/assets/*`, the page's built files from `web/dist/assets/`, without a token, as
  review and the brain serve their own: they are the same for everyone and hold no data.

Everything else is `404`, and any request that changes state from another origin is `403`, before
routing, but a worker route's, see "How Dev flow authenticates a request" below. An error the service did not plan for is
an RFC 9457 problem document, `application/problem+json` with `status`, `title` and `detail`,
through ASP.NET Core's `AddProblemDetails`, as review's are.

The routes under `/devflow/api/` are pinned by `contracts/devflow/openapi.json`, see "The schema"
below.

## The pages

`web/`, a frontend like any other, a `web` folder under its service, so CI and the agents gate and
build it with the rest, see `design/ui/README.md`. It is review's stack at its versions,
`review/web/package.json`, React Query among them, and depends on the components as
`"@platform/ui": "file:../../design/ui"`. Its `index.html`
includes the nav's lines from "Navigation" in `High-level/docs/platform-architecture.md`, with
`<platform-nav current="devflow">`, and its content sits in `<main class="page">`.

The page is an `h1`, "Dev flow", a line under it, "What your agents built, and what waits for
you.", then the package's `Tabs`, one per section, each with its own address:

| Section         | Address             |
| --------------- | ------------------- |
| Waiting for you | `/devflow`          |
| Projects        | `/devflow/projects` |

The page names its tab in the top bar's crumb, "Dev flow / " and the tab's name, by setting the
`crumb` attribute of its `<platform-nav>` whenever the tab changes.

Above the heading, in the fake mode, a strip says "Fake GitHub. Nothing here is real.", in the
attention style of "Needs you banner" in `design/DESIGN.md`, tokens only, and is not dismissible. It
is `src/banner.tsx`, the page's own component, since no other feature needs it, and it reads
`GET /devflow/api/status` once as the page loads; before the answer, with `github`, or when the
status cannot be read, it shows nothing.

The Projects tab shows the GitHub link above its list, `src/projects-link.tsx`, as the package's
`Card` titled "GitHub": with nothing linked, "Link your GitHub account to connect repositories and
to merge and comment as yourself." and a primary "Link GitHub", which posts to
`/devflow/api/github/link` and sends the browser to the address it answers; linked, "Linked as
@<login>" and a ghost "Unlink" behind the package's `Dialog`, "Unlink GitHub?"; and with
`needs_relink`, the card's attention style, "Your GitHub link has expired." and "Link again". At
`?link=failed`, or when starting fails, it says "GitHub was not linked. Try again." as an alert.
Before the status answers it shows nothing.

Under the link is the Claude login, `src/claude-login.tsx`, the package's `Card` titled "Claude
login", read from `GET /devflow/api/claude-login` as the tab loads: "Agents run on your
subscription with this. Make it with `claude setup-token`.", then where it stands, "Not set yet.",
or "Set on <date>, renew by <date>.", the dates in the reader's own zone. From a month before it is
due the card takes the attention style and says "Set on <date>. Renew it by <date>: agents cannot
run once it runs out.", and once past it "Set on <date>. It ran out on <date>: agents cannot run
until you replace it.", in `--accent-text` at weight 600. Under that, a password field labelled
"Claude token" and a secondary "Save", or "Replace" once one is set, enabled only while the field
holds something, and, once set, a ghost "Remove" behind a destructive `Dialog`, "Remove the Claude
login?". Saving empties the field whatever the answer; a token the service refuses says "That is
not a setup token. Make one with claude setup-token and paste all of it." on the field, and any
other failure says so as an alert. The page never reads the token back, not even a part of it.
The buttons are secondary, since the tab's one primary is "Link GitHub" or "Connect a repository".

Under it are the projects, `src/projects.tsx`. A primary "Connect a repository", enabled only
while GitHub is linked, opens "Choose a repository": the repositories the app reaches for the
owner, read from `GET /devflow/api/github/repositories`, as one package `Table` per account, named
and headed by it, each repository a row with "Connect", or a `Badge` "Connected" when it is a
project already; with the app installed nowhere he can reach, "The GitHub App is not installed on
any repository you can reach." with a link "Install it on GitHub" to the answer's `install_url`.
Connecting closes it and reads the projects again. The projects are a `Table` named "Projects",
each row "<owner>/<name>" over "Read the way this repository's runner writes." with a ghost
"Disconnect" behind a destructive `Dialog`, "Disconnect <owner>/<name>?", which says Dev flow
forgets the project and everything it read for it and that nothing changes on GitHub; with none,
and the choice closed, the `EmptyState` "No project connected yet". A read, a connect or a
disconnect that fails says so as an alert. Each row's "<owner>/<name>" is a link to the project's
page.

A project's page, `src/project.tsx`, is at `/devflow/projects/<id>`, under the Projects tab, which
stays selected there. Its header is a link "Back to Projects" over the repository's
"<owner>/<name>" as a section heading, with beside it, once the settings are read, a `Badge`
naming the preset they match, "Fully automatic", "Guided", "Only I merge" or "Custom", see
"Presets" under "Settings": the settings as last read or saved, never the form while it is edited,
the badge going under the name where a long name leaves no room. A project the caller does not have is the `EmptyState`
"There is no such project". Under the header is the Settings panel, then the Cards panel, see
"Cards", then the Workers panel, the package's `Card` titled "Workers",
read from `GET /devflow/api/projects/{id}/workers` as the page loads: a line saying a worker holds
the project's key and asks Dev flow for work with it, then the key, then the region "Start a
worker", then the workers. With no key,
"It has no key yet." and a primary "Make a key". Making one shows it once, in the region "The new
key", on an inset in the mono font, with "Shown once. Copy it now: Dev flow keeps only a hash of it
and cannot show it again." and a secondary "Copy the key", which copies it and then reads
"Copied"; a copy that fails says so as an alert. The page never reads the key back: after a
reload it says "Its key was made <date and time>.", and the button is a secondary "Make a new
key", which opens a destructive `Dialog`, "Make a new key?", saying the old key stops working at
once and every worker holding it is refused until it is given the new one, and makes nothing until
it is confirmed. The workers are the list "Workers", each an inset row, by when it first said
hello: its name, "Recipe <version> on <base image>" in the mono font, and "Last seen <time> ago",
or, after ten minutes without a word, "Not seen since <date and time>", in `--text` at weight 600;
with none, "No worker has said hello yet."

The region "Start a worker" says to paste the commands in a terminal on the machine that runs the
workers, and that running the second again replaces the worker. Right after "Make a key" it holds
the region "Save the key", the command that saves it, with it; always, the region "Build and start
the worker", the command from `start_command`; each in the mono font on an inset that wraps, with a
secondary "Copy the save command" or "Copy the start command" that reads "Copied" once it has,
and an alert when copying fails, see "Starting a worker". A worker that needs a rebuild shows, under
its name, the `needs-you` status "Rebuild" and, in `--text` at weight 600, "<repository>'s recipe
has changed: run the command again" or "A new base image was released: run the command again".
When the list's `held` says the project hands out no work, the panel shows, under its first line,
the `needs-you` status "No work" and, the same way, "No work is handed out until you give your
Claude login on the Projects tab", or for `claude_login_expired` "No work is handed out: your
Claude login is past its renewal. Give a new one on the Projects tab", see "Jobs". When its
`cannot_run` is set, the panel shows, the same way, the `failed` status "Cannot run" and its text,
see "Stops".

The Settings panel, `src/settings.tsx`, the package's `Card` titled "Settings", read from
`GET /devflow/api/projects/{id}/settings` as the page loads, is a form in three groups, each a
region with a heading and a line on what it holds, fields two by two at desktop width and one
under another at phone width, each with a help line saying what it does:

- **"How cards are picked"**: a line saying Dev flow picks up the open issues the owner's trusted
  accounts wrote that carry the pick-up label; the text fields "Pick-up label", "Hold label",
  "Attention label" and "Review label"; then "Trusted accounts", the list with the owner's own
  login marked "You" and the others each with "Remove", and the field "Add a trusted account" with
  "Add".
- **"How much happens alone"**: a line saying a preset fills in the settings below and is saved as
  any change; the buttons "Fully automatic", "Guided" and "Only I merge", the one the form matches
  pressed and inverted, and beside them "Preset" with the name the form matches as it is typed, or
  "Custom", by the rule of "Presets" below; the checkbox "Questions before starting"; the select
  "Pull requests wait for you", "Never", "Sometimes" or "Always". On "Sometimes", the group "Waits
  for you when" follows, a line saying a pull request waits when any of these holds and merges by
  itself otherwise, the checkboxes "The card or its feature is marked for your review", "The change
  touches a path on the list" and "The agent made a decision for you", and the textarea "Paths",
  one pattern a line in the mono font; on the other two the group is not shown and its values are
  kept and saved as they are. A preset's button changes only what it covers, in the form; nothing
  is saved until "Save".
- **"Models and limits"**: a line saying a change counts from the next job, the selects "Reading
  model", "Building model" and "Reviewing model", then the four limits as number fields.

Lines of "Paths" are trimmed and blank ones dropped as it is saved. A secondary "Save" puts all
eighteen, never the preset, and then says "Saved."; the page's heading takes the preset the answer
names. A value Dev flow refuses is shown under its field as the field's error, from the problem's
`errors`; any other failure is the alert "The settings were not saved. Try again.", and what was
typed is kept.

The Cards panel, `src/cards.tsx`, the package's `Card` titled "Cards", read from
`GET /devflow/api/projects/{id}/cards` as the page loads, is the list "Cards", in the order the
answer gives, each card an inset row: its number, `#12` in the mono font, a link to the issue on
GitHub in a new tab when its address is https and plain text otherwise; its title, as text, a link
to the card's page; its
state as a status, "Queued" and "Held" `idle`, "Ignored", "Stopped" and "Too thin to build",
a card read as too thin, `failed`, "Reading", "Building", "Checking", "Reviewing" and "Fixing"
`running`, "Waiting for you" and a feature's "Ready for you" `needs-you` and "Done" `done`;
and its reason under them as a caption. With none, "No cards yet. An open issue you wrote with the
pick-up label becomes one."; a list that could not be read is an alert. Features come first, in the
list "Features", each its parent's number linked to the issue as a card's is, its title, a link to
the feature's page, see "A feature's page", its state and reason, and its cards in their order in
the list "Cards of #<number>".

A card's page, `src/card.tsx`, is at `/devflow/projects/<id>/cards/<number>`, under the Projects
tab, which stays selected there, read from `GET /devflow/api/projects/{id}/cards/{number}` as it
loads. Its header is a link "Back to the project" over the card's title as a section heading; a
card the caller does not have is the `EmptyState` "There is no such card", and one that cannot be
read an alert. Under it the package's `Card`s: "The card", its state as the Cards panel
shows it with its reason beside it, and the links "Issue #<number>" and "Pull request #<number>",
each in a new tab with `rel="noreferrer"` when its address is https and plain text otherwise, with,
between them for a card of a feature, "Feature #<number>: <title>", to the feature's page;
"The reading", once a reading of the card came back, see "Reading a card", its outcome as a
status, "Ready to build" `done`, "Built on the recommendations" `idle` or "Too thin to build"
`failed`, with "Read on <time>" beside it; for a card with questions, "The card leaves choices
open that it does not settle, so it is built on the reader's recommendations." and "Dev flow posted
them on the card." once its comment is there, "Dev flow posts them on the card." before, muted,
and the list "Questions", each question an inset row with "Answer <the answer it is built on>"
and "Because <why>" under it; and for one too thin, the list "Missing", what it lacks; for a card
read again, "Earlier readings", muted "It was read again before it was built. The readings before
are kept as they were, with any answers given to them, and none is carried into its build.", and
the list "Earlier readings", newest first, from the story's `earlier_readings`, each an inset row
named "Read on <time>" with its outcome as a status, "Ready", "Questions", "Answered" once the owner
answered them, or "Too thin", the list "Questions then", each question with "Your answer <his
answer>", "(recommended)" where he accepted it, or "Recommended <the recommendation>", and the list
"Missing then"; every text the reader's, as text; "The merge", once Dev flow merged its pull request as the app, "Dev flow merged #<pull request>
into <base> as the app, once every condition of the merge check held." and, muted, "Merge commit
<commit> of the approved <commit>, on <time>.", both commits in the mono font; "Rounds", once the card has been sent back, the list "Rounds", newest first, each fix an inset row
named "Round <n> of <m>", `m` the project's `fix_rounds_card` as it is now, or "Your comment" for one the owner's comment started, with its outcome as
a status, "The review of <commit> asked for changes." or "You sent it back with a comment.", his
words under "What you wrote" with their line breaks, the developer's answer to each finding under
"Replies", and "It left <commit>", with what the review of it said, "not reviewed yet", or nothing
more when the fix failed; "Reviews", once the card has one, the list "Reviews", newest first, each
the commit, its verdict, whether it counts for the pull request's head, its summary, its findings,
blocking first, and its view on each decision for the owner; "Runs", the list "Runs", newest first, each run an inset row with its kind and its outcome as a
status, "Succeeded" `done`, "Running" `running`, and "Failed", "Check failed", "The agent
stopped", "Report did not match" and "Lost" `failed`, then in the mono font its worker, when it
started and how long it took, for a failure its reason and whether it lay in the card or outside
it, "Logged in with <auth method>" and "Tokens <in> in, <out> out", and "Check exit <code> on
<commit>" with a "Show" that opens the output's tail, kept as written and wrapped; or "No run has
taken this card yet."; and, when a run has handed one back, "The latest report": its summary, then
its tasks, decisions, tests, pages and follow ups, each a list under its label, each item on an
inset, a decision for the owner marked "For you", a page the agent looked at itself marked
"Looked at" and one it did not "The agent did not look at this page", a list with nothing left
out. With
`screenshots`, a `Card` "Screenshots" after "The card": "Taken by the worker of <commit>, after
its check passed", the worker's note when it has one, and the pairs as part 1's, through
`src/screenshots.tsx`, each read from the card's own route; a card's item on "Waiting for you"
shows the same row after its review. Every text came from GitHub, a worker or an agent and is shown
as text, through `src/format.tsx`.

Waiting for you, `src/waiting.tsx`, is one list of what waits for the owner across his projects,
read from `GET /devflow/api/waiting`, see "What waits", through React Query: as the page loads,
every 60 seconds while the page is visible, and when the window regains focus. Its tab carries the
answer's `count` as a badge once it is more than none. Its parts, top to bottom:

- With `needs_relink`, "Your GitHub link has expired. You can read, but not merge or comment." in
  the attention style, with a link "Go to Projects".
- The age of the picture: for each project that is stale, "<owner>/<name> has not been updated for
  <time>", or "has not been read yet" when no read has succeeded, and for each that is failing,
  "<owner>/<name>: GitHub could not be read, showing what was last seen", in the attention style;
  when every project is fine, one muted "Updated <time> ago" from the oldest `last_success_at`. A
  secondary "Refresh" beside it posts to every project's refresh route and then reads the list
  again.
- Seven groups, in this order, each a section heading in the heading font's capitals with its
  count, and shown only with items: "Questions before starting", Dev flow's cards that wait for the
  owner's answers to their reading's questions, see "Reading a card" under "Jobs"; "Pull requests waiting", Dev flow's own waiting cards, then
  items of kind `waiting` not sent back; "Features ready for your review", Dev flow's features
  ready for review or waiting for the owner, see "Reviewing a feature", each with a secondary
  "Merge into <default>" while it is offered, see "Merging a feature"; "Stopped", kind `stopped` not sent back; "Refused",
  Dev flow's cards refused as too thin to build, see "Reading a card" under "Jobs"; "Decisions to
  look at", the issues Dev flow opened for decisions on cards it merged; and "Sent back, the runner has it", every item
  sent back, muted, and not in the tab's count. Within a group, by `<owner>/<name>` and then
  number.
- An item is the package's `Card`, titled by the pull request, its status label on the right:
  "Waiting for you" in the needs you tone, with the card's attention outline, "Stopped" or "Sent
  back". Under the title, `<owner>/<name> #<number>` in the mono font, then the summary. A waiting
  item with decisions for the owner shows "The agent decided", each decision on an inset with what
  was chosen, "Instead of" and the alternative, and "Because" and the reason, each when there is
  one, and "and <n> more it did not need you for" when the body records others; without
  decisions, "Why it waits" and the runner's reason, and a stopped item "Why it stopped". A
  waiting item's reviewer points follow as "The reviewer". Then, with screenshots,
  `src/screenshots.tsx`, "Screenshots": each pair under its name in the mono font, the desktop
  one and the phone one as thumbnails at most 160px high, loaded lazily and read from the
  screenshot route, never from the body's addresses, each image with its name as its alternative
  text; a name with only one of the two shows that one, and an image that fails to load shows
  "<name> could not be loaded". A thumbnail is a button that opens the image in the package's
  `Dialog`, titled by its name, at its natural size in a frame that scrolls, with one "Close".
  Last, its actions, `src/actions.tsx`,
  and a ghost styled link "Open on GitHub" to the pull request, in a new tab with
  `rel="noreferrer"`, offered only when its address is `https`. On a phone it all stacks and the
  actions wrap.
- The actions, before "Open on GitHub": a waiting item has "Merge" and a secondary "Send back"; a
  stopped item a secondary "Comment to run again" and no Merge; an item sent back nothing but the
  link. Only the first waiting item's Merge is primary, the rest secondary, by the design's one
  primary button per view. Merge is disabled, with a line under it saying why, when `can_merge` is
  false, "The checks are not green on this commit yet", or when GitHub is not linked or the link
  has expired, which `GET /devflow/api/status` says; the comment buttons are disabled for the
  link, with the same line. Merge opens the package's `Dialog`, "Merge #<number> into main?", with
  the title, the head commit's first seven characters in the mono font, and, with decisions for
  the owner, "This confirms <n> decisions the agent made.", and sends nothing until its "Merge" is
  pressed. Send back opens a `TextArea` in the item, labelled "Comment", or "A comment runs it
  again." for a stopped item, with "Send", disabled while the text is empty, and "Cancel". A merge
  ends in a toast, "Merged #<number>", and the item leaves the list; sending back in "Sent back
  #<number>", and the item moves to "Sent back, the runner has it". Either way the list is read
  again, and a refusal is shown inside the item, in words, as an alert. The page is wrapped in the
  package's `ToastProvider`.
- A card of Dev flow's own, `src/waiting-card.tsx`, is the package's `Card` with the attention
  outline, titled by the card, "Waiting for you" on the right; under the title
  `<owner>/<name> #<pull request>` in the mono font, the report's summary, "Why it waits" with the
  card's reason as text, such as "CI did not report" or "This project waits for you on every pull
  request", when it has one, "The agent decided" with
  the decisions the agent marked for the owner, as an item's, and "and <n> more it did not need you
  for", and "The check", "Dev flow ran <check> on <commit>: exit <code>.". Its actions are "Merge",
  primary when it is the group's first entry, and "Open on GitHub" to its pull request, offered
  only when its address is https, with a secondary "Send back" between them, which opens a
  `TextArea`, "What should the developer change?", with "Send" and "Cancel", posts to
  `/devflow/api/projects/{id}/cards/{number}/send-back`, and ends in a toast, "Sent back #<pull
  request>", or a refusal in the card, and the list read again; it is disabled without a usable
  link. Merge is
  disabled with a line saying why while the project is stopped, "The project is stopped: resume it
  before you merge.", or GitHub is not linked or the link has expired; it opens a `Dialog`, "Merge
  #<pull request> into <base>?", with the title, the commit and how many decisions it confirms,
  posts to `/devflow/api/projects/{id}/cards/{number}/merge` with the commit shown, and ends in a
  toast, "Merged #<pull request>", or a refusal in the card, and the list read again.
- With no project, the `EmptyState` "Nothing waits for you", "Connect a repository under Projects
  to see what waits for you."; with projects and no item, "Nothing waits for you", "Your agents
  have nothing for you to decide."; and a list that cannot be read says so as an alert.

Every text in an item came from the code host and is shown as text. The summary, the decisions,
the reviewer's points and the reason go through `src/format.tsx`, which turns `` `code` `` into a
code element and `**bold**` into a strong element, as React elements, and leaves the rest as
written; no Markdown library, and no HTML is ever inserted, see "Text read from a code host is
data" in `docs/devflow-architecture.md`.

"Running now" and "Usage and history" join with the parts that build them, never as empty tabs. The address is the state,
through the History API and no router library (`src/address.ts`): a tab pushes its section's
address, Back and Forward come back as `popstate`, and an address that names no section shows a
not found state under the tabs; a project's name pushes its page's address, and "Back to Projects"
the tab's. The list of sections and the project page's address live in `src/sections.ts`, and the
addresses the service answers the page for in `src/DevFlow/Pages/PageEndpoints.cs`; the two change
together.

`src/api/client.ts` is the page's one way to call Dev flow: every call goes to `/devflow/api/` with
the session cookie, a `401` is refreshed once, shared by every call that got one, by posting to
`/auth/refresh` and repeating the call, and a second `401` sends the browser to
`/auth/refresh?return_to=<path>`, as review's client does. Its GETs are typed from
`src/api/schema.d.ts`. The page reads the status once as it loads, in `src/status.ts`, for the
banner and the link, and again after unlinking; its other calls are starting a link, unlinking,
the projects' four, reading what waits and refreshing a project, merging and sending back,
merging a card, reading a card's story, reading a feature's story and answering a decision on it,
a project's workers and making its key, and reading, giving and removing the Claude login, which
`src/app.tsx` gives the tabs, so a test gives its own,
with its own clock.

## The schema

`Microsoft.AspNetCore.OpenApi` generates the OpenAPI document of the API at
`GET /devflow/api/openapi.json`, needing no token, only when `DEVFLOW_EXPOSE_OPENAPI=1`, so the
running service serves it nowhere. `ContractTests` writes it with keys sorted, a two space indent
and a trailing newline and checks that it equals `contracts/devflow/openapi.json` byte for byte,
see `contracts/README.md`. Every route has a summary and named response types, so a change to
either is a diff to review. After changing a route, from this directory:

```zsh
UPDATE_CONTRACTS=1 dotnet test --filter "FullyQualifiedName~ContractTests"
cd web && npm run generate:api
```

The second line writes `web/src/api/schema.d.ts` from the file with `tools/generate-schema.mjs`,
review's tool, and `src/api/schema.test.ts` fails when it is stale. The app is named for its own
assembly, so the document is the same under the tests' host as in the service.

## Running it

From this directory, with the .NET SDK `global.json` pins installed, the database migrated (see
"Migrating"), and the page built first, since Dev flow refuses to start without
`web/dist/index.html`, naming it:

```zsh
(cd web && npm ci && npm run build)
DEVFLOW_APP_DATABASE_URL="postgres://devflow_app:<password>@127.0.0.1:5432/devflow" \
  dotnet run --project src/DevFlow -c Release --no-launch-profile
```

`dotnet run` runs it from this directory, where `web/dist` is, which `RunWorkingDirectory` in
`src/DevFlow/DevFlow.csproj` sets; the Web SDK would otherwise run it from the project's folder. Its
entry in `infra/services.json` starts it the same way, with `DEVFLOW_LISTEN_ADDR` set to the host
and port the manifest gives, and `DEVFLOW_APP_DATABASE_URL` built from `infra/.env` by
`scripts/agent/services.sh`. Port 8084 sits beside review's 8083, and the Caddyfile's upstream for
`/devflow` points at it. In the hosted copy it is the `devflow` container, built by `Dockerfile`
here, see `infra/hosted/README.md`.

Its tests, the steps `.github/workflows/dotnet.yml` runs:

```zsh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore -c Release
DEVFLOW_TEST_ADMIN_DATABASE_URL="postgres://postgres:$(grep '^POSTGRES_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/postgres" \
DEVFLOW_APP_DATABASE_URL="postgres://devflow_app:$(grep '^DEVFLOW_APP_PASSWORD=' ../infra/.env | cut -d= -f2-)@127.0.0.1:5432/devflow" \
  dotnet test --no-build --no-restore -c Release
```

The tests that need a database each get one of their own, from `Databases.DevFlow` in
`tests/DevFlow.Tests/Harness/TestDatabase.cs`, the harness `PlatformData.Testing` holds, see
`libs/dotnet/PlatformData/README.md`, through the Postgres superuser's string,
`DEVFLOW_TEST_ADMIN_DATABASE_URL`, which only the test step holds, and as `devflow_app` through
`DEVFLOW_APP_DATABASE_URL` pointed at their copy. Both are required, and unset is a failure, never
a skip. Tests that need no database take none.

and the page's, from `web/`, the steps `.github/workflows/web.yml` runs for every frontend:

```zsh
npm ci
npx biome ci .
npx tsc --noEmit
npx vitest run
npm run build
```

`packages.lock.json` is committed beside each project; after changing a package, run
`dotnet restore` without `--locked-mode` once to update it, and commit the change. The default
`.editorconfig` wants no final newline in a C# file, and `dotnet format --no-restore` fixes one.

## Settings

Dev flow reads its settings from the process environment, once, at startup, into one immutable
`DevFlowSettings`, in `src/DevFlow/Settings/DevFlowSettings.cs`. It loads no `.env` file. Unset
means the default, and a value that is set must parse, or Dev flow refuses to start, naming the
setting, rather than quietly using the default. The rules are review's, written again rather than
shared, since no service imports from another's directory.

- `DEVFLOW_LISTEN_ADDR`, default `127.0.0.1:8084`: where Kestrel listens, a host and a port, an
  IPv6 host in brackets.
- `DEVFLOW_CORE_URL`, default `http://127.0.0.1:8080`: the core's own address, never the proxy's,
  where the key set is fetched. A scheme, a host and an optional port, with nothing after it.
- `DEVFLOW_PUBLIC_ORIGIN`, default `https://platform.test`: the platform's origin exactly as a
  browser sends it, by the same rule as `PublicOrigin::parse` in `Core/src/origin.rs`.
- `DEVFLOW_EXPOSE_OPENAPI`, default `0`: `1` maps the schema's route, for the contract test and
  regenerating the file, see "The schema". Nothing else is accepted.
- `DEVFLOW_APP_DATABASE_URL`, required, with no default: the database as `devflow_app`,
  `postgres://devflow_app:<password>@<host>:<port>/devflow`, parsed by `DatabaseUrl.Parse` from
  `libs/dotnet/PlatformData/`. Unset, or anything else, stops Dev flow at startup, naming the setting
  and never repeating the value, since it holds a password, which `DevFlowSettings` also leaves out
  of what it prints.
- `DEVFLOW_OWNER_DATABASE_URL`, the same form as `devflow_owner`: read by `migrate` alone, never
  by Dev flow while it serves, see "Migrating".
- `DEVFLOW_CODE_HOST`, default `fake`: `fake` or `github`, nothing else, and set but empty is refused
  like any value that does not parse. It decides which code host Dev flow is built with, whether
  the fake's addresses exist and the page shows its banner, whether the vault needs a key, and
  whether the four GitHub settings below are read; see "The code host".
- `DEVFLOW_VAULT_KEY`, the vault's current key, `<id>:<base64 of 32 bytes>`, see "The vault". Unset
  or empty, Dev flow makes one in memory with the fake code host and refuses to start with
  `github`. A key that does not parse stops it, naming the setting and never repeating the value.
- `DEVFLOW_VAULT_OLD_KEYS`, unset or empty for none: keys rows may still be sealed with, comma
  separated in the same form, each with an id of its own that is not the current key's.
- The GitHub App's four, in `src/DevFlow/Settings/GitHubSettings.cs`, each required with
  `DEVFLOW_CODE_HOST=github` and ignored with `fake`, whatever their values; empty is unset. A
  missing or malformed one stops Dev flow at startup, naming the setting and never repeating its
  value, and `DevFlowSettings` prints the client id and the slug alone.
  - `DEVFLOW_GITHUB_CLIENT_ID`: the app's client id, ASCII letters, digits and dots, such as
    `Iv23li` followed by the rest. The app's tokens carry it as `iss`.
  - `DEVFLOW_GITHUB_CLIENT_SECRET`: a client secret of the app, printable ASCII without spaces.
    Sent when a person's code or refresh token is exchanged for their tokens, see "Linking
    GitHub".
  - `DEVFLOW_GITHUB_PRIVATE_KEY_FILE`: the path of the app's private key, read once at startup with
    `RSA.ImportFromPem`. It must be an RSA private key of at least 2048 bits in PEM, unencrypted,
    either `BEGIN RSA PRIVATE KEY`, as GitHub gives it, or `BEGIN PRIVATE KEY`.
  - `DEVFLOW_GITHUB_APP_SLUG`: the app's name in its address, `github.com/apps/<slug>`, lowercase
    letters, digits and inner hyphens. It makes `install_url`, where the owner installs the app,
    see "Connecting a repository".
- `DEVFLOW_POLL_SECONDS`, default `60`: how often the loop reads every project, in whole seconds,
  ASCII digits alone, from `10` to `86400`, a day; or `0`, which turns the loop off, as the
  service's own tests do, see "What waits". Empty is unset, so the hosted copy passes it empty when
  its `.env` leaves it out and the default stands.
- `DEVFLOW_WORKER_IMAGE`, default `platform-hosted/devflow-worker:dev`: the worker's base image, which
  the command on a project's page builds a worker FROM, and which a worker must report to be given
  work, see "Starting a worker". At most 255 characters, beginning with a lowercase letter or a
  digit, of letters, digits and `._/:@-` alone, since the command shows it unquoted.
- `DEVFLOW_WORKER_URL`, default `http://host.docker.internal:8084`: the address a worker calls Dev
  flow at from its container, which the command starts each worker with. A scheme, a host that is
  not an IPv6 address, whose brackets a shell reads as a pattern, and an optional port, nothing
  after it. The default is Dev flow on the dev stack as Docker Desktop's containers reach the
  host.

The defaults are right on the Mac mini and in CI, so neither sets them, the code host's fake
included, except the listen address,
which the manifest's `start` sets, and the database URLs, which `scripts/agent/services.sh` and
`.github/workflows/dotnet.yml` build. The hosted copy sets `DEVFLOW_WORKER_IMAGE` to the base image
of its own release, `platform-hosted/devflow-worker:<PLATFORM_RELEASE>`, and `DEVFLOW_WORKER_URL`
to its `.env`'s, `http://host.docker.internal:9080` unless set, the proxy's published port, so a
release does not refuse without it. It sets the four it needs to serve and passes the
vault's two, the code host's mode and the GitHub App's settings in `infra/hosted/compose.yaml`, from
its `.env`, and the owner's URL only on `devflow-migrate`. The app's key file is mounted read only
at `/run/secrets/github-app.pem`, which is the path Dev flow is given; the `.env` names the file on
the machine. Its `.env.example` names the mode `github` and lists the app's four, so a release
refuses an env file without them, and `check.sh`'s throwaway copy sets `fake` with placeholders
and an empty key file, which the fake never reads.

## The database

Dev flow has a database of its own, `devflow`, in the one Postgres in `infra/`, created with its two
roles by `infra/postgres/initdb/devflow.sh`, see "The databases" in `infra/README.md`. Why a
database per service, and why the roles are split, is in "Databases, migrations, and running it"
and "Multi user, and how isolation is enforced" in `High-level/docs/platform-architecture.md`.
Everything it does with it is the shared library's, `libs/dotnet/PlatformData/`, whose README says
what it holds; what is Dev flow's own is below.

- **`devflow_owner`** owns the tables and runs the migrations. Nothing serving connects as it.
- **`devflow_app`** is what Dev flow serves as. It owns nothing, so row level security applies to
  it, and `migrations/0001_default_privileges.sql` grants it `SELECT`, `INSERT`, `UPDATE` and
  `DELETE` on every table the owner creates and `USAGE` on every sequence, never `TRUNCATE`, and
  takes `CREATE` on `public` from everyone but its owner, as review's first migration does.

Its tables are the vault's, `secrets`, from `migrations/0002_secrets.sql`, see "The vault", the
GitHub link's two, `github_sign_ins` and `github_accounts`, from
`migrations/0003_github_link.sql`, see "Linking GitHub", and `projects` with `poll_targets`, from
`migrations/0004_projects.sql`, see "Connecting a repository", and `waiting_items` with
`project_reads`, from `migrations/0005_waiting_items.sql`, see "What waits", and `worker_keys`,
`worker_key_lookup` and `workers`, from `migrations/0007_workers.sql`, see "Workers".
`poll_targets` and `worker_key_lookup` are the two tables without row level security, which
`devflow_app` may only read.

### Migrating

Migrations are plain SQL files in `migrations/`, named `NNNN_snake_description.sql`, forward only,
embedded in the assembly by `src/DevFlow/DevFlow.csproj`. They run only as a command, never at
startup:

```zsh
DEVFLOW_OWNER_DATABASE_URL="postgres://devflow_owner:<password>@127.0.0.1:5432/devflow" \
  dotnet run --project src/DevFlow -c Release --no-launch-profile -- migrate
```

`src/DevFlow/Commands.cs` runs the shared `MigrateCommand` with the name `devflow`, so its lock's
key is the first 8 bytes of SHA-256 of `devflow.migrate`, reading `DEVFLOW_OWNER_DATABASE_URL` and
nothing else. With no argument Dev flow serves, and any argument but `migrate` is refused with the
usage. On the Mac mini and in CI the manifest's `migrate` runs it; in the hosted copy,
`devflow-migrate`.

### The door

Every query goes through `UserDb.InUserTransaction(userId, work, cancellationToken)`, the shared
library's door, which sets `devflow.user_id` for that transaction alone, but two, each through
the door's `WithoutUser`, a transaction on behalf of nobody, to which every guarded table is
empty: the loop lists `poll_targets` and then reads each project as its user, see "What waits",
and a worker's request looks its key's hash up in `worker_key_lookup` and then does everything as
that project's user, see "Workers". Dev flow registers one
`UserDb`, opened at startup in `src/DevFlow/Commands.cs` with the name `devflow`,
`DevFlowSettings.Service`, as `devflow_app`, so a database it cannot reach, or a wrong password,
stops Dev flow there, naming `DEVFLOW_APP_DATABASE_URL` and never its password. It opens no
connection of its own, which `IsolationTests.OnlyTheDoorOpensConnections` checks by reflection.

Every scoped table has `user_id uuid NOT NULL`, row level security enabled
and forced, and one policy using `UserDb.GuardFor("devflow")` for `USING` and `WITH CHECK`.
`tests/DevFlow.Tests/IsolationTests.cs` proves the policy on a probe table, and fails for any table
a later migration adds to `public` without exactly that one policy, `schema_migrations` aside and
the two tables left unguarded on purpose, `poll_targets` and `worker_key_lookup`, which the test
names; Dev flow has no other path around the policies.

## The vault

The vault keeps a secret for a user, such as their GitHub token, in the database, sealed so that a
copy of the database does not reveal it. It is `Vault`, in `src/DevFlow/Vault/`, one instance the
app registers beside the door, with three operations that each take the door's transaction, so a
secret is only ever touched as its user:

- `Put(transaction, userId, purpose, value, cancellationToken)` seals the value, bytes, and stores
  it, replacing any before it;
- `Get(transaction, userId, purpose, cancellationToken)` gives the value, or null when there is
  none;
- `Delete(transaction, userId, purpose, cancellationToken)` removes it, saying whether there was
  one.

A purpose is a short fixed name the caller chooses, such as `github.access` or `claude-login`,
matching `^[a-z][a-z0-9.-]*$`; anything else is an `ArgumentException`. The hyphen arrived with
`claude-login`, in `migrations/0008_claude_logins.sql`, which changes the table's check with it.

Each row of `secrets` is one user's secret for one purpose: `key_id`, `nonce` and `ciphertext`,
with `updated_at`. A put seals with AES-256-GCM, from .NET's own `AesGcm`, under the current key, a
random 12 byte nonce every time and the 16 byte tag appended to the ciphertext. The associated data
is the UTF-8 of `devflow:v1:<user id>:<purpose>`, so a row moved to another user or purpose does
not open, and neither does one with any byte changed. The table is guarded like every scoped one,
so a user's transaction never sees another's rows. A get that cannot open its row throws
`VaultException`, naming the purpose and, for a key that is not configured, the key's id.

Nothing prints a value or a key: not a log line, not an exception's message, not a problem
document. `DevFlowSettings`, `VaultSettings` and `VaultKey` print key ids alone.

**The key** is `DEVFLOW_VAULT_KEY`, `<id>:<base64 of 32 random bytes>`, the id 1 to 16 lowercase
letters and digits. Make one with:

```zsh
printf 'k1:%s\n' "$(openssl rand -base64 32)"
```

A put records the id of the key it sealed with, and a get opens with the key its row names, the
current one or one listed in `DEVFLOW_VAULT_OLD_KEYS`; a row naming a key that is not configured is
refused with its id. So a key is changed by making a new one current and listing the old one until
every row has been put again, which happens as each secret is next written. Nothing rewrites every
row at once yet.

Without a key, with the fake code host, Dev flow makes a random key in memory at startup and says so
in one line on stderr, without the key: the tests, CI and the runner's stack need nothing set, and
what is sealed there is lost at a restart, which is right for a throwaway stack. With
`DEVFLOW_CODE_HOST=github` a missing key stops startup. The hosted copy's key is in its `.env`, and
the owner's password manager, never in the backups; see `infra/hosted/README.md`.

What it protects against, and what it does not, is in "Security decisions and accepted risks" in
`docs/devflow-architecture.md`.

## The code host

Everything Dev flow asks of a code host goes through one interface, `ICodeHost`, in
`src/DevFlow/CodeHost/`, with plain records for what it answers. Why it sits there, and why the
fake does, is "The code host seam" in `docs/devflow-architecture.md`. It names things as the
development flow's design does, a repository, a pull request, a comment, never as GitHub's API
does. Its read side:

- `OpenPullRequests(user, repository)`: each open pull request, with its number, title, author's
  login, labels, head commit, head and base branch, whether it is a draft, body, web address, when
  it was last updated, and its state;
- `PullRequest(user, repository, number)`: one, whatever its state, open, merged or closed;
- `Comments(user, repository, number)`: an issue's or pull request's conversation comments, oldest
  first, each with id, author's login, body, when it was created and when it was last edited;
- `Statuses(user, repository, commit)`: each context with its state and description;
- `CheckRuns(user, repository, commit)`: each name with its status, conclusion and the slug of the
  app that made it;
- `File(user, repository, path, branch)`: the bytes;
- `DefaultBranchFile(user, repository, path)`: the file on the repository's default branch, read at
  the newest commit of that branch that changed it, and that commit's id, which changes when the
  file does and not when the rest of the repository does; not found when no commit has it. On
  GitHub it is `GET /repos/{owner}/{repo}/commits?path=…&per_page=1`, which with no `sha` lists the
  default branch, then the file raw at exactly that commit; in the fake, the file on the
  repository's default branch, its commit the SHA-256 of its content in hex, since the fake keeps
  no history;
- `FolderFile(user, repository, folder, path)`: the file at `path`, inside `folder`, on the default
  branch, read at the newest commit of that branch that changed anything under the folder, and
  that commit's id, a project's setup version for `.devflow`; on GitHub the same two reads with
  `path=<folder>`, and in the fake, its commit the SHA-256 of each of the folder's files' path and
  content's SHA-256, in the order of their paths, `FakeCodeHost.FolderVersion`;
- `DefaultBranch(user, repository)`: the repository's default branch, as the host says it now;
- `BranchHead(user, repository, branch)`: the commit a branch is at now, the commit a check is
  handed; on GitHub `GET /repos/{owner}/{repo}/git/ref/heads/{branch}`, and in the fake the branch
  in its git repository, the default branch given the fake's first commit when it has none;
- `ChangedFiles(user, repository, from, head)`: the paths of the files `head` changed since its
  merge base with `from`, both full commit ids, at most 300, each once, a renamed file by its new
  path. On GitHub it is `GET /repos/{owner}/{repo}/compare/{from}...{head}?per_page=1`, each
  file's `filename`, the first page holding every file GitHub lists and one commit, which is not
  read; in the fake, `git diff --name-only from...head` in the repository's git repository, sorted.
  Handing out a conflict reads it both ways, see "A conflict" under "The end of a feature";
- `PullRequestFiles(user, repository, number)`: every file the pull request changes, in the host's
  order, each a `ChangedFile` with its path and, for a rename, its old path; the whole list or a
  failure, never part of it; at most 3000, GitHub's limit, `ICodeHost.MostPullRequestFiles`. On
  GitHub it is `GET /repos/{owner}/{repo}/pulls/{number}/files?per_page=100`, paged to the end,
  each file's `filename` and `previous_filename`; in the fake, what the world stages for the pull
  request. The merge check reads it for the paths of "sometimes", see "The merge check";
- `Activity(user, repository, branch, since)`: what changed the branch, newest first, each a
  `BranchActivity` with the host's id, the commit before and after, when, its `ActivityKind`,
  `Push`, `ForcePush`, `BranchCreation`, `BranchDeletion`, `PullRequestMerge` or
  `MergeQueueMerge`, and the actor's login, null when the host names none. With `since` null, the
  newest page alone; otherwise every change newer than the one with that id, read page by page
  until it is reached, and all of them when it never is, as for `0`. "Watching branches"
  reads both;
- `Installations(user, userToken)`: the installations of the app the person whose token it is can
  reach, each with its id and the login of the account it is on;
- `Repositories(user, userToken, installation)`: the repositories of one of them the person can
  reach, each a `Repository`; not found for an installation they cannot reach;
- `InstallAddress(origin)`: where a person installs the app, GitHub's page for it, or the fake's
  own under the platform's origin.

The two that take a user's token read as that person, never as the app, so what they list is what
the person may connect; the token comes from `UserTokens.For`, see "Linking GitHub".

The owner's write side, both as the person and never as the app:

- `Merge(user, token, repository, number, commit)`: merges the pull request with a merge commit
  while its head is `commit`, answering a `MergeResult`: `Merged` with the merge commit's id, null
  when the host's answer gives none that reads as one, `HeadMoved` when the head is
  another commit, `NotMergeable` with the host's words, `Refused` with the host's status and words,
  or `Unavailable` when it did not answer;
- `Comment(user, token, repository, number, body)`: writes `body`, exactly, in the pull request's
  conversation, answering a `CommentResult`: `Posted` with the comment's id, null when the host's
  answer gives none, `Refused` with the status and words, or `Unavailable`.

Each takes a `UserAccessToken`, a type of its own, and nothing else that could authorise it. An
installation's token is a string, so passing one to a write does not compile. The type's
constructor is `internal`, and the one code in the service that calls it is `UserTokens`, which
answers `UserToken.Usable` holding it; `GitHubWriteTests.AUserTokenIsMadeOnlyByUserTokens` reads
the compiled code and fails if anything else does. Why, and where the compiler's guarantee ends,
is "The owner's actions from the page are made only with his token" in
`devflow/docs/devflow-architecture.md`.

The program's own write side, as the app, each with an `AppWriteToken`, a key the app made for one
purpose on one repository, see "The program's own writes" in `docs/devflow-architecture.md`:

- `ForWrite(user, repository, purpose)`: a new key for `Push`, `Post` or `Merge`, never kept, as a
  `CodeHostResult<AppWriteToken>`; not found when the repository is not one the app reaches or the
  installation has not granted what the purpose needs. The one way to a key;
- `CloneAddress(user, repository)`: where git fetches and pushes it, GitHub's `https` address, or
  the fake's `file://` one;
- `OpenPullRequest(user, token, repository, headBranch, baseBranch, title, body)`: the pull request
  as the host made it;
- `CommentOnIssue(user, token, repository, number, body)`: on an issue or a pull request, the
  comment;
- `AddLabel` and `RemoveLabel(user, token, repository, number, label)`: the labels it carries after;
  removing one it does not carry is refused with `404`;
- `OpenIssue(user, token, repository, title, body, labels)`: the issue, its number, title, labels
  and web address;
- `MergeAsApp(user, token, repository, number, commit)`: a merge commit while its head is `commit`,
  answered as `Merge` is.

The first five writes take a `Post` key and `MergeAsApp` a `Merge` key; a `Push` key is for git
alone. A key for another purpose or repository is refused with an `ArgumentException` before the
host is asked, since it is a bug in Dev flow, not a condition to handle. Each but the merge answers
a `WriteResult<T>`: `Written` with its value, `Refused` with the host's status and words, or
`Unavailable` when it did not answer, or answered a success that does not parse. Nothing is
retried in a code host. `AppWriteToken`'s constructor is `internal`, and
`AppWriteTokenTests.OnlyTheIssuerMakesOne` reads the compiled code and fails if anything but the two
issuers, `InstallationTokens` and the fake, calls it.

A `Repository` is the installation it was reached through and its id, which identify it, with its
owner and name carried for display and for GitHub's addresses, which name a repository by them; the
adapter's token is narrowed to the id, so a name that no longer matches it is not found rather than
another repository read. Every operation takes the platform account it is asked for:
the fake keeps a world per account, and the GitHub adapter reads as the app and writes as the
account's linked user. Each read answers a `CodeHostResult<T>`: `Found` with the value, `NotFound`,
or `Unavailable` with when to try again if the host said, and the caller handles all three; nothing
is thrown for an expected condition.

Signing a person in is a second interface beside it in the same folder, `ICodeHostSignIn`, which
both implementations implement, since its reads are as the app and these are as a person:

- `AuthorizeAddress(user, state, challenge, redirect)`: the code host's page to send the browser
  to, carrying the state, the S256 challenge and where to send it back;
- `Exchange(user, code, verifier, redirect)`: the two tokens for the code the browser came back
  with, an access token and a refresh token, with when each expires;
- `Renew(user, refreshToken)`: a new pair, the refresh token taken once;
- `WhoIs(user, accessToken)`: the account the token acts as, its login and numeric id.

Each answers a `SignInResult<T>`: `Granted`, `Refused` when the host does not take what it was
given, or `Unavailable` when it did not answer. The two failures are kept apart because a refused
renewal ends a link and an unavailable one must not. The PKCE challenge is `Pkce.Challenge`, the
base64url of the verifier's SHA-256, RFC 7636's S256.

`DEVFLOW_CODE_HOST` chooses the implementation when the app is built, in `DevFlowApp.Build`. `fake`
is `FakeCodeHost`; `github` is `GitHubCodeHost`, in `src/DevFlow/CodeHost/GitHub/`, registered by
`AddGitHubCodeHost` with its settings. A test can still hand the app a code host of its own, which
`Build` keeps, as the test harness's `codeHost` does.

### The GitHub adapter

`GitHubCodeHost` reads GitHub's REST API as Dev flow's GitHub App. How it signs in, why its keys
can only read and what it does with GitHub's failures are "GitHub" in
`docs/devflow-architecture.md`. Each operation is one address, relative to
`https://api.github.com`, a constant:

| Operation | Address |
|---|---|
| `OpenPullRequests` | `GET /repos/{owner}/{repo}/pulls?state=open&per_page=100` |
| `PullRequest` | `GET /repos/{owner}/{repo}/pulls/{number}` |
| `Comments` | `GET /repos/{owner}/{repo}/issues/{number}/comments?per_page=100` |
| `Statuses` | `GET /repos/{owner}/{repo}/commits/{sha}/statuses?per_page=100`, the first of each context, which is its latest |
| `CheckRuns` | `GET /repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100` |
| `File` | `GET /repos/{owner}/{repo}/contents/{path}?ref={branch}`, with `Accept: application/vnd.github.raw+json` |
| `Installations` | `GET /user/installations?per_page=100`, with the user's token, each installation's `id` and its account's `login`, or an enterprise's `slug` |
| `Repositories` | `GET /user/installations/{id}/repositories?per_page=100`, with the user's token |
| `InstallAddress` | `https://github.com/apps/<DEVFLOW_GITHUB_APP_SLUG>/installations/new`, on GitHub's site |
| `DefaultBranch` | `GET /repos/{owner}/{repo}`, its `default_branch` |
| `Activity` | `GET /repos/{owner}/{repo}/activity?ref={branch}&per_page=100`, newest first, following the `Link` header's cursor until the change `since` is reached; a change on another ref, a type the adapter does not know, or a commit id that is not 40 or 64 lowercase hex digits makes the whole answer unavailable |
| `Merge` | `PUT /repos/{owner}/{repo}/pulls/{number}/merge` with `{"sha": "<commit>", "merge_method": "merge"}`, with the user's token: `200` merged, with the answer's `sha` as the merge commit, `409` head moved, `405` not mergeable, any other status refused with it |
| `Comment` | `POST /repos/{owner}/{repo}/issues/{number}/comments` with `{"body": "<text>"}`, with the user's token: `201` posted, any other status refused with it |
| `ForWrite` | `POST /app/installations/{id}/access_tokens` with `repository_ids` and the purpose's `permissions`, as the app |
| `CloneAddress` | `https://github.com/{owner}/{repo}.git`, with no request |
| `OpenPullRequest` | `POST /repos/{owner}/{repo}/pulls` with `title`, `head`, `base` and `body`: `201` written |
| `CommentOnIssue` | `POST /repos/{owner}/{repo}/issues/{number}/comments` with `body`: `201` written |
| `AddLabel` | `POST /repos/{owner}/{repo}/issues/{number}/labels` with `{"labels": ["<label>"]}`: `200` written |
| `RemoveLabel` | `DELETE /repos/{owner}/{repo}/issues/{number}/labels/{name}`, the name escaped: `200` written |
| `OpenIssue` | `POST /repos/{owner}/{repo}/issues` with `title`, `body` and `labels`: `201` written |
| `MergeAsApp` | `PUT /repos/{owner}/{repo}/pulls/{number}/merge` with `sha` and `merge_method` `merge`, answered as `Merge` |

The app's writes carry the key `ForWrite` made, and any status but the one written is refused with
it and GitHub's words.

The two read with the user's token follow every page as the others do, and nothing of them is
remembered for a conditional request, since an answer read with one person's token is never one to
give another. The owner's two writes never ask for an installation token. No write is retried, and
each keeps at most 500 characters of GitHub's `message` from a refusal.

Signing a person in, in `GitHubCodeHost.SignIn.cs`, is GitHub's web flow exactly as GitHub
documents it:

| Operation | Address |
|---|---|
| `AuthorizeAddress` | `https://github.com/login/oauth/authorize` with `client_id`, `redirect_uri`, `state`, `code_challenge` and `code_challenge_method=S256` |
| `Exchange` | `POST https://github.com/login/oauth/access_token`, a form of `client_id`, `client_secret`, `code`, `redirect_uri` and `code_verifier`, with `Accept: application/json` |
| `Renew` | the same `POST`, a form of `client_id`, `client_secret`, `grant_type=refresh_token` and `refresh_token` |
| `WhoIs` | `GET /user` on the API, with the user's token |

GitHub answers a token request it refuses with `200` and an `error` field. Each error is refused,
but `incorrect_client_credentials` and `redirect_uri_mismatch`, which say the app's own settings are
wrong, are unavailable, as is any answer that is not a success and one without expiring tokens,
which an app without "Expire user authorization tokens" gives: ending a person's link would fix
none of them. `WhoIs` is refused on `401`.

Every request is sent with one `HttpClient` named `github` from `IHttpClientFactory`, with a 20
second timeout, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: platform-devflow`, and `Accept:
application/vnd.github+json` unless it reads a file, and the client's logs leave every header's
value out. GitHub's answers are read into records private to the adapter, in
`GitHubCodeHost.Answers.cs`; a field GitHub always sends must be there, and a word the adapter does
not know, such as a new state, makes the answer one that does not parse. An author whose account
was deleted is `ghost`, as GitHub shows it, and a body GitHub sends as null is empty. Waiting,
requested and pending check runs are queued.

Its tests are in `tests/DevFlow.Tests/GitHub/`, against GitHub's real answers recorded in
`contracts/github/`, and for signing in against its documented answers written there by hand, see
`contracts/README.md`, with a stub `HttpMessageHandler` in place of the
network and a `FakeTimeProvider`: no test reaches GitHub. They build it as Dev flow does, through
`AddGitHubCodeHost`, and replace the client's handler, never its address.

### The fake

`FakeCodeHost` keeps a world per platform account, in memory, lost at a restart: installations,
their repositories, and each repository's pull requests, conversations, commits with their statuses
and check runs, files, and issues, each with what it is blocked by and its edits, and what changed
its branches. Per account, since every acceptance test registers its own account and
they all run against one Dev flow. A world is one JSON document whose shape is the interface's
records, so staging something is writing what the interface would answer:

```json
{
  "installations": [
    {
      "id": 1,
      "account": "Adamniels",
      "repositories": [
        {
          "id": 1001, "owner": "Adamniels", "name": "Personal-platform",
          "pull_requests": [ { "number": 304, "title": "The vault", "author": "mac-mini-agent",
            "labels": ["owner-merge"], "head_commit": "<40 hex>", "head_branch": "272-the-vault",
            "base_branch": "main", "draft": false, "body": "", "url": "https://github.com/...",
            "updated_at": "2026-11-15T09:30:00+00:00", "state": "open" } ],
          "conversations": [ { "number": 304, "comments": [ { "id": 1, "author": "Adamniels",
            "body": "...", "created_at": "...", "updated_at": "..." } ] } ],
          "commits": [ { "id": "<40 hex>",
            "statuses": [ { "context": "review-ok", "state": "success", "description": null } ],
            "check_runs": [ { "name": "ci-ok", "status": "completed", "conclusion": "success",
              "app": "github-actions" } ] } ],
          "files": [ { "path": "AGENTS.md", "branch": "main", "content": "<base64>" } ],
          "issues": [ { "number": 338, "title": "Settings and picking cards", "author": "octo-owner",
            "labels": ["devflow"], "body": "...", "url": "https://github.com/...",
            "created_at": "...", "open": true, "blocked_by": [339],
            "edits": [ { "id": "UCE_1", "editor": "octo-owner", "at": "...", "kind": "body" } ] } ]
        }
      ]
    }
  ]
}
```

A world may also name who the account is at the fake, `"user": {"login": "octo-owner", "id":
70000001}`, the one person its sign in page shows and its tokens act as; it is the one field a
world may leave out, `octo-owner` with that id when it does, and it is left out when it is read
back.

A repository may also name its default branch, `"default_branch": "trunk"`, `main` when it is left
out, and list what changed its branches, oldest first, as `ICodeHost.Activity` answers it newest
first: `"activity": [{"id": 1, "branch": "main", "before": "<40 hex>", "after": "<40 hex>", "at":
"...", "kind": "push", "actor": "devflow-fake[bot]"}]`, `kind` one of `push`, `force_push`,
`branch_creation`, `branch_deletion`, `pull_request_merge` and `merge_queue_merge`, and `actor`
null for an account GitHub no longer names; none when it is left out. It may also list the branches
that bringing another branch into answers a conflict, `"conflicts": ["feature-60-x"]`, none when it
is left out, see "The end of a feature". Each is left out when it is read back unset. A test stages
a push to the default branch by adding one, as `add_activity` in `e2e/devflow_worlds.py` does, or
pushes a commit through `POST /devflow/api/fake/push`, below.

It is read strictly, by `FakeJson` in `src/DevFlow/CodeHost/FakeWorld.cs`: every field present but
`user`, a repository's `default_branch`, its `activity` and its `conflicts`, null only where the record allows it, no field the records do not have, numbers as numbers, and states
as their snake case words, `open`, `in_progress`, `timed_out`. What identifies a thing is listed
once: an installation's id, a repository's id within it, a pull request's or a conversation's
number, a commit's id, a file's path and branch, a change's id, and an issue's number, which no
pull request may share. An installation's `account` is the login of
the account it is on, which the list to choose from groups by. A pull request's number reaches its
conversation, empty unless one is listed; a commit that is not listed is not found, unless the
repository's git repository holds it, such as a commit the fake worker made, which has no statuses
and no check runs, as GitHub answers for a commit nothing was posted on. An issue's
`blocked_by` names issues listed in the same repository, by number. An issue may also have
`sub_issues`, its sub-issues in their order, each `{"number", "repository"}`, the id of another
repository of the same installation or null for this one, naming an issue or a pull request listed
there and never the issue itself; it and `label_events` are the two fields an issue may leave out,
and each is left out when the world is written back unset. `ICodeHost.SubIssues` answers them in that order, each from its own
repository, a pull request marked. Listing issues by label answers
the open issues carrying it, whatever its case, and the open pull requests carrying it, marked as
pull requests, as GitHub lists them; an issue's edits are answered as staged, its body's first and
then its title's, `kind` `body` or `title`. An issue may also have `label_events`, each
`{"label", "change", "actor", "at"}`, `change` `added` or `removed`, answered by
`ICodeHost.LabelEvents` oldest first by `at`; left out, each label the issue carries was put on by
its author at `created_at`, which is what most worlds mean, and it is left out when the world is
written back unset. The app's own `AddLabel` and `RemoveLabel` on an issue add theirs to it, by the
app's login, as GitHub's timeline would. A repository may also have `pull_request_files`, each
`{"number", "files"}`, a pull request's number and the files it changes in order, each
`{"path", "previous_path"}`, `previous_path` null but for a rename, answered by
`ICodeHost.PullRequestFiles`; a pull request it does not list changes none, and it is left out when
the world is written back unset.

The fake signs a person in too, in `FakeCodeHost.SignIn.cs`, per account and apart from its world,
so staging a world again signs nobody out. Its sign in page, `GET /devflow/fake-github/authorize`,
mapped only in the fake mode and under the `Page` policy, so it knows whose world to read, is a
plain page that names itself "Fake GitHub", shows the login of the world's user, and has
"Authorize" and "Deny", a form posted back to the same address, which sends the browser to the
redirect address with a code and the state, or with `error=access_denied` and the state. It sends
the browser only to Dev flow's own redirect address, and answers `400` to anything else, as GitHub
sends it only to a registered one. Its codes last ten minutes and are taken once, bound to the
challenge, and a verifier the challenge was not made from is refused. Its tokens are random and
last as long as GitHub's, eight hours and six months, and a refresh token is taken once, so a test
of renewal means something. With the account set unavailable, signing in is unavailable too.

Listing what a person can connect reads the world's installations and their repositories, and
only with the account's current access token from its sign in, as GitHub takes only the person's
own: another token, or one that has expired, is unavailable, as the adapter answers GitHub's `401`.
Its install page, `GET /devflow/fake-github/install`, mapped only in the fake mode and under the
`Page` policy, is a plain "Fake GitHub" page that installs nothing and says a test stages
installations in its world.

Its writes, in `FakeCodeHost.Writes.cs`, take only the account's current access token too, and
answer another as GitHub answers `401`, refused with "Bad credentials". A merge is refused with
`404` for a pull request the world does not hold, is not mergeable unless the pull request is open,
answers head moved unless its head is the commit named, and otherwise marks it merged in the world
and lands it on the repository's default branch as a `pull_request_merge` by whoever merged, from the
commit the branch's last change left it at, forty zeros before any, to a new random merge commit,
which the merge answers. Merged into the default branch, each issue of the repository its body
names after a closing keyword, `close`, `fix` or `resolve` in any form and case, as `Closes #10`,
is closed, as GitHub closes it; a merge into another branch closes none, as on GitHub.
A comment is refused with `404` for a number with neither a pull request nor a conversation, and is
otherwise added to the end of the conversation, by the login the token acts as, at the fake's now.
Each write taken is recorded, with whose token made it, and the record survives staging a world
again, so a test asserts who wrote.

The app's writes, in `FakeCodeHost.AppWrites.cs`, take only a key the fake issued. `ForWrite` makes
one, random and an hour long, recorded for the account; a write with a key the account's record
does not hold for that repository and purpose, or one past its hour, is refused with `401`, "Bad
credentials", and a key for another purpose or repository throws, as the adapter does. Each write
is the app's, by the login `devflow-fake[bot]`, and recorded with its key's purpose. A pull request
is opened only between two branches its git repository holds, see below, with no open one already
between them, and otherwise refused with `422`; its head is the branch's commit and its number the
next after every pull request's, issue's and conversation's. A comment goes on a pull request, an
issue or a conversation, and a label on a pull request or an issue; removing a label it does not
carry is refused with `404`, "Label does not exist". An issue the app opens is kept in the repository's
`issues`, as a staged one is, by `devflow-fake[bot]`, open, blocked by nothing and never edited, so
listing issues by label answers it. A merge is the owner's merge, by the app, landing on the default
branch as `devflow-fake[bot]`'s.

Each repository has a bare git repository of its own, in a temporary folder deleted with the fake,
made empty, `main` its default branch, the first time `CloneAddress` is asked for it, which answers
its `file://` address. A worker in a test clones it, pushes to it and fetches from it with git, as
it would from GitHub; the address takes no key, so the fake does not check the push key. Merging
does not change it. The fake needs `git` on the machine only for this.

### Staging a world

In the fake mode only, under the `Api` policy and the origin check like every route, each acting on
the caller's own world, and left out of `contracts/devflow/openapi.json`, since they are not part
of what Dev flow is:

- `PUT /devflow/api/fake/world` replaces the caller's world with the document, `204`. One that does
  not parse is `400`, a problem document titled "The world is malformed" whose detail starts with
  the field, such as `installations[0].repositories[0].name: is missing`, and the world before it is
  kept.
- `GET /devflow/api/fake/world` reads it back, the same document; `{"installations": []}` before
  anything is staged.
- `POST /devflow/api/fake/unavailable` with `{"unavailable": true}` makes every operation for the
  caller answer unavailable, without a time to try again, and `false` makes it answer again. The
  world is kept.
- `POST /devflow/api/fake/expire-token` ends the caller's access token now, at the fake and in Dev
  flow's record of when it expires, so the next use of it renews, `204`. The refresh token still
  works.
- `POST /devflow/api/fake/revoke-token` ends both of the caller's tokens at the fake, as revoking
  the app on GitHub does, and the access token in Dev flow's record, so the next use renews, the
  renewal is refused and the link becomes `needs_relink`, `204`.
- `POST /devflow/api/fake/claude-login-age` with `{"days": 340}` moves when the caller's Claude
  login was given to that many days before now, so a test sees it due without waiting a year,
  `204`; `404` without a login, and `400` for a negative or malformed number.
- `POST /devflow/api/fake/end-wait` with `{"project_id"}` ends the caller's project's wait after
  failures outside its cards now, its count kept, so a test sees the next failure without waiting
  minutes, `204`; `404` for a project the caller does not have or that does not wait, and `400`
  without a `project_id`. See "Stops".
- `POST /devflow/api/fake/push` with `{"repository": 1001, "branch": "main", "message": "…"}`
  pushes one commit on the branch of the caller's repository with that id, made in the fake's git
  repository on top of where the branch is, and lists it among the branch's changes as a push by the
  person the world signs in as, so the watch finds it the owner's; it answers `{"commit": "<40
  hex>"}`, and `404` for a repository the world does not hold. How a test moves the default branch
  on under a feature, see "The end of a feature".
- `POST /devflow/api/fake/worker/run`, the fake worker, which takes a project's next job and ends
  it with a prepared result, see "Jobs".
- `GET /devflow/api/fake/writes` lists the writes the fake took for the caller, oldest first, each
  `{"kind": "merge", "repository": 1001, "number": 304, "commit": "<40 hex>", "method": "merge",
  "body": null, "by": "octo-owner"}`, or with `kind` `comment`, the `body`, and `commit` and
  `method` null. A write by the app has `by` `devflow-fake[bot]` and a `purpose`, `post`, `merge`
  or `push`, and may be of `kind` `pull_request`, with `title`, `head`, `base`, its head commit and
  its body; `pull_request_edited`, with its new body; `issue`, with `title` and `body`;
  `label_added` or `label_removed`, with `label`; `branch`, a branch made with a push key, `number`
  0, with `branch` and the `commit` it was made at, in the fake's git repository, the default branch
  given a first commit when it has none; or `branch_merge`, a branch brought into another with a
  merge key, `number` 0, with `head`, `base`, the merge `commit`, `method` `merge` and its message as
  `body`. A field
  that is null past `by` is left out, so the owner's writes read as before.

In the GitHub mode none of them exists, `404`. An acceptance test stages a world through the
`devflow_world` fixture, which puts it through the page's own session, with `e2e/devflow_worlds.py`
to build it; see "Fixtures" in `e2e/README.md`. A C# test calls `FakeCodeHost.Replace` and
`SetUnavailable`, or sends the same requests through the harness.

## Linking GitHub

The owner links his GitHub account once, on the Projects tab, and from then on Dev flow holds a
token that acts as him on GitHub, renewed by itself, which he can remove again. Why it acts as him,
and why it is safe, is "Acting as the owner" in `docs/devflow-architecture.md`. It is
`GitHubLink` and `UserTokens`, in `src/DevFlow/Link/`, over the two tables of
`migrations/0003_github_link.sql`, both guarded like every scoped table:

- `github_sign_ins`: a link started and not yet returned from, `user_id`, `state_hash`, the
  SHA-256 of the state and never the state, `verifier`, the PKCE verifier, and `created_at`, with
  the primary key `(user_id, state_hash)`;
- `github_accounts`: the one GitHub account a user has linked, `user_id` its primary key, `login`,
  `github_user_id`, `state`, `linked` or `needs_relink`, `access_expires_at`, `refresh_expires_at`
  and `linked_at`. The two tokens are in the vault, under the purposes `github.access` and
  `github.refresh`, and nowhere else.

**Starting.** `POST /devflow/api/github/link` makes a random state and a random PKCE verifier, 32
bytes each in base64url, stores the state's SHA-256 with the verifier and the time, removing the
user's own sign ins older than ten minutes on the way, and answers the code host's address with the
state and the verifier's S256 challenge. The redirect address is
`<DEVFLOW_PUBLIC_ORIGIN>/devflow/github/callback`, so it follows the origin a copy is served at.

**The return.** `GET /devflow/github/callback?code=&state=`, under the `Page` policy, so the person
must be logged in to the platform, and one who is not goes to refresh and comes back with the same
address, nothing spent. It deletes the state's sign in from this user's own, so it is spent whatever
follows, and refuses one that is missing, already spent, another user's or older than ten minutes,
a return carrying the code host's own `error`, and one without a code. Otherwise it exchanges the
code with the stored verifier, asks who the token belongs to, stores the account as `linked` with
both tokens in the vault, and answers `303` to `/devflow/projects`. Any refusal answers `303` to
`/devflow/projects?link=failed` and stores nothing.

**A usable token.** `UserTokens.For(userId)` is the one way to a token that acts as the user, which
every later use goes through. In one door transaction it locks the account's row with
`SELECT ... FOR UPDATE`, returns the access token while it has more than five minutes left, and
otherwise renews, stores both new tokens and their lifetimes, and returns the new one. A renewal
the host refuses, or a refresh token already past its end, sets the state to `needs_relink` and
deletes both tokens; a host that is unavailable changes nothing and is answered as unavailable;
with no account, or `needs_relink`, it answers that for the caller to show.

**Unlinking.** `DELETE /devflow/api/github/link` removes the account, its tokens and any sign ins.
Nothing is revoked at GitHub; "Registering the GitHub App" below says where the owner does that.

Nothing prints a code, a verifier, a state or a token. The request log, which carries each
address's query and so the return's code and state, is raised to `Warning` in `DevFlowApp.Build`,
and `LinkTests.NothingPrintsACodeAVerifierAStateOrAToken` reads every log line of a whole link and
a renewal for each of them.

## Connecting a repository

A project is one connected repository with its settings, and in this part it has almost none:
every project is read the way this repository's runner writes, and one without the runner's
labels shows nothing waiting. What it is for is "Projects" in `docs/devflow-architecture.md`.

**As the owner does it.** On the Projects tab, with GitHub linked, press "Connect a repository",
find the repository under the account it is on, and press "Connect": it is listed under the
tab's projects from then on. A repository that is not listed is one the app is not installed on,
or one the GitHub account linked cannot reach: install the app on it, through "Install it on
GitHub" or the app's page, see "Registering the GitHub App", and press "Connect a repository"
again. "Disconnect" removes the project and everything Dev flow read for it, after asking, and
changes nothing on GitHub.

It is `ProjectStore` and `ProjectEndpoints`, in `src/DevFlow/Projects/`, under the `Api` policy
and the origin check:

- `GET /devflow/api/github/repositories` answers
  `{"installations": [{"id", "account", "repositories": [{"id", "owner", "name", "connected"}]}], "install_url": "..."}`:
  every installation the caller's GitHub account can reach, read as them, sorted by account, its
  repositories by owner and name, each saying whether it is already one of their projects, and
  where to install the app. An installation gone between the two reads is left out.
- `POST /devflow/api/projects` with `{"installation_id", "repository_id"}` asks the code host, as
  the caller, for that installation's repositories, and only when the id is among them stores the
  project, with the owner and name the code host gave and never the request's, answering `201` with
  `{"id", "installation_id", "repository_id", "owner", "name", "created_at"}`. A repository it does
  not list there, or an installation the caller cannot reach, is `404`; one already a project of
  theirs is `409`; a field missing or not a positive number is `422` naming it. Nothing is stored
  for any of them.
- `GET /devflow/api/projects` answers `{"projects": [...]}`, each as above, by owner and name.
- `DELETE /devflow/api/projects/{id}` answers `204`, or `404` for a project the caller does not
  have, another user's among them.

The first two need a linked account: without one each is `409`, a problem document titled "GitHub
is not linked", and with a link GitHub stopped taking, "Your GitHub link has expired". When GitHub
does not answer, either is `503`, "GitHub did not answer", with `Retry-After` when GitHub said when
to try again. Listing projects and disconnecting need no link.

The two tables, from `migrations/0004_projects.sql`:

- `projects`, guarded like every scoped table: `id` its primary key, `user_id`, `code_host`, always
  `github`, `installation_id`, `repository_id`, `owner`, `name`, `bot_login`, the login whose pull
  requests the runner's reader looks at, `mac-mini-agent` by default and changed from no page yet,
  and `created_at`, unique on `(user_id, repository_id)`. Two users may connect the same
  repository, each as their own project. Everything later slices store for a project references
  its id with `ON DELETE CASCADE`, so disconnecting removes it.
- `poll_targets`: `project_id`, its primary key, and `user_id`, nothing else, and **no** row level
  security, so a loop with no logged in user can learn which projects exist and whose they are, and
  then works through the door as each one's user. A trigger on `projects`, with a
  `SECURITY DEFINER` function, `sync_poll_target`, owned by `devflow_owner`, adds a row after an
  insert, removes it after a delete and follows a changed id, so no code path can forget;
  `devflow_app` may only `SELECT` it. Why it is safe is "Security decisions and accepted risks" in
  `docs/devflow-architecture.md`, and `IsolationTests.EveryTableIsGuarded` names it as the one
  exception.

## What waits

Dev flow keeps a stored picture of what waits for the owner in every project, and the pages read
it and never the code host, so they are fast and still answer while GitHub is down. Why it is
built so is "Reading and the snapshot" in `docs/devflow-architecture.md`. It is `ProjectReader`,
`ProjectRefresher`, `Poller`, `WaitingStore`, `WaitingEndpoints` and `ScreenshotEndpoints`, in
`src/DevFlow/Reading/`.

**Reading a project.** `ProjectReader.Read(projectId, userId)` reads one project as the app,
through `ICodeHost`: its open pull requests in one list; then, only for those by the project's
`bot_login` that carry `owner-merge`, `owner-decision` or `owner-blocked`, their comments, their
head's statuses and check runs, and the comments of the slice issue their body closes; and passes
each to `RunnerReader.Read`, with the project's repository and the owner's login from his link in `github_accounts`, whatever
its state, or none without a link. A conversation, a commit or a slice that is not found is read as
empty. It then replaces the project's items in one transaction through the door as the user: an
item still there is updated and keeps its id, one gone is deleted, and the read is recorded as a
success. A read that fails, because GitHub did not answer, does not show the repository, or
anything throws, leaves the items as they were and records the time and a fixed phrase, "GitHub
did not answer", "GitHub does not show this repository" or "Reading failed", never the host's own
text.

**Asking for a read.** `ProjectRefresher.Request(projectId, userId)` is the one way to ask, and
answers a task that completes once a read that began after the request is done, whether it
succeeded or not. A project is read by one reader at a time; requests made while it is read share
one more read after it. The loop, the refresh route, merging and sending back, see "Acting from the
page", and a webhook, when the platform is reachable, all call it.

**The loop.** `Poller`, a hosted service, lists `poll_targets` as it starts and then every
`DEVFLOW_POLL_SECONDS`, through the door's `WithoutUser`, the one read outside a user, and requests
each project as its user, `Poller.ReadsAtOnce`, 4, at a time, so a tick holds at most that many
database connections for its reads however many projects there are. It reads as the app, so it goes on when the owner's link has expired,
and runs in the fake mode as well. With `DEVFLOW_POLL_SECONDS=0` it does not run.

**The routes**, under the `Api` policy and the origin check:

- `GET /devflow/api/waiting` answers
  `{"count", "needs_relink", "items": [...], "projects": [{"id", "owner", "name", "last_success_at", "stale", "failing"}], "own_items": [...], "cards": [...], "stopped_cards": [...], "refused_cards": [...], "failing_projects": [...], "decisions": [...], "features": [...], "question_cards": [...]}`.
  Each item is a stored row with its project's id and repository name: `id`, `project_id`,
  `project_name`, `number`, `kind`, `waiting` or `stopped`, `sent_back`, `title`, `web_url`,
  `slice_number`, null when the body closes none, `head_commit`, `can_merge`, `summary`, `reason`,
  `decisions`, each `{"chosen", "alternative", "why"}`, `other_decisions`, `reviewer_view`,
  `screenshots` and `read_at`, by project and number. `own_items` are Dev flow's own, read from its
  own tables beside the runner's, by project; in this part each is a stopped project, see
  "Watching branches". `screenshots` are the stored names paired for
  the page by `Screenshots.Group`, each `{"name", "images"}`: the part of the names before
  `-desktop` and `-phone`, and the names, the desktop one first; what each means is `RunnerItem` in
  `src/DevFlow/Runner/RunnerItem.cs`, and every text is plain text to show as text. `cards` are Dev
  flow's own cards whose pull requests wait for the owner, by project and number, see "The pull
  request Dev flow opens". `stopped_cards` are its own cards that stopped and `failing_projects` its
  projects that cannot run their cards, each by project, see "Stops". `refused_cards` are its cards
  refused as too thin to build, by project and number, see "Reading a card" under "Jobs", and
  `question_cards` its cards that wait for the owner's answers to their reading's questions, by
  project and number, see the same. `decisions` are the issues Dev
  flow opened for decisions marked for the owner on cards it merged, see "The merge check", and
  `features` its features ready for the owner's review or waiting for him, see "Reviewing a
  feature". `count` is the items not sent
  back, the own items, the cards, the stopped and refused cards, the failing projects, the decisions, the features and the
  cards waiting for answers, plus one when
  `needs_relink`, the caller's link needing renewal. A project is
  `stale` when its last success was more than five minutes ago or it was never read, and `failing`
  when its last read failed.
- `POST /devflow/api/projects/{id}/refresh` requests the caller's project and answers `204` once it
  is read, or `202` after ten seconds if it is not, while the read goes on. Whether it failed is in
  the list. Another user's project, or none, is `404`.
- `GET /devflow/api/waiting/{id}/screenshots/{name}` answers one of the item's screenshots, read
  when it is asked for and never stored. In order: another user's item, or none, is `404`; a
  `name` that is not one of the item's stored `screenshots` is `404`, without asking GitHub; then
  `ICodeHost.File` reads `<number>/<name>.png` on the branch `pr-screenshots` of the item's own
  repository, as the app, the path built here and never taken from the body. What it finds is
  served only when it begins with the PNG signature and is at most 5 MB, always as `image/png`,
  with `Cache-Control: private, max-age=3600`; anything else it finds is `502`, a file not there
  `404`, and GitHub not answering `503`, with `Retry-After` when GitHub said when. Every answer
  carries `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'`, and
  a refusal `Cache-Control: no-store`. Why it is built so is "A code host's bytes handed to the
  browser" in `docs/devflow-architecture.md`.

The two tables, from `migrations/0005_waiting_items.sql`, both guarded like every scoped table:

- `waiting_items`: `id`, `user_id`, `project_id` referencing `projects` with `ON DELETE CASCADE`,
  and the item's fields above, `decisions` and `reviewer_view` as `jsonb` arrays, unique on
  `(project_id, number)`; and `screenshots`, from `migrations/0006_waiting_screenshots.sql`, the
  names `RunnerReader` accepted from the body, a `jsonb` array of strings, empty by default;
- `project_reads`: `project_id`, its primary key with the same reference, `user_id`,
  `last_attempt_at`, `last_success_at`, null before a read succeeds, and `last_error`, the phrase,
  null after a success.

## Cards

A project's settings, and the cards each read of it picks. Why they are built so is "Cards" in
`docs/devflow-architecture.md`. It is `ProjectSettings.cs`, `TrustedAccounts.cs` and
`ProjectEndpoints.cs` in `src/DevFlow/Projects/`, and `CardPicker`, `CardStore` and `CardEndpoints`
in `src/DevFlow/Cards/`.

### Settings

Eighteen, and only the eighteen something reads, with the preset they match, see "Presets" below:

| Setting | Default | What it does |
|---|---|---|
| `pickup_label` | `devflow` | An open issue carrying it is a card |
| `hold_label` | `devflow-hold` | A card carrying it is held |
| `attention_label` | `devflow-attention` | The label of the issue Dev flow opens for a decision marked for the owner on a card it merged, see "The merge check"; part 3's "Stops" marks a stopped card with it too |
| `review_label` | `devflow-review` | A card carrying it, or every card of a feature whose parent carries it, is marked for the owner's review, which `sometimes` waits for when `wait_when_marked` is ticked |
| `waits_for_owner` | `always` | `never`, `sometimes` or `always`: with `never` the merge check merges an approved card when every condition holds; with `sometimes` it does too, unless a ticked condition below holds; with `always` it waits for the owner, "This project waits for you on every pull request", see "The merge check" |
| `wait_when_marked` | ticked | On `sometimes`, a card marked for the owner's review waits |
| `wait_when_paths` | ticked | On `sometimes`, a change touching a path on `wait_paths` waits |
| `wait_when_decision` | ticked | On `sometimes`, a card whose agent marked a decision for the owner in any build's or fix's report waits |
| `wait_paths` | none | The paths the owner wants to see, glob patterns, see "Paths" below |
| `trusted_logins` | the owner's linked login alone | The GitHub logins whose cards, edits and comments are instructions, see "Trusted accounts" below |
| `model_reading` | `opus` | The model the reading job runs on, see "Reading a card" under "Jobs" |
| `model_building` | `opus` | The model of a build, a fix, a conflict and a feature's fix, see "Jobs" |
| `model_reviewing` | `opus` | The model of a card's review and a feature's review |
| `run_minutes` | 90 | The longest a run may take, 10 to 240; the worker ends it then, and a run is lost once it was handed out its minutes and 10 more ago, see "Jobs" |
| `run_turns` | 200 | The most turns a run may take, 20 to 1000, which Claude Code enforces |
| `fix_rounds_card` | 3 | The fix rounds a card's reviews may start, 0 to 10; a `changes` verdict after them stops the card, see "Fixing" |
| `fix_rounds_feature` | 3 | The fix rounds a whole feature's reviews may start, 0 to 10; a `changes` verdict after them has it wait for the owner, see "Fixing a feature" |
| `questions` | true | Questions before starting: on, a card its reading found questions in is `needs_answers` and waits for the owner's answers under "Questions before starting" on "Waiting for you"; off, it is built on the reader's recommendations, which the app posts on it. See "Reading a card" under "Jobs" |

A label is written, at most 50 characters, GitHub's limit, with no space at either end, no comma,
since GitHub's list of issues by label takes a comma separated list, and no control character; and
the four are different names whatever their case, as GitHub's label names are. The three
conditions are read only on `sometimes`, where at least one is ticked, since `sometimes` with none
would wait for nothing. A model is `opus`, `sonnet` or `haiku`, exactly: Claude Code's aliases,
which follow the newest model of each family, so a new model needs no change here, where a pinned
model id would need a release for each. `RunSettings` in `src/DevFlow/Projects/` holds the values
and the ranges.

**A changed setting takes effect at the next decision.** Each job carries the model and the limits
read when it was handed out, and its run keeps them while it runs, so a run under way is never
shortened or moved to another model; the next job, the next verdict, a feature's next readiness and
the next search for lost runs read the settings fresh, the search judging each run by the minutes
its own job carried, which the run records. The page's "Round n of m" lines count against
the setting as it is now.

**Presets.** Three shortcuts, never stored: a project's preset is the one its settings match, which
`Presets.Of` in `src/DevFlow/Projects/Presets.cs` answers, and the panel works out the same way
from the form as it is typed. Each covers three settings, `questions`, `waits_for_owner` and the
three conditions of `sometimes`, and nothing else: labels, trusted accounts, paths, models and
limits are outside every preset and never change which one matches.

| Preset | `preset` | `questions` | `waits_for_owner` |
|---|---|---|---|
| Fully automatic | `fully_automatic` | off | `never` |
| Guided | `guided` | off | `sometimes`, all three conditions ticked |
| Only I merge | `only_i_merge` | on | `always` |

On `never` and `always` the conditions are not read and do not count for matching; any other
combination is `custom`. A new project starts as "Only I merge", and
`migrations/0041_presets.sql` made every project there was the same. Choosing a preset in the panel
fills in what it covers and leaves every other field as typed, Guided ticking all three
conditions and the other two leaving them as they are; it is saved with "Save" like any change. Why
presets are not stored is "Projects" in `docs/devflow-architecture.md`.

**Paths.** `wait_paths` is a list of glob patterns, as `Microsoft.Extensions.FileSystemGlobbing`'s
`Matcher` reads them, each from the repository's top: `*` within one folder, so `*.sql` is a file
at the top and `**/*.sql` one anywhere; `**` across folders, as in `src/auth/**`; and a pattern
ending in `/` meaning everything under that folder, as `.github/` does. A path is matched whatever
its case. There is no negation. A pattern is written, at most 200 characters, with no space at
either end and no control character, does not start with `!` or `/`, separates folders with `/`,
never `\`, and holds no empty, `.` or `..` folder; the list holds at most 100, and a pattern given
twice is kept once, in its first place. `PathPatterns` in `src/DevFlow/Merging/` holds the rules and
the matching.

**Trusted accounts.** `trusted_logins` lists the GitHub logins whose words count: who may write a
card or a feature's parent, who may edit their body or title, and whose comment is input, wherever
one is read, sending a card back, running a stopped card again, fixing a card, and fixing and
sending back a feature; and the merge check reads it, see "The merge check". Everywhere else is
data. A trusted account gives instructions and never makes Dev flow act as it: Dev flow acts on
GitHub as the owner only with his linked token. Logins are compared whatever their case, as
GitHub's are. The owner's linked login is always on the list and is not stored: it is added whenever
the list is read, so a relinked account is trusted under its new login at once and the old one is
not; without a link nobody is known to be him, and nothing is trusted. A login is 1 to 39 ASCII
letters, digits and single hyphens, with no hyphen at either end, GitHub's rule. Who changes a label
is read by the list too, see "Cards" below.

- `GET /devflow/api/projects/{id}/settings` answers the eighteen and `preset`,
  `{"pickup_label", "hold_label", "attention_label", "review_label", "waits_for_owner",
  "wait_when_marked", "wait_when_paths", "wait_when_decision", "wait_paths", "trusted_logins",
  "model_reading", "model_building", "model_reviewing", "run_minutes", "run_turns",
  "fix_rounds_card", "fix_rounds_feature", "questions"}`, the three conditions and `questions` booleans, `wait_paths` a list of
  patterns in their order, `trusted_logins` a list of `{"login", "mine"}`, the caller's linked login
  first with `mine` true, then the others in the order they were given, the four limits whole
  numbers, and `preset` one of `fully_automatic`, `guided`, `only_i_merge` and `custom`, computed
  and never stored. Another user's project, or none, is `404`.
- `PUT /devflow/api/projects/{id}/settings` takes all eighteen, `trusted_logins` as a list of logins,
  and answers them as saved, with the preset they match. A `preset` sent with them is not read. One that breaks a rule, or is missing, is `422`, a validation problem
  whose `errors` names each field, and nothing changes: a label that is another's whatever its case,
  named on the later of the two, `sometimes` with no condition ticked, under `waits_for_owner`, a
  pattern that breaks a rule, under `wait_paths` with the first one named, a model outside the three
  or a limit outside its range, each under its own name, `trusted_logins` without
  the caller's linked login, whatever its case, or with a login that is not GitHub's shape, among
  them. A login given twice whatever its case is kept once, in its first spelling, and the caller's
  own is never stored. Another user's project, or none, is `404`. From another origin, `403`.

The table, from `migrations/0010_project_settings.sql`, guarded like every scoped table:
`project_settings`, `project_id`, its primary key, and `user_id`, referencing `projects` with
`ON DELETE CASCADE` and `ON UPDATE CASCADE`, and the four columns with their defaults and checks;
`trusted_logins`, a `text[]` of the others, empty by default and never holding a null, from
`migrations/0035_trusted_logins.sql`, whose login rules are the route's; and from
`migrations/0036_sometimes.sql`, `waits_for_owner` taking `sometimes`, with a check that it has a
condition ticked, `wait_when_marked`, `wait_when_paths` and `wait_when_decision`, `boolean`, true by
default, `review_label`, `text`, `devflow-review` by default, held to the other labels' checks and
different from each whatever the case, and `wait_paths`, a `text[]`, empty by default and never
holding a null, whose pattern rules are the route's. The migration gives a project that already
named a label `devflow-review` the review label `devflow-review-1`. From
`migrations/0037_models_and_limits.sql`, the three models, `text`, `opus` by default, checked to be
one of the three, and the four limits, `int`, with the defaults and ranges above as checks; the
migration also raises the checks on the rounds a card, a feature and a run record from 3 to 10, and
gives each run its `run_minutes`, see "Jobs". From `migrations/0040_questions.sql`, `questions`,
`boolean`, true by default, which makes it true for every project there was. From
`migrations/0041_presets.sql`, `waits_for_owner` is `always` by default, and every project there
was was set to `questions` true and `always`, "Only I merge", its three conditions kept; the
policy is forced, so the migration lifts it for that one update and forces it again. A
trigger on `projects`
makes a project's row when the project is made, running as the caller, and the migration made one
for every project before it.

### Cards

Each read of a project, `ProjectReader.Read`, picks its cards after its waiting items, see "Reading
a project" under "What waits", and stores both in one transaction:

- `ICodeHost.LabelledIssues` lists the open issues carrying `pickup_label`; a pull request among them
  is never a card.
- A card no trusted account wrote, or any card while the owner has no link, is `ignored`;
  otherwise `ICodeHost.Edits` reads who edited its body and title, and one a login off the list
  edited is `ignored`. Then `ICodeHost.LabelEvents` reads its timeline, and `LabelActors.Judge`
  reads its labels by who changed them: a labelled issue, a card on its own or a feature's parent,
  whose `pickup_label` no trusted account put on is `ignored`. A card carrying `hold_label`, whoever
  put it on, is `held`, and so is one whose `hold_label` a trusted account put on and only someone
  untrusted took off since. A card of a feature reads its timeline only when no hold on it or its feature
  decided first. Otherwise `ICodeHost.BlockedBy` reads what it is blocked by, and it is `queued`,
  waiting for what of that is still open. A timeline that cannot be read fails the read, as any
  other read does.
- Its priority is `high` with the label `priority: high`, `low` with `priority: low`, and `none`
  otherwise.
- `CardStore.Store` merges them into the project's cards: a new card is inserted, one that has not
  started is updated, one that has started keeps its state unless it is now ignored, and one no
  longer listed is deleted, unless its run is under way, when it is marked `unlabelled`, or it is
  `done` with a pull request, when it is kept, since its pull request's `Closes` line closed its
  issue as it merged and the card is its story.
- A card that has not started, `queued`, `needs_answers` or `refused`, that a trusted account edits
  or comments on after it was read is read again before it is handed out, and one waiting for the
  owner is queued for it; once it has started nothing reads it again. See "It is read again" under
  "Reading a card" in "Jobs".

The reasons, as stored and shown: "Dev flow does not know your GitHub login: link GitHub on the
Projects tab", "<login> is not a trusted account of this project", "Edited by <login>, who is not a
trusted account of this project", "The pick-up label <pick-up label> was added by <login>, who is not
a trusted account of this project", naming who put it on last, or "The pick-up label <pick-up label>
was not added by a trusted account of this project" when its timeline does not say, "Held by the
label <hold label>", "Held by the label <hold label>, taken off by <login>, who is not a trusted
account of this project", and "Waits for #12", "Waits for
#12 and #14" or "Waits for owner/name#3"; null when a card is queued with nothing to wait for.

`CardStore.Queue(projectId)` answers the numbers of the queued cards with nothing to wait for, in
the order they are handed out: high, none, low, each oldest first.

- `GET /devflow/api/projects/{id}/cards` answers `{"features": [...], "cards": [...]}`: the features,
  see "Features" below, and every card on its own as last read, newest change first: `number`,
  `title`, `web_url`, `state`, `reason`, `waits_for`, `priority` and `changed_at`. A feature's cards
  are listed in it and not among `cards`. It reads what is stored, never GitHub. Another user's
  project, or none, is `404`.

The table, from `migrations/0011_cards.sql`, guarded like every scoped table: `cards`, `id`,
`project_id` and `user_id` referencing `projects` as `project_settings` does, `number`, unique per
project, `title`, `web_url`, `state`, one of `queued`, `reading`, `needs_answers`, `building`, `checking`, `reviewing`, `fixing`,
`waiting_for_owner`, `done`, `stopped`, `refused`, `held` and `ignored`, `needs_answers` and `refused` the owner's
turn: one waits for his answers to its reading's questions, "Waits for your answers to its
questions", and the other, read as too thin to build, for his edit or comment, see "Reading a card"
under "Jobs", `reason`, `waits_for`, a `text[]`,
`priority`, `issue_created_at`, the record of what was picked, `picked_body_sha256`, `picked_title`
and `picked_edit`, null for a card never picked, `unlabelled`, `changed_at`, when its state, reason or
what it waits for last changed, and `read_at`. `migrations/0012_runs.sql` adds `picked_body`, the body
as it was picked, which a job's brief quotes; null for a card never picked and for one picked
before the column, until its next read, and such a card is not handed out.
`migrations/0014_card_pull_requests.sql` adds `pull_request_number` and `pull_request_url`, the
card's pull request, both or neither, see "The pull request Dev flow opens".
`migrations/0024_features.sql` adds `feature_id` and `position`, see "Features" below.

### Features

A feature is an open issue carrying `pickup_label` that has at least one sub-issue, and its cards
are its direct sub-issues in the same repository. Why it is built so is "Features and their cards"
in `docs/devflow-architecture.md`. Each read picks features beside cards, in `CardPicker`, and
stores them in the same transaction, in `CardStore`:

- `ICodeHost.SubIssues` reads each labelled issue's sub-issues, as the app, a hundred a page,
  `GET /repos/{owner}/{repo}/issues/{number}/sub_issues`; one that cannot be read makes the read
  unavailable, as any read does. A labelled issue with none is a card as above.
- The parent is checked as a card is, by everything but what blocks it: not linked, another
  author, another editor or a pick-up label no trusted account put on is `ignored`, with the card's
  reasons, and the hold label, on it or taken off only by someone untrusted, is `held`, with the
  card's reasons. Otherwise `queued`, with its record of what was picked, as a card's.
- Each direct sub-issue in this repository, a pull request left out, is a card of the feature, at
  `position` 1, 2, 3 in GitHub's sub-issue order, and is checked by the card rules with no pick-up
  label of its own, its own hold label read as a card's. With the parent `ignored`, every one is `ignored`, "Its feature, #<parent>, is not
  picked", read no further; with the parent `held`, every one the owner's is `held`, "The feature is
  held by the label <hold label>". Its priority is the feature's; its own priority label is not read.
  A sub-issue carrying the pick-up label too is still one card, of its feature.
- A closed sub-issue is a card only when it has started, since Dev flow built it; it stays where
  its run put it. Any other closed one is left out.
- A sub-issue in another repository is not stored, since a card is a number in the project's
  repository; while the feature is queued its reason names them, "Only a feature's direct
  sub-issues in this repository are its cards: owner/name#5 is not one". A sub-issue of a
  sub-issue, known when the sub-issue in the middle carries the label, is `ignored` on its own with
  "Only a feature's direct sub-issues in this repository are its cards".
- A labelled issue that has started as a card on its own stays one when sub-issues are added: it
  is no feature, and they are `ignored` on their own, "Its parent, #<n>, is being built as a card".
- Taking the label off the parent removes the cards of it that have not started; one under way is
  marked `unlabelled` and finishes, as a card's own label does. The feature is marked `unlabelled`
  while any card refers to it, and is deleted once none does.
- A feature's cards are handed out one at a time, in position order, on the feature's branch, see
  "Jobs"; `CardStore.Queue` lists only cards on their own.

The route above answers each feature as `number`, `title`, `web_url`, `state`, `reason`,
`priority`, `changed_at` and `cards`, its cards in position order, each as a card on its own is;
features in the order their cards would be taken, by the parent's priority and then the oldest.

The table, from `migrations/0024_features.sql`, guarded like every scoped table: `features`, `id`,
`project_id` and `user_id` referencing `projects` as `cards` does, `number`, unique per project,
`title`, `web_url`, `state`, one of `queued`, `building`, `bringing_in`, `resolving`, `reviewing`,
`fixing`, `waiting_for_owner`, `ready`, `merged`, `closed`, `held` and `ignored`, of which picking
sets only `queued`, `held` and `ignored` and the later slices of part 4 the others, `reason`,
`priority`, `issue_created_at`, the record of what was picked, `picked_body_sha256`, `picked_title`,
`picked_edit` and `picked_body`, null for a feature never picked, `unlabelled`, and, null until the
later slices set them, `branch`, `branch_base_commit`, `brought_in_commit` and
`pull_request_number`, with `pull_request_url` beside it from `migrations/0027_feature_page.sql`,
both or neither, and `bring_in_failed_at` from `migrations/0029_feature_end.sql`, see "The end of
a feature", and `ci_passed_commit` and `outcome_told_at` from `migrations/0034_feature_merge.sql`,
see "Merging a feature"; `fix_round`, 0 to 3, 0 until then; `changed_at`, when its state or reason
last changed, and `read_at`. `merged` and `closed` are set by a read that finds the feature's pull
request merged or closed, see "Merging a feature". On `cards`, `feature_id` refers to a feature of the same project, null
for a card on its own, and `position`, from 1, is set exactly when `feature_id` is, unique per
feature at the end of each transaction.

## Acting from the page

From the waiting list the owner merges a pull request or sends it back with a comment, and GitHub
records both as done by him, since both are made with his own linked account's token, from
`UserTokens.For`, and never as the app. The runner then picks his comment up as it picks up one
written on GitHub. Why it is built so, and what it never does, is "Acting from the page" in
`docs/devflow-architecture.md`. It is `ActionStore` and `ActionEndpoints`, in `src/DevFlow/Acting/`,
under the `Api` policy and the origin check. Every refusal is a problem document, its `title` the
line the page shows and its `detail` what follows it.

**`POST /devflow/api/waiting/{id}/merge`**, with `{"head_commit": "<the commit the page showed>"}`,
checks, in this order, and answers the first that fails:

| Answer | Title | When |
|---|---|---|
| `404` | No such item | The caller has no item with that id, another user's included. |
| `422` | The request breaks a rule | `head_commit` is missing or empty. |
| `409` | This pull request is stopped | Its kind is `stopped`: the runner says do not merge it as it is. A comment runs it again. |
| `409` | This pull request was sent back | The owner has commented since the latest verdict, so the runner has it. |
| `409` | The checks are not green on this commit yet | `can_merge` is false. |
| `409` | The pull request has changed since this was shown | `head_commit` is not the stored head. The project is read again first. |
| `409` | GitHub is not linked | The caller has no link. |
| `409` | Your GitHub link has expired | The link is `needs_relink`, or renewing it was refused. |
| `503` | GitHub did not answer | The token needed renewing and GitHub did not answer; nothing was sent. |

Only then is GitHub asked to merge that commit, with a merge commit. Merged, the project is read
again and the answer is `204`. GitHub saying the head moved is the same `409` "The pull request has
changed since this was shown", after the project is read again. Not mergeable is `409` "GitHub
would not merge it", with GitHub's words in the detail; any other refusal `409` "GitHub refused the
merge", with its status and words; no answer `503` "GitHub did not answer", after the project is
read again, since it may have merged. Merging an item with decisions confirms them, as merging on
GitHub does, and nothing else is recorded.

**`POST /devflow/api/waiting/{id}/send-back`**, with `{"comment": "<text>"}`, for a waiting or a
stopped item: `404` "No such item" as above; `422` when `comment` is missing, empty once trimmed,
or longer than 10,000 characters once trimmed; the same two `409`s and the `503` for the link; then
the comment is posted, trimmed and with nothing added. Posted, the item's `sent_back` is set at
once, the project is read again and the answer is `204`. GitHub refusing is `409` "GitHub refused
the comment", with its status and words; no answer `503`, after the project is read again.

**`POST /devflow/api/projects/{id}/cards/{number}/merge`**, with
`{"head_commit": "<the commit the page showed>"}`, merges a card of Dev flow's own whose pull request
waits, from "Waiting for you", as the caller with his linked token and never as the app, through
the same refusals as an item's where they apply, in this order: `404` "No such card" for a project
or card the caller does not have; `422` without `head_commit`; `409` "This card is not waiting for
you" unless it is `waiting_for_owner` with a pull request; `409` "This project is stopped", since
nothing a stopped project produced is merged until the owner resumes it, see "Watching the default
branch"; `409` "The pull request has changed since this was shown" when `head_commit` is not the
commit its latest built run checked, after the project is read again; the two `409`s and the `503`
for the link; then GitHub is asked to merge that commit, with a merge commit, and answers as an
item's merge does. Merged, the project is read again, which makes the card `done`.

**`POST /devflow/api/projects/{id}/features/{number}/merge`**, with
`{"head_commit": "<the commit the page showed>"}`, merges a feature into the default branch, from its
page or its item under "Features ready for your review", as the caller with his linked token and
never as the app, in this order: `404` "No such feature" for a project or feature the caller does
not have; `422` without `head_commit`; `409` "This feature is not waiting for you" unless it is
`ready` or `waiting_for_owner` with its pull request recorded; `409` "This project is stopped";
`409` "CI has not passed on this commit" unless `ci_passed_commit` is the head its branch's watch
names; `409` "The pull request has changed since this was shown" when `head_commit` is not that
head, after the project is read again; the two `409`s and the `503` for the link; then GitHub is
asked to merge the feature's pull request at that commit, with a merge commit, and answers as an
item's merge does. Merged, the project is read again, which makes the feature `merged`. The review's
verdict refuses nothing; the confirmation says it, see "Merging a feature".

**`GET /devflow/api/projects/{id}/features/{number}/merge`** answers what the dialog shows before
that merge, read from what Dev flow stored: `number`, `title`, `into`, the default branch,
`pull_request`, `{"number", "url"}`, `head_commit`, `review`, `open_findings` and `unanswered`, see
"Merging a feature". It refuses as the merge does, `404` and the three `409`s before the head's.

Reading again goes through `ProjectRefresher` and waits for the read as the refresh route does, at
most ten seconds, so the list the page reads next is current. After either, whatever it answered,
the page reads the list again and dispatches `platform-nav-attention` on `window`, so the nav bar
asks `GET /devflow/api/attention` again and its dot follows; a refusal can change the answer too,
an expired link among them. It is `askNavAgain` in `web/src/nav-attention.ts`. Neither route
writes a label, a status or anything else the runner owns: after a comment, the runner sees the
owner's input on its next turn and moves the label itself.

## Watching branches

Every read of a project watches its default branch first, then each open feature's branch, so
anything that lands on one of them that Dev flow did not put there, and the owner did not put there
himself, stops the project until he has looked. Why, and what it guards, is "Watching the default
branch" in `docs/devflow-architecture.md`, with "A feature's branch" under it. It is `BranchWatch`,
`WatchStore` and `WatchEndpoints`, in `src/DevFlow/Watching/`, `AppMerges`, in
`src/DevFlow/Merging/`, and `RunStore.Record`, in `src/DevFlow/Jobs/`, for a run's own push.

**Reading.** `ProjectReader.Read` calls `BranchWatch.Watch` before anything else, through the same
seam as every read, `ProjectRefresher`, so the loop watches every project about once a minute. It
asks `ICodeHost.DefaultBranch` for the branch, never assuming `main`, and `ICodeHost.Activity` for
what changed it, both as the app with the read only token and both conditional requests. The first
read of a project, or the first after its default branch changed, records the newest change and
judges nothing; every read after judges what is newer than the last change judged. Then each open
feature whose branch is made, every state but `merged` and `closed`, has its branch's changes read
the same way, each on its own. A card's own branch is never watched. A read that fails records
`last_attempt_at` and a fixed phrase, "GitHub did not answer", "GitHub does not show this
repository" or "Reading failed", on that branch's watch alone, and changes nothing else; it never
fails the next branch's read or the rest of the project's.

**A feature's watch begins** when its branch is made: `FeatureBranchStore.Made` writes the
feature's `branch_watches` row in the transaction that records the branch, read as of then, with no
change judged. Its first read lists every change the branch has and begins at the oldest
`branch_creation` that made it at the commit the feature recorded, `branch_base_commit`, judging
nothing before it, such as an earlier branch of the same name. When no such creation is listed,
every change listed is judged. Once its feature is `merged` or `closed`, its branch is not read
again.

**Judging.** On the default branch, a change is fine when it is not a force push and its actor is
the owner's linked login, from `github_accounts`, compared whatever its case, as GitHub compares
logins; or when it is a merged pull request whose merge commit is in `merges_made` for that branch.
On a feature's branch the same, and a push that left the branch at a commit in `pushes_made` for
that branch, or at a commit in `merges_made` for that branch, the default branch brought in, which
GitHub lists as a push, see "The end of a feature"; but its deletion is never fine, by anyone. Anything else stops the project: a push or
a merge by the app that Dev flow did not record, a force push by anyone, the owner included, a
change by any other login, one whose actor GitHub no longer names, and a feature's branch deleted.
The project's `project_stops` row records when it stopped and the newest change that stopped it,
its branch, type, actor, commit and when it landed. While it is stopped the watch goes on reading:
a later change on the same branch that would stop it replaces what the row shows, keeping when it
stopped, so the owner always looks at the newest; one on another branch is not judged yet, and the
watch of that branch stays before it, so it is judged, and stops the project again, on the first
read after he resumes.

**A run's own push.** A push by the app on a feature's branch, read while a run of Dev flow's on
that branch is under way, is not judged yet: it is kept in `pushes_awaited` with the run. The run's
result, in `RunStore.Record`, writes the commit it names to `pushes_made` with the branch and the
run, and judges every awaited push whose run has ended, in the same transaction: fine when its
commit is in `pushes_made`, a stop when it is not. A run found lost has no result, so its awaited
pushes stop the project when `RunStore.SweepLost` ends it, and each read of a feature's branch
judges any left. Both take the project's row first, as the watch and `AppMerges` do. Who the app
is comes from `ICodeHost.AppCommitter`, asked only when such a push is listed while a run is under
way. Any other change is judged as soon as it is read. No job of this part pushes to a feature's
branch: resolving a conflict and fixing a feature, in later slices, are the runs this is for.

**Dev flow's own merges.** `AppMerges.Merge(user, project, number, commit)` merges a pull request
as the app, with a `Merge` key, while its head is `commit`, into the default branch as its watch
names it, and writes the merge commit `MergeAsApp` answered to `merges_made`, with that branch, in
the same transaction. `AppMerges.MergeCard` merges a card's pull request into the base its newest
build or fix ran on, the default branch or its feature's, and records that branch. Each holds the
project's row with `FOR NO KEY UPDATE` from before the merge until the row is written, and the
watch takes the same lock before it judges, so the watch never judges Dev flow's merge before it is
recorded. Each refuses a stopped project before GitHub is asked, and a merge into an open
feature's branch not read for five minutes, `FeatureUnwatched`, which the merge check records on
the card as "Its feature's branch has not been read lately" and tries again; see "The merge
check".

**Holding work.** `WatchStore.Hold(user, project)` says why a project hands out no new work:
`Stopped`, while it has a stop, then `Unwatched`, when its default branch's watch has not read the
branch for five minutes, or never has; otherwise `None`. `jobs/next` answers `204` to every worker
of a held project: the `Dispatcher` reads the hold before it asks GitHub anything, and again with
`WatchStore.HoldUnderLock` in the transaction that takes the card, after taking the project's row
`FOR SHARE`, so a stop recorded meanwhile still holds the card back. The workers' route answers it
as `held`, `stopped` or `unwatched`, ahead of the Claude login's reasons, and the project's page
says why under Workers. An unwatched project is not marked stopped, and hands out work again as
soon as a read succeeds. A feature's branch not read for five minutes holds only that feature:
`WatchStore.FeatureWatchedSql` keeps its cards from every job, build, review and fix, while the
project's other cards are handed out. A card already with a worker is left as it is, so its run
finishes and its result is recorded; nothing it produced is merged while the project is stopped,
since `AppMerges` refuses.

**On "Waiting for you".** A stopped project is one of Dev flow's own items, `own_items` in
`GET /devflow/api/waiting`, of kind `project_stopped`, with its project's id, `project_name` and
`owner`, the `branch`, `activity_type`, `actor`, `commit`, `commit_url`, the commit's page on
GitHub built from the project's owner and name, `landed_at` and `stopped_at`. It is counted in the
list's `count`, lights the nav's dot, and is counted in the display data's `stopped`. The page shows
it under "Stopped", before the runner's, as "Something landed on <branch> that Dev flow did not put
there", with the commit's first seven characters, who did it, when, and "Open the commit on GitHub"
when the address is `https`; a deleted branch shows the branch instead, and no commit or link.
"I have looked, resume" asks first, in a dialog that says resuming accepts what landed.

**The route**, under the `Api` policy and the origin check, so only the project's own user, from
the platform's page, resumes it:

- `POST /devflow/api/projects/{id}/resume` with `{"commit": "<the commit the page showed>"}`. In
  order: `404` for a project the caller does not have, another user's among them; `422` when
  `commit` is missing or is not a full commit id; `409` "This project is not stopped"; `409`
  "Something else has landed since this was shown" when it is stopped by another commit, so what
  landed after the page was shown is never accepted unseen. `204` once resumed: the stop is copied
  to `project_resumes` with who resumed it, when and the commit accepted, and removed, in one
  transaction under the watch's lock. A stop on a feature's branch is resumed the same way.

The six tables, from `migrations/0013_main_watch.sql` and `migrations/0025_feature_watches.sql`,
each guarded like every scoped table and following its project:

- `branch_watches`: one row per watched branch, unique on `(project_id, feature_id)` with nulls
  not distinct: `project_id`, `user_id`, `feature_id`, null on the default branch's row and the
  feature's id on a feature's, following the feature, `branch`, `last_activity_id`, the code host's
  id of the newest change judged, null when the branch had none when the watch began or a
  feature's watch has judged nothing yet, `last_commit`, the commit the branch was at after the
  newest change judged, fine or not, and for a feature's branch the one it was made at until then,
  null for a watch that began before the column until its next judged change, which a feature's
  page shows, `began_at`, `last_read_at`, when the read that last succeeded listed the branch's
  changes, taken before the code host was asked and never moved back, so a change that landed
  while a read was under way is never counted as read, `last_attempt_at` and `last_error`.
- `merges_made`: `project_id`, `user_id`, `branch`, the branch it merged into, `commit`, `number`
  and `merged_at`, its primary key `(project_id, commit)`.
- `pushes_made`: `project_id`, `user_id`, `branch`, `commit`, `run_id`, the run whose result named
  it, and `recorded_at`, its primary key `(project_id, branch, commit)`.
- `pushes_awaited`: `project_id`, `user_id`, `activity_id`, `branch`, `run_id`, the run under way
  when it was read, `actor`, `commit`, `landed_at` and `read_at`, its primary key
  `(project_id, activity_id)`; a row until its run has ended and it is judged.
- `project_stops`: `project_id`, its primary key, `user_id`, `stopped_at`, `branch`, the branch
  that stopped it, `activity_id`, `activity_type`, `actor`, `commit` and `landed_at`; a row while
  the project is stopped.
- `project_resumes`: `id`, `project_id`, `user_id`, `resumed_by`, `resumed_at`,
  `accepted_commit`, and the stop as it was: `branch`, `activity_type`, `actor`, `stopped_at` and
  `landed_at`. Kept, so a later reader sees who accepted what.

## Workers

A worker is a program in a container on one of the owner's machines that builds a project's cards.
Every worker belongs to one project, and so to that project's user, and calls Dev flow; Dev flow
never calls it. A project has a key and a worker its calls, which the worker program, "The worker"
below, makes; the image it runs in and the commands that start it are "Starting a worker". Why it
is built so is "Workers" in `docs/devflow-architecture.md`.

**As the owner does it.** On the Projects tab, open a project by its name, and in its Workers panel
press "Make a key". Copy the key then, or the command under it that saves it: it is shown once and
never again. A worker started with it, as "Starting a worker" says, says hello and is listed under
the panel, with a notice when it needs building again. "Make a new key" replaces it, and every worker holding
the old one is refused from then on.

**The key.** One per project, shared by every worker of that project: 32 random bytes from .NET's
`RandomNumberGenerator`, written as `dfw_` and their base64url without padding, 47 characters. Only
the SHA-256 of that text is stored. There is no way to list a key or show one again, and a key is
never logged or put in an address. It is `WorkerKeys`, in `src/DevFlow/Workers/`.

**The worker scheme.** A worker sends `Authorization: Bearer dfw_…` and nothing else: no cookie and
no `Origin`. `WorkerAuthentication` reads the one header, refuses a second one, another scheme or a
key of the wrong shape, hashes the key, looks the hash up in `worker_key_lookup` through the door's
`WithoutUser`, and then, as the project's user through `InUserTransaction`, confirms that
`worker_keys` holds that hash for that project. Everything the request does after that runs as that
user. Anything else is `401` with an empty body, the same whatever the reason. A worker route takes
only this scheme, and a user's route only the user's token: a user's token is not the shape of a
key, and a key is not a token the core signed.

The routes, in `WorkerEndpoints`:

- `POST /devflow/api/projects/{id}/worker-key`, the user's, origin checked, answers
  `{"key": "dfw_…", "save_command": "…"}` with `Cache-Control: no-store`, the one time the key is
  sent, with the command that saves it, see "Starting a worker", and replaces the project's last
  key at once. `404` for a project the caller does not have, another user's among
  them, and nothing is stored.
- `GET /devflow/api/projects/{id}/workers`, the user's, answers
  `{"key_made_at": "<time>" or null, "recipe_version": "<commit>" or null, "start_command": "…", "held": "stopped", "unwatched" or null, "workers": [{"id", "name", "recipe_version", "base_image", "first_seen_at", "last_seen_at", "rebuild"}]}`,
  by when each worker first said hello: `recipe_version`, the project's recipe as Dev flow last
  read it, null before it has been or when the default branch has none; `start_command`, the
  command that builds and starts a worker; each worker's `rebuild`, `recipe`, `base_image` or
  null, see "Starting a worker"; and `held`, why no worker of the project is handed work, which the
  project's page says, the first that applies: `stopped` or `unwatched`, see "Watching the default
  branch", then `no_claude_login` or `claude_login_expired`, see "Jobs", or null; and
  `cannot_run`, once six runs in a row have failed outside their cards, "Dev flow cannot run
  <project>'s cards: <the last reason>", until a run succeeds, or null, see "Stops". Never the key.
  `404` as above.
- `POST /devflow/api/worker/hello`, the worker's, with
  `{"worker_id": "<uuid>", "name": "<the container's hostname>", "recipe_version", "base_image"}`,
  records the worker in its key's project, or brings it up to date, keeping when it was first
  seen, and answers `204`. Each field is required; `worker_id` is a UUID that is not all zeros, and
  each text has 1 to 255 characters, not only spaces; otherwise `422` naming the field.
- `POST /devflow/api/worker/jobs/next`, the worker's, with `{"worker_id"}`, marks the worker seen
  and answers its next job, `200`, or `204` when there is none for it, see "Jobs". A worker that
  needs a rebuild is never given one, nor is a worker of a project that is stopped or unwatched, see
  "Watching branches". A worker that has not said hello
  in the key's project is `409` "Say hello first", and says hello and asks again; no `worker_id`
  is `422`.

The tables, from `migrations/0007_workers.sql`:

- `worker_keys`, guarded like every scoped table: `project_id`, its primary key, `user_id`,
  `key_hash`, the 32 byte SHA-256, unique, and `created_at`.
- `worker_key_lookup`: `key_hash`, its primary key, `project_id` and `user_id`, nothing else, and
  **no** row level security, so a worker's request, which carries no user, can be led to one. A
  trigger on `worker_keys`, with a `SECURITY DEFINER` function, `sync_worker_key_lookup`, owned by
  `devflow_owner`, keeps it, so a replaced key and a disconnected project leave nothing behind;
  `devflow_app` may only `SELECT` it, as with `poll_targets`.
- `workers`, guarded: `project_id` and `id`, the worker's own, together its primary key, so the
  same id in two projects is two workers and one user's worker never meets another's, `user_id`,
  `name`, `recipe_version`, `base_image`, each 1 to 255 characters, `first_seen_at` and
  `last_seen_at`.

`worker_keys` and `workers` reference `projects (id, user_id)`, not `projects (id)` alone, with
`ON DELETE CASCADE`, so a key or a worker can only belong to its project's own user, whatever a
query tries, and disconnecting removes them; the migration adds the unique constraint on
`projects (id, user_id)` they need.

`migrations/0009_recipe_version.sql` adds `projects.recipe_version`, the version of the project's
recipe as Dev flow last read it, a full commit id of 40 or 64 lowercase hex digits, or null.

## Starting a worker

A worker runs in a container built in three layers: the **base image**, built by every release of
the platform; the project's **recipe**, its `.devflow/Dockerfile`, which adds the project's own
tools on top; and the worker, started from that image with the project's key. The owner builds and
starts it by pasting two commands from the project's page into a terminal on the machine that
runs the workers, the Mac mini. Why it is built so is "Workers" in `docs/devflow-architecture.md`.

**The base image** is `worker.Dockerfile`, built from the repository root like every hosted image,
and built at each release as the service `devflow-worker` in `infra/hosted/compose.yaml`, never
started: it has no replica. Its build stage publishes `DevFlow.Worker` as one self-contained file
for the build's architecture. Its final stage is `node:26-trixie-slim` with `git`,
`ca-certificates`, `jq`, `procps`, `util-linux` and Claude Code 2.1.325, installed with `npm ci`
from `worker/package-lock.json`, which pins it with its integrity, and on `PATH` as `claude`; and
Playwright 1.65.0, the version `e2e/` pins, from the same lock and on `PATH` as `playwright`, with
Chromium and the system packages it needs installed at build time by
`playwright install --with-deps chromium` into `/ms-playwright`, root's, mode 755, which `agent`
reads and cannot change, so one job cannot leave a browser that fakes the next job's pictures; the
image sets `PLAYWRIGHT_BROWSERS_PATH` to it, and the worker gives it to the agent's process, whose
environment it builds from nothing. Two users are in it:

- root runs the worker program, the image's entrypoint, `/usr/local/bin/devflow-worker`, and keeps
  a job's secrets in `/run/devflow/`, mode 700, owned by root;
- `agent`, uid 1000, home `/home/agent`, runs Claude Code, the project's check and its start, which
  the worker starts with `setpriv --reuid agent --regid agent --init-groups` and an environment it
  builds from nothing, and owns the working copies under `/work`, the image's working directory.

Claude Code adds a `Co-Authored-By` trailer to every commit and a line to every pull request unless
told not to, so `/home/agent/.claude/settings.json` sets
`{"attribution": {"commit": "", "pr": "", "sessionUrl": false}}`: the `attribution` setting, which
replaced the deprecated `includeCoAuthoredBy` in Claude Code 2.0.62, and where an empty string
hides each. The image also turns two build arguments a project's build passes into the variables
the worker reports in `hello`, with `ONBUILD`: `DEVFLOW_BASE` becomes `DEVFLOW_BASE_IMAGE`, and
`DEVFLOW_RECIPE_VERSION` stays itself.

Its test is `tests/image/base-image.test.sh`, which `infra/hosted/check.sh` runs: it builds the
image under a tag of its own, never the hosted name, and checks that `agent` cannot list or read
`/run/devflow/`, nor read root's environment through `/proc`, that the worker runs, that
`git-upload-pack` is on the agent's `PATH`, that `claude --version` answers 2.1.325, that `git` and
`node` run, that the attribution is off, that `playwright --version` answers 1.65.0 and, as
`agent` in an environment of `HOME`, `PATH`, `LANG` and `PLAYWRIGHT_BROWSERS_PATH`,
`playwright screenshot --full-page` of a page node serves on `localhost` writes a PNG at the size
asked for, while `/ms-playwright` stays root's and unwritable by `agent`, that `agent`, in the
environment Claude Code is given, starts a page project and takes both its pictures with the
commands the developer's brief gives, see "Screenshots", and that a recipe built
FROM it reports its base and version. From the repository root, with Docker running:

```zsh
bash devflow/tests/image/base-image.test.sh
```

**The format of `.devflow/`**, in the project's own repository, on its default branch:

- `.devflow/Dockerfile`, the recipe: it begins `ARG DEVFLOW_BASE` and `FROM ${DEVFLOW_BASE}`, and
  installs what the project's check needs with `RUN`, as root. It is built with no context, so it
  cannot `COPY` or `ADD`, and Dev flow refuses one that does: the project's own packages are
  installed by its check, in each card's working copy.
- `.devflow/project.json`, how a card is checked and the project started:
  `{"version": 1, "check": "<command>", "start": "<command or empty>", "url": "<http://localhost… or empty>"}`,
  every field there and no other. Its schema is `contracts/devflow/project.schema.json`, and
  `DevFlow.Protocol`'s `ProjectFile` reads it by the same rules. Dev flow reads it when it hands
  out a job, which carries its `check`, `start` and `url`; how `start` and `url` are used, and
  what `start` must do, is "Screenshots".

**The recipe route**, `GET /devflow/api/worker/recipe`, in `src/DevFlow/Recipes/`, under the
worker scheme: the key's project's `.devflow/Dockerfile`, from `FolderFile`, read as the app at the
project's setup version, the newest commit of the default branch that changed anything under
`.devflow/`, its recipe or its `project.json`, as `text/plain` with
`X-Devflow-Recipe-Version: <that commit>`, `X-Content-Type-Options: nosniff` and
`Cache-Control: no-store`. It records the commit as the project's `recipe_version`, whose column
keeps its name though it is the version of the whole folder. A line beginning
`COPY` or `ADD`, in any case, alone or after `ONBUILD`, after continued lines are joined, comment
lines inside them dropped and the `escape` directive honoured, is `422` with `line` and a detail
naming it; text that is not UTF-8, or over 64 KB, is `422` too; no file is `404` "This repository
has no .devflow/Dockerfile"; GitHub not answering is `503`.

**The two commands**, below the key on the project's page, for a project named by its repository,
lowercased with every other run of characters one hyphen, here `fodr`:

1. Saving the key, shown only right after "Make a key", with the key in it, in a subshell so the
   terminal's own umask is kept:

   ```zsh
   (umask 077 && mkdir -p ~/.devflow && printf 'DEVFLOW_PROJECT_KEY=%s\n' 'dfw_…' > ~/.devflow/fodr.env)
   ```

2. Building and starting, shown always, without the key, which it reads from that file:

   ```zsh
   sed -n 's/^DEVFLOW_PROJECT_KEY=/Authorization: Bearer /p' ~/.devflow/fodr.env | curl -fsS -H @- -D /tmp/devflow-fodr.headers -o /tmp/devflow-fodr.Dockerfile '<DEVFLOW_PUBLIC_ORIGIN>/devflow/api/worker/recipe' && \
     docker build --build-arg DEVFLOW_BASE=<DEVFLOW_WORKER_IMAGE> --build-arg DEVFLOW_RECIPE_VERSION="$(awk 'tolower($1) == "x-devflow-recipe-version:" { print $2 }' /tmp/devflow-fodr.headers | tr -d '\r')" -t devflow-fodr - < /tmp/devflow-fodr.Dockerfile && \
     { docker rm -f devflow-fodr-1 2>/dev/null; docker run -d --name devflow-fodr-1 --restart unless-stopped --memory 2g --env-file ~/.devflow/fodr.env -e DEVFLOW_URL=<DEVFLOW_WORKER_URL> devflow-fodr; }
   ```

The recipe is fetched from the platform's public origin, the address the browser is on, and not
from `DEVFLOW_WORKER_URL`: Docker Desktop's `host.docker.internal` resolves inside a container and
not on the Mac itself. The version reaches the build from the response's header, so it is the
commit the bytes were read at. `curl` reads the key's header on stdin, `-H @-`, and not as an
argument, since every user on the machine sees a process's arguments in `ps`. Running the second
again replaces the worker. When `curl` fails, its message names the status: `404` is a repository
without a recipe, and `422` one that copies files, whose reason the same `curl` without `-f`
shows. `WorkerStart`, in `src/DevFlow/Recipes/`, writes both.

**Memory.** Each worker is started with `--memory 2g`; one run of a real task peaked at 1.05 GiB.
On the Mac mini, with Docker's 8 GB, start at most two workers beside the hosted copy until its
memory has been measured.

**A worker that needs a rebuild** runs another version of the recipe than Dev flow last read, or
another base image than `DEVFLOW_WORKER_IMAGE`. It gets `204` from `jobs/next`, so no new work, a
card it is on finishes, and its row on the page shows "<project>'s recipe has changed: run the
command again" or "A new base image was released: run the command again". The recipe's version,
the setup version, so a commit changing only `project.json` asks for a rebuild too, is
recorded by the recipe route and by the loop each time it reads the project; GitHub not answering
keeps the last, and a default branch without the recipe clears it. A project whose recipe has not
been read judges its workers by their base image alone. The comparison is in SQL, in
`WorkerStore`, so the workers' namespace compares no strings, which the constant time test checks.

## Setting up a project

A project hands out no card until its setup has been tried. Every version of its `.devflow/`, the
newest commit of the default branch that changed anything under it, is checked once, by a job a
worker built from that version runs, before that worker takes any other job. Why it is built so is
"Setting up a project" in `docs/devflow-architecture.md`. The code is `src/DevFlow/Setup/`, the
worker's side `src/DevFlow.Worker/Build/BuildJob.Checking.cs`.

**A valid `.devflow/`** is both files on the default branch, `.devflow/Dockerfile` by the recipe
route's rules and `.devflow/project.json` by its schema, `SetupFiles.Problem`. Each read of the
project records whether it is, `projects.devflow_valid`, and why not, `devflow_problem`, from
`migrations/0043_setup_runs.sql`; null before the first read, which hands out no setup on a guess.
Missing or invalid, the project's first step is the setup run.

**The setup run.** The setup step shows a command, `WorkerStart.SetupCommand`, that starts a worker
from the base image itself, with no recipe and no build, under the key file "Make a key" saved:

```zsh
{ docker rm -f devflow-fodr-setup 2>/dev/null; docker run -d --name devflow-fodr-setup --memory 2g --env-file ~/.devflow/fodr.env -e DEVFLOW_URL=<DEVFLOW_WORKER_URL> -e DEVFLOW_BASE_IMAGE=<DEVFLOW_WORKER_IMAGE> <DEVFLOW_WORKER_IMAGE>; }
```

It names its base image so it is not held back for another, and is not restarted with the
machine. Its image sets no recipe version, so it reports `unset`, `WorkerApi.NoVersion`. A worker
with no recipe version is handed only a setup's jobs, `SetupRunStore.NextKind`, while its project's
`.devflow/` is not valid, and otherwise nothing, needing a rebuild, `recipe`, as a worker on an old
recipe does; it is never handed a check. Its jobs need the Claude login, as a card's do.

- **The run**, kind `setup`: no card, `devflow/setup`, or `devflow/setup-<k>` for the project's
  `k`th setup, made from the default branch, the brief `Briefs/setup.md`, the form
  `contracts/devflow/setup.schema.json`, an empty `check`, the building model, the project's limits
  and a key that pushes. The worker runs the agent, commits what it left and pushes, and runs no
  check, see "The worker".
- **The report**, `DevFlow.Protocol`'s `SetupReport`: `summary`; `proposed`, `preset`
  (`fully_automatic`, `guided` or `only_i_merge`), `why` and `paths`; `variables`, each `name` and
  `why`; `unsettled`, each `kind`, `real_secret`, `containers_in_tests`, `macos_needed` or `other`, and
  `detail`; and a build's `decisions` and `handover`. `SetupReport.Schema()` writes the schema,
  pinned by the worker's `SetupFormTests`.
- **What it pushed is validated** by the result route, before the run is judged: both files are
  read as the app at the commit it names, and the result is recorded as its check,
  `Dev flow's validation of .devflow/`, exit 0 or 1 with why; GitHub not answering is `503`, and the
  worker sends the result again. A failure in the setup, a report out of its form, nothing pushed and
  a validation that failed each run it again once, "Failed once, so it runs again", and stop it the
  second time in a row, as a card's do; a failure outside puts it back and makes the project wait.
- **Its pull request**, on the minute, `SetupLoop`: opened as the app from its branch into the
  default branch, titled "Set up Dev flow", its body `SetupText` with the summary, what it could not
  settle, what it proposes, the values its tests need, its decisions and the validation, every text
  made safe as a card's is, and rewritten after each fix. It opens only once what it wrote is
  valid: one that stopped before then has none, and is run again from its step.
- **Reviewed and fixed like a card's**: a `review` job, the setup's pull request as its card,
  `Briefs/setup-reviewer.md`, which also asks whether the recipe installs only what the check and
  the start need, the reviewing model, a key that only fetches; `changes` starts a fix round, a
  `setup` job with `to_fix`, in the project's `fix_rounds_card`, and its rounds used up stop it.
  The run's kinds in the database are `setup` and `setup_review`.
- **It always waits for me.** Approved, it is `waiting_for_owner`, "Approved; it waits for you to
  merge it", whatever the settings: the merge check reads only cards. I merge it from the step,
  `POST /devflow/api/projects/{id}/setup/merge` with `{"head_commit"}`, as myself with my linked
  token, refused as a card's merge is, or on GitHub; the minute follows it, merged is `done`, closed
  is stopped, "The pull request was closed".
- **Stopped**, my comment on its pull request, by a trusted account, after it stopped, runs it again
  as a fix with the comment, or opens a new pull request from the same branch when the old one
  closed. One that stopped before it had a pull request, whatever stopped it, has nothing on GitHub
  to comment on, so my comment from its step, `POST /devflow/api/projects/{id}/setup/run-again`,
  runs it again the same way: a fix of its branch when a run pushed to it, a first run again when
  none did, with the comment in its brief, its failures counted afresh, "You commented on it; it
  runs again next". A run under way when its pull request closes leaves it stopped, "The pull
  request was closed", until my comment there.

The table, `setups`, guarded like every scoped one: one row per setup, `(project_id, number)`,
its branch, state (`queued`, `checking`, `reviewing`, `fixing`, `waiting_for_owner`, `stopped`,
`done`), reason, pull request, which every state but `queued`, `checking`, `fixing` and `stopped`
has, fix rounds, the comments its next run answers, and its failures in a row; at most one not done
per project. `runs.setup_number` names a setup's runs.

**The settings step.** After the setup's pull request and before a worker on the recipe, the
owner accepts the settings the newest setup run that succeeded proposed, its `proposed` preset and
paths, or saves the settings panel; either does the step, once, `project_settings.accepted_at`, from
`migrations/0044_settings_step.sql`. "Accept" fills in what the preset covers over the settings as
saved, as the panel's preset buttons do, sets `wait_paths` to the proposed paths, and saves them
through `PUT /devflow/api/projects/{id}/settings`; nothing is stored as a preset. The step waits,
`settings_waits` on the setup route, `SetupStore.SettingsDueSql`, only once the setup is behind it:
`.devflow/` is valid on the default branch, so the setup's pull request merged, and no setup sent back
from a check of its version is under way. The first save of the route while it waits, whatever it
changes, marks the step done; a save before then is kept but does not, so the proposal is still put.
The page offers the step only while it waits. A project with no setup run, one whose `.devflow/` was
valid when it was connected, has no proposal, and its step asks for the panel to be saved once. Until the step is done the project hands out no reading or build, `JobStore.NextKind`,
though its check and its setup runs go out before it; every project connected before the migration,
Fodr and the registered ones among them, starts with it not done. In the fake mode the fake worker
takes the settings as accepted before it asks for a job, unless `settings_wait` is true, and the
tests' harness does the same unless a test of the step turns `SettingsAccepted` off.

**Sending a failed check back.** On the newest check of the project's version, failed, while no
check of it runs and no setup is under way, `can_send_back`, the step offers "Send back with a
comment". The owner's comment starts a new setup from the default branch on the next free
`devflow/setup-<k>`, numbered after the project's last. Dev flow reads both files of `.devflow/` as
the app at the commit checked and keeps them on the setup with the comment and the check, from
`migrations/0045_sent_back_setups.sql`; every run of that setup, its fixes too, has a section "Sent
back" in its brief with the files, the check's exit code and output and the comment, each fenced,
the comment as the owner's input. The setup is needed, `SetupRunStore.SentBackPending`, until it is
done or the project's version changes, so its jobs go to the worker started from the base image,
which is not told to rebuild meanwhile; its pull request is the setup's, opened, reviewed, fixed and
waiting for the owner as above. A check sent back is listed no more under "Setup" on "Waiting for
you"; the setup run is, once it waits or stops. The owner's comment on GitHub is not read for this,
since a check has no issue or pull request of its own.

**The check job**, kind `check`, handed out by `jobs/next` to a worker whose version has no check
that passed, when no check of that version is running and its newest check, if any, failed and
the owner asked for another; a worker on a version whose newest check failed gets `204` until then
or until the version changes. It comes before the Claude login is looked at, since it runs no
agent: its `card` is null, its `brief` and `model` empty, its `claude_token` empty and its
`report_schema` null; `branch` and `base` are the default branch, `to_check` holds `version`, the
worker's, and `commit`, the default branch's head as read with `BranchHead` when it was handed out,
and `check`, `start` and `url` come from the default branch's `project.json`, with a key that only
fetches and `limits.minutes` 30. The worker clones the repository, names `to_check.commit` as the
branch in its own repository, runs the check as `agent` on a clean clone of it with `CI=true`, as a
build's check runs, for at most 30 minutes, and, when it passed and `start` and `url` are set,
starts the project from another clean clone and takes its address at both sizes as "Screenshots"
takes a page, named `<page>-desktop` and `<page>-phone` from the address's path. Its result is
`succeeded` when the check ran, whatever its exit code, with `report` null, `check` required and
`head_commit` null, and `pictures`, each `{"name", "png"}`, a PNG of at most 5 MB in base64, at most
two, each named in `screenshots.names`; a key that only fetches pushes nothing, so the pictures go
to Dev flow. `pictures` on any other job is `422`. Anything that stopped the check from running is
`failed`, outside: the run is `failed_outside_card`, nothing is recorded for the version, and the
project waits as "Stops" has it; a check lost with its worker is put back the same way. A check
that ran with `0` ends `succeeded`, any other exit code `check_failed`.

**What is recorded**: `recipe_checks`, one row per check that ran, its run's id, the project, the
version, the commit, whether it passed, its exit code, its last 200 lines, its pictures' names or
why there are none, when, and `again_at`, when the owner asked for another after it failed; and
`recipe_check_pictures`, each picture's bytes under its check and name. A version counts as checked
once one check of it passed. A project is `setting_up` until any version has passed and its
settings step is done, then `registered`; a later version that fails holds back only its own
workers. Every project connected before this, Fodr among them, starts `setting_up` and is checked
once. The migration is
`migrations/0042_recipe_checks.sql`, which also lets a run of kind `check` name neither card nor
feature and adds `runs.recipe_version` with one check of a version under way at a time.

**The routes**, under the `Api` policy:

- `GET /devflow/api/projects/{id}/setup`: `{"state": "setting_up" or "registered", "steps": [{"step", "done"}], "check": … or null, "checking"}`.
  The steps in their order: `setup`, a valid `.devflow/` on the default branch; `recipe`, a version
  of `.devflow/` read from the default branch; `settings`, the settings accepted or saved once;
  `worker`, a worker built from that version said hello; `check`, a check of that version passed.
  The values' slice of part 6 adds its step before `worker`. It also answers `proposal`, the
  `preset`, `why` and `paths` the newest setup run that succeeded proposed, or null, and
  `can_send_back`. It also answers `problem`, why `.devflow/` is not valid, or null;
  `setup_command`; and `setup`, the newest setup run with its state, reason, pull request, head,
  newest report in its form and the review of its head, or null. `check` is the newest check of
  the project's version, or of any before it is known, with `id`, `version`, `commit`, `passed`,
  `exit_code`, `output`, `pictures`, `pictures_note`, `checked_at` and `again_asked`; `checking`
  says a check of that version runs.
- `POST /devflow/api/projects/{id}/setup/check-again`: `204`, and the next worker on that version
  that asks is handed another check; `409` when the newest check did not fail or another was
  already asked for.
- `POST /devflow/api/projects/{id}/setup/run-again` with `{"comment"}`: `204`, and the setup that
  stopped before it had a pull request runs again with the comment; `422` for a comment missing,
  empty or over 10,000 characters, as a send back's; `409` when the newest setup has not stopped, has
  a pull request, is running, or the project no longer needs one.
- `POST /devflow/api/projects/{id}/setup/send-back` with `{"comment"}`: `204`, and a new setup
  run is queued with it, as above. In order: `404` for a project the caller does not have; `422` for
  a comment missing, empty once trimmed or over 4,000 characters; `409` when it is not offered, the
  newest check of the version not failed, another check of it running or a setup under way; `503`
  when GitHub did not answer for the files.
- `GET /devflow/api/projects/{id}/setup/checks/{check}/pictures/{name}`: the PNG as stored, as
  `image/png` with `nosniff`, `default-src 'none'` and `private, max-age=3600`; `404` otherwise.

**On the page**: while the project is setting up, its Setup panel stands in place of its Cards
panel, with the steps, each done or to do, the settings step while it waits, with the proposal, its preset, why
and paths, and "Accept", or without one the line asking to save the panel once, the newest check
with its output and its pictures, and "Check again" when it failed, with "Send back with a comment"
when it may be, which opens a box labelled "What should the setup do differently?"; a registered project whose newest check failed shows the panel above
its cards; while the setup step is due it shows why, the setup command to copy, and the setup run,
where it is, its pull request, its review, what it could not settle by kind, what it proposes, the
values its tests need, "Merge" when it waits for me, and, when it stopped, "Comment on its pull
request, and it runs again." or, with no pull request, "Comment to run again", which opens a box
labelled "A comment runs it again." with Send. "Waiting for you" lists under "Setup" a project
whose newest check failed with no other asked for, and one whose setup run waits for me or stopped
while its `.devflow/` is not valid, `setup_runs`, `src/setup.tsx`'s `WaitingSetupRun`, and counts
each; a project merely setting up is not listed, since a project connected only so Dev flow reads
this repository's runner would sit there for good.

**In the fake mode**, the fake worker passes a check it is handed unasked, so a test of anything
after setting up never meets it; with `for_check`, its prepared result ends the check instead. The
tests' harness takes every version its workers report as checked, `RecipesChecked`, unless a test of
setting up turns it off.

## The worker

The worker is the program in a worker's container, `src/DevFlow.Worker/`: started with Dev flow's
address and a project's key, it introduces itself, asks for work in a loop, and runs an agent
behind one narrow interface. What it says to Dev flow is `src/DevFlow.Protocol/`, the worker API's
paths, its requests and their JSON, which Dev flow's worker routes take too, so a change that breaks
either side fails to compile. Why it is built so is "Workers" in `docs/devflow-architecture.md`.
A job Dev flow hands it, see "Jobs", it builds, see "A build job" below, reviews, see "Review", or
reads, see "Reading a card" under "Jobs", by its kind; one of a kind it does not know is left, and
Dev flow finds its run lost. The image it runs in and the commands that start it are "Starting a
worker".

**Its settings**, from the environment:

| Variable | What it is |
|---|---|
| `DEVFLOW_URL` | Required. Dev flow's address, an `http` or `https` origin with no path, such as `http://host.docker.internal:9080` |
| `DEVFLOW_PROJECT_KEY` | Required. The project's worker key, `dfw_…`, made on the project's page; sent as `Authorization: Bearer` and nowhere else, and never printed |
| `DEVFLOW_RECIPE_VERSION` | The version of the project's recipe the image was built from, which the image sets; sent in `hello` |
| `DEVFLOW_BASE_IMAGE` | The base image the image was built on, which the image sets; sent in `hello` |

A value of only spaces counts as unset. Unset, each version is sent as `unset`: `hello` requires
both, each 1 to 255 characters, so an empty one would be refused, and `unset` says plainly that the
worker runs outside a project's image and never matches a version Dev flow expects.

**The loop.** The worker makes its id, a UUIDv7, when it starts, and takes its container's
hostname as its name. It says `hello`, then asks `jobs/next`:

- `204`: no work. It waits and asks again, 5 seconds at first, doubling each time to at most 60.
- A job: it builds it, see "A build job", and asks again at once, and the wait starts again from 5
  seconds.
- `409`: it says `hello` and asks again, at once, or after a wait when it has just said it.
- Dev flow away, which is no connection, no answer in 30 seconds or a `5xx`: a wait of the same
  length and another try, never an exit, and the next answer from Dev flow begins with `hello`
  again. So does an answer this worker does not know.
- `401`, to either call: it ends, see below.

Time is a `TimeProvider`, so the tests move it. The log is a line per event, its time in UTC
first, on standard output; nothing is logged while there is no work.

**Exit codes:**

| Code | When |
|---|---|
| `0` | Stopped by `SIGTERM` or `SIGINT`, as `docker stop` sends |
| `2` | A setting is missing or wrong, named on standard error before anything is sent; or Dev flow refused the key, `401`, with a line naming `DEVFLOW_PROJECT_KEY` and never the key; or refused what `hello` reports, `422`, with Dev flow's reason |

Anything else is a crash, with .NET's own code and message.

**The agent interface.** `IAgent`, in `src/DevFlow.Worker/Agents/`, has one method: given the
working copy, the brief as text, the report's JSON Schema, the model, the limits and the
credential, it answers a result, a success with the report, an object, or a failure with its reason and which
side it lies on, in the card or outside it; and either way the usage as the agent gave it, its
standard error, and how it was logged in. Two implementations:

- `FakeAgent` answers a prepared report and writes or removes the files it is told to, refusing a
  path outside the working copy. It spends no usage, so the worker's own tests use it.
- `ClaudeCodeAgent` runs Claude Code headless. First `claude auth status --json`, which must answer
  `"loggedIn": true` and `"authMethod": "oauth_token"`, or the run never starts and fails outside
  the card; the method is recorded either way. Then, in the working copy:

  ```zsh
  claude -p <brief> --model <the job's model> --permission-mode bypassPermissions --output-format json \
    --json-schema <schema> --max-turns <the job's turns> --max-budget-usd 25
  ```

  for a build, the `Write` access of `AgentRun`. A review's reviewer, `ReadOnly`, runs in the job's
  folder instead, with `--permission-mode default --permission-prompts none --allowedTools
  Read,Grep,Glob` in place of `--permission-mode bypassPermissions`, see "Review". A reading's
  reader, `ReadOnly` too, runs the same way in its copy of the branch it reads, `ReadJob` in the
  worker's `Read/`: root clones the repository with the job's key, which only fetches, into its own
  bare repository, the agent clones the job's branch from it through a bundle, and the reading goes
  back with no commit and no check, see "Reading a card".

  never with `--bare`, which refuses the subscription token. Both get an environment built from
  nothing: `HOME`, the agent's home, `/home/agent`; `PATH`; `LANG`; `CLAUDE_CONFIG_DIR`, `.claude`
  in that home; `CLAUDE_CODE_OAUTH_TOKEN`; and `PLAYWRIGHT_BROWSERS_PATH`, `/ms-playwright`, the
  image's Chromium, so the pictures the developer takes of its own pages find a browser, see
  "Screenshots". The arguments are a list, never through a shell.

**When a run succeeds**: only when every one of these holds: exit `0`; standard output is valid
JSON; `is_error` is false; `terminal_reason` is `completed`; `structured_output` is present and an
object; `permission_denials` is empty. `subtype` is never read, since it says `success` on some
failures. The report's schema is checked by Dev flow, which hands it out with the brief; the
worker checks only that it is an object. Anything else is a failure, judged in this order:

| What Claude Code answered | Where it lies |
|---|---|
| Past the time limit, ended by the worker | In the card, sent with `timed_out` |
| Nothing on standard output, as root with `bypassPermissions` | Outside |
| Something that is not JSON | Outside |
| `terminal_reason` `api_error`: not logged in, a refused key (`api_error_status` 401), a usage limit | Outside, with `resets_at` when its `result` says when the limit resets, see "Stops" |
| `terminal_reason` `max_turns` | In the card |
| `terminal_reason` `budget_exhausted` | In the card |
| `permission_denials` not empty, a refused action, for a build; a review's are kept and fail nothing | In the card |
| Any other exit code | Outside |
| `is_error` true, whatever `subtype` says | Outside |
| Any other `terminal_reason` | Outside |
| No `structured_output`, or one that is not an object | In the card |

Outside the card means another try may well succeed and the card is not to blame: the login, the
API or the machine. The usage is taken as Claude Code gives it: `usage`, `modelUsage`,
`total_cost_usd`, `duration_ms` and `num_turns`.

**The model and the limits** are the job's, from its project's settings as they were when it was
handed out, see "Jobs"; the worker has no model or limit of its own. The turns, 200 by default, and
the spend, 25 USD for every job until part 8's budget, Claude Code enforces, overshooting by about a
turn; the minutes, 90 by default, the worker enforces by ending the process and every process still
in its tree. A job without a model, or with one that is not a model's name, lowercase letters and
digits with single dots or hyphens between, is a protocol error: the worker refuses it before
anything runs and sends it back failed outside the card, "The job carries no model the worker can
pass to Claude Code, so Dev flow and the worker disagree on the protocol", so no value given as
`--model` is read as another option. One the agent
detached from that tree, by forking twice, keeps running as `agent`; the accepted risk under "The
agent's copy has no credentials" in `docs/devflow-architecture.md` covers it.

**The token** is handed to the run in its environment only. The worker never writes it to a file,
logs it or sends it back: `AgentCredential` prints as redacted, and the token's value is replaced
by `[redacted]` in Claude Code's standard output and standard error before anything reads them, so
neither the report, the stderr kept nor a reason can carry it out.

**Its own user.** The process starter, `SystemProcessStarter`, takes the user to run as, `agent`
in the image, and starts Claude Code, the check and every git command in the agent's copy through
`setpriv --reuid agent --regid agent --init-groups`, which keeps the environment as built; Claude
Code refuses `bypassPermissions` as root. The worker itself runs as root, so what a job needs kept
from the agent stays out of the agent's reach. Without a user, as in the tests, it starts the
program directly.

**A build job**, `BuildJob` in `src/DevFlow.Worker/Build/`, from the job to its result. The folders
are `JobFolders`: `/run/devflow/<job>/`, root's alone; `/run/devflow-handover/<job>/`, root's, mode
755, where root writes the bundles the agent clones from, which the agent reads and cannot write;
and `/work/<job>/`, the agent's, where root writes nothing:

1. **Refused before anything runs** when the job's branch is the repository's default branch or its
   base, or either is not a plain branch name: letters, digits, `.`, `_`, `-` and `/`, not
   beginning with `-` or `/`, without `..`, `//` or `@{`, and not ending in `/`, `.` or `.lock`.
   The result is a failure in the card, so the card stops rather than coming back.
2. **The worker's own repository.** Root makes `/run/devflow/<job>/`, mode 700, and clones the
   repository there with the job's push key, `git clone --bare`, into `repo`. The job's branch is
   the remote's when it has one, from a run that was lost, and otherwise made at the base with
   `git update-ref`, which refuses one already there; `HEAD` points at it.
3. **The agent's copy.** Root makes `/run/devflow-handover/<job>/` and writes a bundle of the
   branch and the base there, `copy.bundle`, mode 644. The agent makes `/work/<job>/`, and
   `/work/<job>/self` beside where its copy goes, its folder for the pictures it takes of its own
   pages, see "Screenshots", and clones the
   bundle into `/work/<job>/copy`, with `user.name` and `user.email` the job's committer, the app's
   bot, in the copy's config; the bundle is removed, so the copy's remote is a path that no longer
   exists and holds no credentials.
4. **The agent** runs there as `agent`, through `IAgent`, with the job's brief, report schema,
   model, limits and Claude token. The worker ends it at the job's `minutes`, whatever it is
   doing, by cancelling it, which ends its process and every process still in its tree; that is a
   failure in the card, "it ran past the time limit of <minutes> minutes and was ended".
5. **What it left uncommitted**, tracked or new and not ignored, is committed in the copy, as
   `agent`, "Work the agent left uncommitted". Ignored files, such as a build's output, stay out.
6. **Back to the worker.** Root fetches the branch from the copy into its own repository, without
   force, so a history the agent rewrote is refused, a failure in the card. The copy's side,
   `git-upload-pack`, runs as `agent` with its environment reset, through `--upload-pack`.
7. **The check**, when the agent succeeded: the worker sends `progress` `checking`, and again
   every 5 minutes while the check runs, so Dev flow keeps seeing it; root bundles the fetched
   head into the handover folder, `check.bundle`, the agent clones it into `/work/<job>/check`, and
   the job's `check` runs there as `agent`, `sh -c` with standard error joined to standard output,
   in an environment of
   `HOME`, `PATH`, `LANG` and `CI=true`, for at most 30 minutes. Its exit code and the last 200
   lines of its output go in the result; past 30 minutes it is ended and its output says so. When
   the agent failed the check does not run, since Dev flow judges the failure first.
8. **The push**, whatever the check said: a fresh key from the push key route, and
   `git push origin <head>:refs/heads/<branch>` from root's repository, never with force. A
   refused push is a failure outside the card, its reason what git said, and `head_commit` null.
9. **The pictures**, when the agent succeeded, the check exited 0 and the push went through: the
   project is started from a clean copy of the pushed commit and the report's pages are taken, see
   "Screenshots". The worker says `progress` `checking` every 5 minutes meanwhile, as during the
   check. Nothing that happens there fails the job.
10. **The result** is sent, see "Jobs": the agent's report on a success, or the failure; the check;
   the head commit pushed; the auth method, usage and the last 200 lines of the agent's standard
   error; the duration; and the pictures' names and note, when they were tried. A push key or a
   result Dev flow is away for is asked again, 5 seconds doubling to 60, for at most 10 minutes.
11. **Afterwards** the folders are removed: root's two by root, and `/work/<job>/` by the agent,
    `rm -rf` as `agent`, so a link it left there, or the folder replaced with one, is removed as
    the link and nothing is removed that the agent could not remove itself. The worker asks for its
    next job. The container, its tools and its caches are kept.

**A whole feature's fix** is carried as a card's fix, see "Fixing a feature" under "Reviewing a
feature", on the feature's branch from `to_fix.commit`, with three differences: a branch that is
elsewhere on the remote runs nothing and fails outside the job, since the commit came from the
feature's watch, which can lag; its commit is pushed only when the agent succeeded with a report
that `Report.TryRead` reads as `done` and its check passed, since the feature's branch is shared and
is the head of its pull request to the default branch, so a fix that failed, stopped or reported out
of its form pushes nothing and names no commit, and for a report stopped or out of form the check is
not run; and it takes no pictures, since nobody reads them.

**A conflict job** is carried as a build, see "A conflict" under "The end of a feature", on the
feature's branch from `to_resolve.feature_commit`, with these differences:

- **The copy.** The branch must be on the remote at `feature_commit`; when it is elsewhere nothing
  runs, a failure outside the job, since the commit came from the feature's watch, which can lag.
  `default_commit` must be in the repository, or it fails in the job; root moves its own ref of the
  default branch to it, in its own repository, which never pushes it, so the bundle carries it and
  the agent's copy holds it as `origin/<default>`.
- **The agent** merges it and resolves the conflict, as its brief says. What it leaves uncommitted is
  not committed for it: the merge commit is the whole of its work, and a second commit would be
  refused below.
- **One merge commit.** When the agent succeeded, root reads the fetched head's parents with
  `git rev-list --parents -n 1`: it must be one new commit on `feature_commit`, a merge of exactly
  two parents, `feature_commit` first and `default_commit` second. Anything else, no new commit, a
  second commit on top, a commit that is not a merge, a merge of more, or of another commit, is a
  failure in the job, and nothing is checked or pushed.
- **The check** runs on a clean copy of the merge, as a build's. When it fails, nothing is pushed:
  the feature's branch is the base of every later job of the feature, so it only gets a merge whose
  check passed, and the result carries the check with `head_commit` null.
- **The remerge diff**, `git show --remerge-diff --no-color --format= <merge>` as root in its own
  repository: what the agent chose against what git's own merge would have left, and nothing for
  the files that merged cleanly, cut beyond 1 MiB with a line saying so. A git that cannot make it
  is a failure outside the job, before anything is pushed.
- **The push**, without force, of the merge to the feature's branch, and the result with the
  remerge diff. No pictures are taken.

Every root git command runs in `repo`, or in `/run/devflow/<job>/` for the clone that makes it,
with `-c core.hooksPath=/dev/null`, `HOME` that folder and no system or global config, and never in
the agent's copy. The push key reaches git only in the environment of the clone and the push, as an
`Authorization` header for the clone address through `GIT_CONFIG_COUNT`, never in an argument, a
file or a remote's address, and is replaced by `[redacted]` in anything git says that is kept.
Every git command in the copy runs as `agent` with hooks, `core.fsmonitor` and commit signing off,
so nothing the agent planted runs or stops the worker's commit. A worker that is stopped mid job
removes the folders and sends nothing: the run is found lost and the card handed out again, on its
branch if it was pushed. A step that fails for another reason, a fetch, a clone or the worker
itself, is a failure outside the card, so the card is queued again.

**Publishing.** One self-contained file carrying its own .NET runtime, so a project's image needs
no .NET, for each Linux a worker runs on:

```zsh
dotnet publish src/DevFlow.Worker -c Release -r linux-arm64 -o <folder>
dotnet publish src/DevFlow.Worker -c Release -r linux-x64 -o <folder>
```

Naming a runtime makes it self-contained and one file, with its symbols inside it, and
`InvariantGlobalization` means the image needs no ICU either. `RuntimeIdentifiers` in the worker's
and `DevFlow.Protocol`'s project files, and the single-file analyzer turned off, keep one
`packages.lock.json` that holds for the build and the publish alike, on any machine: the analyzer
comes in an implicit package, `Microsoft.NET.ILLink.Tasks`, that a source-built SDK, such as
Homebrew's on the Mac mini, serves with another hash than nuget.org's, which fails a locked
restore on one side or the other.

**Running it** by hand, against Dev flow from "Running it" above, with a key made on a project's
page:

```zsh
DEVFLOW_URL=http://127.0.0.1:8084 DEVFLOW_PROJECT_KEY=<the key> dotnet run --project src/DevFlow.Worker
```

**Its tests** are `tests/DevFlow.Worker.Tests/`, run by the same `dotnet test` as Dev flow's own,
with the same two database strings. `Build/` runs whole build jobs on a bench, `BuildSupport.cs`:
Dev flow in process handing out a card, the fake code host's real bare git repository as the
remote, folders of the test's own, both users' processes recorded, and an agent that writes and
commits as the test says. `BuildJobTests` checks the copy, the agent, the check, the push and the
result; `Screenshots/ScreenshotJobTests` the pictures, on the same bench with the project's start
faked by `FakePageStarter`, its url by `FakeProbe` and Playwright by a script on the agent's `PATH`
that records what it is asked and writes a PNG, and with the process group ended for real where
`setsid` exists, on Linux, skipped on macOS, which has none; `GitSafetyTests` that root runs git
only in its own repository, that hooks and an
`fsmonitor` the agent planted never run, that the push key never reaches anything the agent can
read, and that a push is never forced or to the default branch; `ConflictBuildTests` a conflict job,
on a feature staged resolving with real conflicting commits, its copy, the one merge commit rule,
the check, the push and the remerge diff; `BuildPartsTests` the branch
names refused, the result sent again while Dev flow is away, and the output's tail. The test
machine has one user, so both users are the test's own there; that the agent cannot read root's
files or environment is checked on the built image, by `base-image.test.sh`. `WorkerLoopTests` runs the worker against Dev flow in process,
on a database of its own, through `DevFlow.Tests`' harness, which that project references, and
against a Dev flow played from a script, on a clock the test moves. `ClaudeAgentTests` runs
`ClaudeCodeAgent` on a fake process starter answering with Claude Code's answers recorded in
`Claude/`, whose README says where they come from; `ProcessStarterTests` checks that a real process
gets only the environment given. `ProtocolTests` checks that Dev flow's routes take the shared
types and that both sides write them as `contracts/devflow/openapi.json` says, `ProjectFileTests`
reads `project.json` and keeps `ProjectFile` and its schema in step, and `PublishTests` publishes
for both runtimes, in locked mode, in a folder of its own.

## Claude login

The Claude login is the token the owner makes with `claude setup-token`, which every run of his
is handed so it runs on his subscription. Why it is held by Dev flow, and how it reaches a run, is
"Workers" in `docs/devflow-architecture.md`.

**As the owner does it.** In a normal terminal, run `claude setup-token`, and paste what it prints
into "Claude login" on the Projects tab, then "Save". The field empties and the section says when
it was given and when to renew it, a year later. A month before then the section, the nav's dot
and the front page's card say it is due: make a new token the same way and "Replace" the old one.
"Remove" forgets it.

**One per user**, not per project, since the subscription is the user's. The token is sealed in
the vault, see "The vault", under the purpose `claude-login`, and nowhere else.
`ClaudeLogins`, in `src/DevFlow/ClaudeLogin/`, does everything with it, through the door as the
user.

**Its year is counted from when it was given.** Claude Code does not say when a setup token runs
out: `claude auth status --json` reports `loginExpiresAt` as `null` for one. So Dev flow records
`given_at` and counts: `renew_by` is `given_at` plus 365 days, the state is `due_soon` from 31
days before `renew_by`, and `expired` from `renew_by` on. Replacing it starts the year again. The
clock is the app's `TimeProvider`, so a test moves it.

**The check on save** is only that it looks like a setup token: `sk-ant-oat` followed by at least
one more visible ASCII character, no space or line break, at most 512 characters, once the space
around a paste is dropped. Dev flow does not ask Anthropic whether it works; a worker checks the
login before every run.

The routes, in `ClaudeLoginEndpoints`, the user's, origin checked:

- `GET /devflow/api/claude-login` answers
  `{"set": true, "given_at": "<time>", "renew_by": "<time>", "state": "ok"}`, with `state` one of
  `none`, `ok`, `due_soon` and `expired`, and both times null with `none`. Never the token.
- `PUT /devflow/api/claude-login` with `{"token": "sk-ant-oat…"}` seals it, replacing any before
  it, given now, and answers the status it leaves, `200`. Anything that does not look like a setup
  token is `422` naming `token`, never repeating what was sent, and nothing is stored.
- `DELETE /devflow/api/claude-login` forgets the login and its sealed token, `204`, whether or not
  there was one.

**Opening it for a run** is one method, `ClaudeLogins.ForRun(runUserId)`, and nothing else opens
it. It opens through the door as the run's user, so no one else's is visible, and the vault's
binding to the user it was sealed for refuses a token moved to another, with `VaultException`. It
answers a `ClaudeToken`, which prints as `ClaudeToken { sealed }`, so a log line or an exception
naming it does not reveal it. It is called only when a job is handed out, so the token leaves Dev
flow only inside a job's answer to its own user's worker, see "Jobs". It opens an expired login
too, since its expiry is only counted; handing out reads the status first, and hands out nothing
past `renew_by`.

The table, from `migrations/0008_claude_logins.sql`, guarded like every scoped one:
`claude_logins`, `user_id`, its primary key, `secret_purpose`, always `claude-login`, and
`given_at`. `(user_id, secret_purpose)` references `secrets (user_id, purpose)` with
`ON DELETE CASCADE`, so a login cannot be recorded without its sealed token, and removing the
token removes the login.

## Jobs

A job is one card, or for a conflict or a whole feature's review or fix one feature, or for a reading the cards of a feature or a card on its own, handed to one worker, with what the worker needs
for it and nothing more; a run is Dev flow's record of it, from when it was handed out to when its result came back or it was
found lost. Why it is built so is "Handing out work" in `docs/devflow-architecture.md`. It is
`Dispatcher`, `JobStore`, `RunStore`, `JobEndpoints` and `FakeWorkerEndpoints` in `src/DevFlow/Jobs/`,
the brief in `src/DevFlow/Briefs/`, and the job's, the result's and the report's types in
`DevFlow.Protocol`'s `Jobs.cs` and `Report.cs`, which the worker program shares.

Every route below is the worker's, under the worker scheme, see "Workers". A job's own routes
answer only the worker holding it, named by `worker_id`, while it runs, and mark that worker seen;
anything else, another worker of the same project, a job of another project or user, or one that
has finished, is the same `404` "No such job". No `worker_id` is `422`.

**Handing out.** `POST /devflow/api/worker/jobs/next`, with `{"worker_id"}`, marks the worker seen,
then answers `204`, no work, when:

- the worker needs a rebuild, see "Starting a worker";
- the project's user has no Claude login, or one past `renew_by`; one due soon still works;
- the project has no card that is `queued`, waits for nothing, and was picked with its body,
  and, for a card of a feature, is let start by its feature, see "A feature's cards" below; such a
  card is built only once it was read, and read first otherwise, see "Reading a card" below;
- or the code host does not answer something a job needs: the repository's default branch,
  `.devflow/project.json` on it, valid as "The format of .devflow/" says, its clone address, the
  app's committer, a push key, or, for a feature's first card, the feature's branch. Each is logged as a warning naming what was missing, and the
  worker asks again on its next turn.

Otherwise it takes the first such card in the order "Cards" gives, high, none, low, each oldest
first, with `SELECT … FOR UPDATE SKIP LOCKED`, so two workers asking at once never get the same card
and neither waits, passing over a card that still has a run under way, which a read can leave
queued; moves it to `building`; records a run for the worker; and answers `200` with
`Cache-Control: no-store`, the job, a `DevFlow.Protocol` `Job`:

| Field | What it is |
|---|---|
| `job_id` | The run's id, which the job's routes take |
| `kind` | `build`, `review`, see "Review", `fix`, see "Fixing", `conflict`, see "A conflict" under "The end of a feature", `feature_review`, see "Reviewing a feature", `feature_fix`, see "Fixing a feature" there, or `read`, see "Reading a card" below |
| `card` | `number`, `title` and `body`, as the card was picked; for a conflict or a whole feature's review or fix, the feature's parent's; for a reading, the feature's parent's, or the card's on its own |
| `repository` | `owner`, `name`, `clone_address`, where git fetches and pushes it, and `default_branch` |
| `branch` | `devflow/<number>-<slug>`: the title lowercased, each run of anything but ASCII letters and digits one hyphen, trimmed, cut to 40 characters; `devflow/<number>` when nothing is left |
| `base` | The branch the card's branch is made from: the default branch, or the feature's branch for a card of a feature |
| `brief` | What the agent is asked, see "The brief" below |
| `report_schema` | `contracts/devflow/report.schema.json`, the report's form; for a review the verdict's, and for a reading `contracts/devflow/reading.schema.json`, the reading's |
| `check` | `check` from the project's `.devflow/project.json` on its default branch, read as the app; empty for a reading, which runs none |
| `claude_token` | The run's user's Claude token, see "Claude login" |
| `push_key`, `push_key_expires_at` | A key the app made for this job, narrowed to fetching and pushing the repository's code, and when it stops working, an hour on |
| `committer` | `name` and `email`, the app's bot, `<app-slug>[bot]` and `<id>+<app-slug>[bot]@users.noreply.github.com`, read once from GitHub's `GET /users/<app-slug>[bot]` and kept; `devflow-fake[bot]` with a fixed address in the fake mode |
| `limits` | `turns` and `minutes`, the project's `run_turns` and `run_minutes` when the job was handed out, 200 and 90 by default, and `budget_usd`, 25 for every job until part 8's budget |
| `model` | The model Claude Code runs on, given as `--model`: the project's `model_building` for a build, a fix, a conflict and a feature's fix, its `model_reviewing` for a review of a card or of a feature, its `model_reading` for a reading, `opus` by default; read when the job was handed out |
| `to_resolve` | For a conflict, `feature_commit`, the commit the feature's branch is at, and `default_commit`, the default branch's commit to bring in; null on any other job |
| `to_review_feature` | For a whole feature's review, what it reads besides the code, see "Reviewing a feature"; null on any other job |
| `to_read` | For a reading, `cards`, each card it reads, `number`, `title` and `body` as picked, in the feature's order, which the reading must answer for each exactly once; null on any other job |

The model and the limits are read from the project's settings in the transaction that takes the
card, so the job carries them as they are then, and the run keeps them whatever is changed after;
the run records its `run_minutes`, from `migrations/0037_models_and_limits.sql`.

Everything the job needs from the code host is read before the card is taken, so the row lock is
held for one short transaction and never while GitHub answers; a push key made for a card another
worker took first is dropped unused. The project's page says when the Claude login stops work:
`GET /devflow/api/projects/{id}/workers` answers `held`, `no_claude_login`, `claude_login_expired`
or null, and the Workers panel shows it, see "The pages".

**A feature's cards.** A card of a feature is let start only while its feature is `queued` or
`building`, no card of the feature has a run under way, and every card before it in position
order is `done`, `stopped` or held, `CardStore.HeldBackBy`. A card before it in any other state,
queued, ignored, under way or waiting for the owner, holds it back, and the page shows a queued card
with no reason of its own as "Waits for #<n>, earlier in the feature", naming the nearest such card,
computed when the cards are read and never stored. A stopped or held card holds back only itself,
and what is blocked by it on GitHub, which waits as `waits_for` says; so does a card `refused` as too
thin to build, see "Reading a card". Nothing of a feature is let start while a run of the feature
itself is under way, its reading among them. Between features and cards on their own the order
above stands: reviews of cards, then of whole features, then fixes, then conflicts, then readings,
then builds, each by priority and age.

**A feature's branch.** Before a build is taken, the dispatcher looks at the card it would take,
`FeatureBranchStore.Due`; when that is a feature's card and the feature has no branch made, it
makes `feature-<parent>-<slug>`, the slug by the card branch's rules from the parent's title as
picked, at the default branch's head, as the app with the job's push key, through
`ICodeHost.MakeBranch`, outside the lock. GitHub's side is `GET /repos/{owner}/{repo}/git/ref/heads/<default>`
then `POST /repos/{owner}/{repo}/git/refs` with `ref` and `sha`, tested against
`contracts/github/ref.json` and `ref-created.json`. Making it is claimed on the feature first,
setting `branch` and moving it to `building`, so a second worker asking meanwhile finds the claim
and asks GitHub nothing; a claim with no commit recorded after two minutes is taken over. Made, the
feature records `branch_base_commit`. A branch of that name already there, GitHub's `422`
"Reference already exists", is never taken: the claim is dropped and the feature is
`waiting_for_owner`, "A branch named <name> already exists"; until "The feature's page" offers a
way on, taking the label off the parent and putting it back starts the feature again once the
branch is gone. Any other refusal, or no answer, drops the claim, queues the feature again and is
no work now. Each card of the feature is then built from the feature's branch at its head, on its
own `devflow/<number>-<slug>`, the run's `base` the feature's branch.

**Reading a card.** Every card is read before anything is built, whatever the project's settings.
Why is "Reading a card" in `docs/devflow-architecture.md`. It is `ReadingStore`, `ReadingText` and
`QuestionComments` in `src/DevFlow/Readings/`, `Briefs/reader.md`, `RunStore.JudgeRead`, the worker's
`Read/ReadJob.cs`, and `DevFlow.Protocol`'s `Reading.cs`.

- **When.** A card is due a reading, `ReadingStore.Due`, when it would be free to start, as above,
  and has no reading that came back in its form, `ReadingStore.IsRead`, or is to be read again,
  `ReadingStore.ReadAgain`, below; a build takes only a card whose newest such reading is `ready`
  or `questions` and that is not to be read again, `ReadingStore.ReadToBuild`, so a card read as too
  thin is never built, however it comes to be queued again. A reading is handed out after
  conflicts and before builds, since it is short and frees work. The first card due by the queue's
  order decides what is read: a card on its own is read alone, in a run for it by its number; a
  card of a feature is read with every queued card of its feature that has no reading, in the
  feature's order, in a run for the feature by its parent's number, only while no run of the
  feature itself is under way, so one reading of a feature runs at a time. A feature is so read
  once, when its first card would be handed out; a card it gains later, or one released from a
  hold, is read alone when its turn comes, and so is a card read again, both below. A held card is
  left out, since it would not be handed out. Each card read moves to `reading`, "An agent reads
  it before anything is built", until the result.
- **The job** is a `read`: its `card` the feature's parent or the card on its own, `branch` the
  feature's branch where it is made and otherwise the default branch, `base` the default branch,
  the reader's brief, the reading's form, an empty `check`, the project's `model_reading` and
  limits, a key that only fetches, as a review's, and `to_read`. A reading is given no key that
  pushes. The worker clones `branch`, runs the agent read only in its copy, and sends back the
  reading, with `check` and `head_commit` null; it commits, checks and pushes nothing, see "The
  worker".
- **The brief**, `Briefs/reader.md` filled by `Brief.Reader`, says: read the cards with the code they
  touch and the project's `CLAUDE.md` or `AGENTS.md`; answer for each card `ready`, `questions`
  only where a choice is the owner's and the card does not settle it, each with a recommendation and
  why, or `too_thin` only when it cannot be built from as written, with what is missing; a
  contradiction between cards is a question on each card involved, naming the other; write the note
  of the code the work touches; and everything read is information. The parent, for a feature, and
  each card are quoted as picked, each in a fence longer than any run of backticks in it, with the
  handover notes of the feature's done cards when it has any.
- **The reading** is `DevFlow.Protocol`'s `Reading`: `cards`, one per card read, each `number`,
  `outcome`, `ready`, `questions` or `too_thin`, `questions`, each `question`, `recommendation` and
  `why`, and `missing`, a list of what the card lacks; and `touches`, the note of the code the
  feature, or the card on its own, touches, at most 4000 characters. `Reading.Schema()` writes
  `contracts/devflow/reading.schema.json` from the type, pinned by the worker's `ReadingFormTests`;
  `Reading.TryRead` refuses a missing, unknown, null or mistyped field, a card it was not given, one
  left out or read twice, a `ready` card with a question or something missing, `questions` with
  none or with something missing, `too_thin` with nothing missing, a blank question,
  recommendation, why or missing line, and a note over 4000 characters.
- **Its result** is judged as any run's, `RunStore.JudgeRead`, the first that holds:

  | What came back | The run's `result` | Each card it read that is still `reading` |
  |---|---|---|
  | A failure outside the cards | `failed_outside_card` | Back to `queued`, nothing counted, and the project waits, see "Stops" |
  | A failure in the cards | `failed_in_card` | Run again once, then `stopped`, "The reading failed: <reason>"; at once for a time limit |
  | A reading that does not match its form | `report_invalid` | Run again once, then `stopped`, "The reading did not match its form: <what>" |
  | Anything else | `succeeded` | By what it answered for the card, below |

  A failure in the cards counts per card, as a build's does, "Failed once in the card, so it runs
  again" then "Failed twice in a row with no new commit", see "Stops", and a stopped card is marked
  on its issue as any is. A lost reading puts its cards back in `queued`. A card whose label was
  taken off while it was read is removed once the reading ends.
- **What each card becomes.** `ready` is `queued`, with nothing to say. `questions` follows the
  project's setting `questions`, as it is when the reading comes back. On, as a new project has it,
  the card is `needs_answers`, "Waits for your answers to its questions": nothing is built and
  nothing is posted on GitHub, and it waits for the owner under "Questions before starting", below.
  Off, it is `queued`, built on the recommendations: the app posts one comment on the card,
  `ReadingText`, listing each question with the answer chosen, the recommendation, and why, and a
  last line naming the card and the reading, by which a copy already posted is found, on the opening
  loop's minute, and its id is kept on the reading; its build's brief carries them, see below. A
  card already waiting for answers keeps waiting when the setting is turned off. A queued card whose
  newest reading's questions nobody answered, neither the owner nor the app's comment, is held for
  answers again on the project's read while the setting is on, `ReadingStore.HoldForAnswers`, so a
  card picked again after its label came off and on is never built on the recommendations by
  accident. Inside a feature, a card that needs answers holds back every later card, as a queued
  card does, and holds the feature's end. `too_thin` is
  `refused`, "Too thin to build; it lacks: <each thing missing>", and is not built; a refused card
  holds back only itself in its feature, as a stopped one does, and holds the feature's end.
- **A refused card is marked** by `RefusalMarker`, on the opening loop's minute and after each fake
  worker run: the app, with a `Post` key, adds the project's attention label to its issue, and
  comments once for the reading that refused it, `RefusalText.Comment`: "Dev flow read this card and
  will not build it as it is written: it is too thin. It lacks:", each thing missing on a line of its
  own, made data as a stop's reason is, "Edit the card, or comment here, and it is read again.", the
  card's page, and a last line, "Refused by Dev flow for #<card>, reading <run id>.", by which a copy
  already posted is found, so a lost answer never posts it twice. Its id is kept on that reading's
  row, `comment_id`, so Dev flow's own comment never reads the card again. `StopMarker` leaves a
  refused card's label on, and takes it off once the card is queued again, as it does a stopped
  card's.
- **On "Waiting for you"** it is listed under "Refused", after "Stopped", `refused_cards`, each
  `{"project_id", "project_name", "owner", "number", "title", "web_url", "missing", "refused_at"}`:
  `src/refused-card.tsx`, the package's `Card` titled with its title, the `failed` status "Refused",
  its repository and number, "What is missing" with each line as a list, when it was read, "Edit the
  card or comment on it on GitHub, and it is read again.", and "Open on GitHub", its issue. Nothing
  is done from the page. It counts on the tab, in `count`, on the nav's dot, and on the front page
  among `waiting`.
- **Questions before starting.** On "Waiting for you" a card that needs answers is listed first,
  under "Questions before starting", `question_cards`, each `{"id", "project_id", "project_name",
  "owner", "number", "title", "web_url", "feature", "reading_id", "questions", "read_at"}`, its
  `feature` `{"number", "title"}` or null for a card on its own, and its `questions` those of the
  reading `reading_id`, each `{"question", "recommendation", "why"}`, in order: `src/question-card.tsx`,
  the package's `Card` with the attention outline, the needs you status "Needs your answers", its
  repository and number, "Card of #<n> <title>" for a feature's, and each question on an inset, what
  it asks, "Recommended" with the recommendation, "Because" with why, "Accept", and "Or your own
  answer", a box; accepting sets what was typed aside and typing takes the acceptance back. "Send
  answers" is pressed once every question has an answer, with GitHub linked; a refusal shows above
  it, and what was typed is kept. It counts on the tab, in `count`, on the nav's dot, and on the
  front page among `waiting`.
- **Answering**, `POST /devflow/api/cards/{id}/answers`, `AnswerEndpoints` in `src/DevFlow/Acting/`,
  under the `Api` policy and the origin check, with `{"reading_id", "answers"}`: one answer for each
  of the reading's questions, in order, each `{"accept": true}`, the recommendation, or
  `{"text": "..."}`, the owner's own, 1 to 4000 characters once trimmed, never both. In order: a card
  the caller does not have is `404`; no `reading_id` or `answers`, `422`; a card that does not need
  answers, `409`; a `reading_id` that is not its newest reading, `409`, so a page showing older
  questions is never answered; `answers` without exactly one valid answer for each question, `422`
  naming `answers`; GitHub not linked or the link expired, `409`. Then the answers are posted as one
  comment on the card's issue with his linked token and never as the app, `AnswerText`: "My answers
  to the questions Dev flow's reading asked of this card before building it.", each question
  numbered with "Answer:" and the answer, "(recommended)" where it was accepted, each made data as a
  stop's reason is, and a last line, "Answered on Dev flow for #<card>, reading <run id>.". Before
  posting, a comment of his on the issue that is exactly that one, left by a send whose answer was
  lost, is kept instead, so it is never posted twice; when the issue's comments cannot be read,
  nothing is posted, `503`. Once posted, in one transaction with the card's row locked: the answers
  are kept on the reading, `answers` and `answered_at`, the comment's id as its `comment_id`, so it
  never reads the card again, and the card is `queued`, not read again; `204`. A card that moved on
  while GitHub answered is `409`, the comment posted and nothing kept; GitHub refusing is `409` with
  its reason, and not answering `503`. Its build's brief carries his answers under "Answers to its
  questions", saying he answered each, and each accepted one as the reader's recommendation he
  accepted; the card's page shows "Built on your answers", each answer, "(recommended)" where
  accepted.
- **It is read again**, `ReadingAgain` in `src/DevFlow/Readings/`, on the project's read, while
  it has not started: it is `queued`, `needs_answers` or `refused`, and no run of it but a reading
  was ever handed out, `ReadingStore.Started`. Two things read it again. An edit of its body or
  title by a trusted account, which picking records, so the card's `picked_body_sha256` or
  `picked_title` no longer matches what its newest reading was handed,
  `ReadingStore.ChangedSinceRead`; an edit by anyone else ignores the card, as picking does. Or a
  trusted account's comment on its issue newer than its newest reading, past those already taken,
  and never one whose id Dev flow keeps on any of its readings: the questions' comment, the
  refusal's, and the owner's answers posted from the page, which are his own comment but Dev
  flow's. A card that waited for the owner, `needs_answers` or `refused`, moves to `queued`, "You
  commented on it; it is read again next" or "You edited it; it is read again next", and is no
  longer listed on "Waiting for you"; a queued card stays as it is. Comments are added to
  `read_again_comments`, after any taken before and not yet read, and `comments_read_through` moves
  past them. A card so changed, or whose newest reading is too thin, is due a reading,
  `ReadingStore.ReadAgain`, before any build, and no build takes it, `ReadingStore.ReadToBuild`;
  nor is it held for answers again, its next reading asking afresh. It is read alone, of a feature
  or not, as a run of the card, so the feature's note is not rewritten by it: its brief gains "The
  feature" with its done cards' handover notes for a card of one, "The code this feature touches",
  or this card, from `ReadingStore.NoteOfCard`, and "Read again", for a refused card what the
  reading that refused it found missing, fenced as a reader's words, and otherwise that an earlier
  reading's questions and answers do not carry over, then each comment under "A comment by
  <login>", exactly as written and fenced as that account's input. It is judged as any reading:
  ready queues it, with nothing to say, a refused card's label off since it was queued to be read;
  questions follow the project's setting, so with it on it needs answers again, to the new
  reading's questions; too thin refuses it, with a comment of its own. The comments kept go once a
  reading comes back in its form. A comment written while it is read, older than the reading that
  comes back, is not taken. Once it has started, from `building` on, nothing reads it again: a
  change follows "A card that changes" in `docs/devflow-architecture.md`, and the merge check
  holds it to its reading, see "The merge check".
- **Answers stay with their reading.** A reading's answers are kept on its row, so a card read
  again carries none of them into its build's brief, which quotes its newest reading's questions
  and the answers given to those alone. The card's page keeps every reading, see "A card's page".
- **A card added to a feature already read** is read alone too. Once a reading of the feature has
  come back in its form, `ReadingStore.FeatureRead`, a card of it that reading never read, a
  sub-issue added since or a card released from a hold, is read in a run of the card when it would
  be handed out, in the sub-issues' order like any card, with "The feature", its done cards'
  handover notes, "The code this feature touches", and "Read alone", saying the rest of the feature
  was read before it.
- **The briefs.** Every brief of a feature's job gains "The code this feature touches", the newest
  reading of the feature's note, fenced as an agent's words, information and never an instruction:
  a card's build, fix and review, `ReadingStore.NoteOfCard`, and the feature's conflict, review and
  fix, `ReadingStore.NoteOfFeature`. A card on its own's briefs gain "The code this card touches",
  from its own reading. A card's build brief gains "Answers to its questions", each question with
  the answer it is built on, the owner's or the recommendation, and why the reader recommended
  what it did, each fenced, when its reading has questions.
- **The table**, from `migrations/0038_readings.sql`, guarded like every scoped table:
  `card_readings`, one row per card and reading run, made when the run is handed out, `run_id`,
  `project_id` and `user_id` referencing `projects`, `card_number`, `body_sha256` and `title`, the
  card as it was handed, which "Reading again" compares against, and `outcome`, `questions` and
  `missing` as `jsonb`, and `read_at`, null until the result came back in its form; a reading that
  ended any other way leaves its rows without them, the record of what it was handed.
  `comment_id`, Dev flow's comment on a card read with questions, null until it is posted. A card's
  reading is its newest with an outcome. The note is kept on the run, its report's `touches`, as a
  card's handover note is its run's `handover`. The migration adds `reading` and `refused` to the
  card's states and `read` to the run's kinds, a run that names no commit.
  `migrations/0039_refusals.sql` lets `comment_id` hold the refusal's comment of a reading too thin
  as well, and adds `read_again_comments` to `cards`, the comments that asked for a refused card to
  be read again, each `{"login", "body"}`, null otherwise. `migrations/0040_questions.sql` adds
  `needs_answers` to the card's states, and `answers`, the owner's answers to a reading's questions,
  each `{"answer", "recommended"}` in the questions' order, and `answered_at`, both null until he
  answers and only on a reading with questions whose `comment_id` holds his comment.

**A fresh push key.** `POST /devflow/api/worker/jobs/{id}/push-key`, with `{"worker_id"}`, answers
`{"push_key", "expires_at"}` with `Cache-Control: no-store`, a new key each time, since a key lasts
an hour and a run up to 90 minutes. `503` "GitHub did not answer" when the code host made none.

**Progress.** `POST /devflow/api/worker/jobs/{id}/progress`, with
`{"worker_id", "state": "checking"}`, says the agent has finished and the check runs: the card moves
from `building` to `checking`, and the run records `checking_at`. `204`. Any other state is `422`.

**The result.** `POST /devflow/api/worker/jobs/{id}/result`, once, whatever happened:

```json
{
  "worker_id": "<uuid>",
  "outcome": "succeeded",
  "report": { "outcome": "done", "...": "..." },
  "failure": null,
  "check": { "exit_code": 0, "output": "<the last 200 lines>" },
  "branch": "devflow/12-show-the-empty-state",
  "head_commit": "<40 or 64 hex>",
  "auth_method": "oauth_token",
  "usage": { "usage": { "input_tokens": 41, "...": "..." }, "total_cost_usd": 1.83, "num_turns": 23 },
  "duration_ms": 184213,
  "stderr": "<the last 200 lines>"
}
```

`outcome` is `succeeded`, with `report` and `failure` null, or `failed`, with `report` null and
`failure` `{"reason", "in_card", "timed_out", "resets_at"}`: whether the card's own work caused
it; whether the run reached its time limit, which must be in the card, null read as false; and when
Claude's usage limit resets, which must be outside it, null when the answer gave none, see
"Stops". `check` is null when the
check did not run, or holds both fields. `branch` must be the job's, `head_commit` null or a full
commit id, `auth_method` null or at most 255 characters, `usage` an object, empty when Claude gave
none, `duration_ms` 0 or more, and `stderr` text. `remerge_diff`, a conflict's, is null or text,
kept on its run cut beyond 1 MiB as `RemergeDiffs.Cut` cuts it, and not kept for any other run. A
field missing or breaking a rule is `422` naming it, and nothing is stored. Everything is stored on the run as it came, the check's output and
stderr cut to their last 200 lines and 64 KiB; and the token counts, `total_cost_usd` and
`num_turns` of `usage` also in columns of their own. Two things are not kept as they came, since
Postgres cannot hold them and JSON allows both: a NUL, in any text and in any string or name inside
`report` and `usage`, and half of a surrogate pair, each replaced by U+FFFD, so one byte from a
process can never keep a result from being stored. Then it is judged, on what was stored, the
first that holds:

| What came back | The run's `result` | The card |
|---|---|---|
| A failure outside the card | `failed_outside_card`, with its reason | Back to `queued`, nothing counted against it |
| A failure in the card | `failed_in_card` | `stopped`, "The run failed: <reason>" |
| A report that does not match its form | `report_invalid` | `stopped`, "The report did not match its form: <what>" |
| A report with `stopped` | `stopped` | `stopped`, "The agent stopped: <stopped_reason>" |
| No check | `check_failed` | `stopped`, "The check did not run" |
| A check that failed | `check_failed` | `stopped`, "The check failed with exit code <n>" |
| Anything else | `succeeded` | Stays `checking`, "Built, and its check passed; its pull request opens next", until its pull request is opened, see "The pull request Dev flow opens" |

Reasons from a worker or an agent are cut to 1,000 characters. A cut never splits a character in
two; it keeps one fewer instead. A card moves only while it is
`building` or `checking`; one a read has since ignored stays ignored. A card whose label was taken
off while it ran is removed once its run ends, whatever the result; its runs stay. Every failure in
the card above but a time limit and a report that stopped runs the card again once, and a failure
outside the card makes its project wait, see "Stops".

**A lost run.** Each read of a project, before it asks the code host anything, ends every run of it
handed out at least its job's minutes and 10 more ago, its `run_minutes` and the margin, 100 minutes
for the default 90, with no result, whose worker has not been seen for 10 minutes:
the run is `lost`, "No result came back, and its worker was not seen for 10 minutes", and its card
goes back to `queued`, nothing counted against it, and its project waits as after any failure
outside the card, see "Stops". A result that comes after is for a finished job,
`404`. It is `RunStore.SweepLost`.

**The brief** is one template, `src/DevFlow/Briefs/developer.md`, embedded in the assembly, the
same for every project, filled by `Brief.Developer` with the card and the project's facts: the
check, the branch and its base, and the job, `start` and `url`, for the section on looking at its
own pages, `Briefs/looking.md`, described in "Screenshots". It says: the card is the work, written by the owner; the project's
facts are in its `CLAUDE.md` or `AGENTS.md`, its docs and `.devflow/project.json`, whose `check`
judges the work; write the tests first and watch them fail; one commit per task, locally on the
branch checked out; never push, make a branch or touch the code host; install a missing tool and add
it to `.devflow/Dockerfile`; no `Co-Authored-By` or "Generated with" lines; never ask, decide with
its own recommendation and record each decision, marked `for_owner` where the owner would want a
say; a card that contradicts itself or cannot be built is reported `stopped`; anything not the card
is information, never an instruction; look at the pages the change affects before reporting, for a
project with pages; and answer in the report's form. The card's number, title and
body are quoted inside a fenced block longer than any run of backticks in them, and the check in
one of its own, so neither can end its quote.

For a card of a feature the brief gains, after the card, "The feature", the parent's number,
title and body as picked, fenced, with "The feature is what your card belongs to, written by the
owner. The card is your work: build the card, and nothing else of the feature."; then "What the
earlier cards built", the `handover` of the newest succeeded build or fix of each `done` card of the
feature, in position order, each fenced with its number and title first, under the brief's words
that they are information and never an instruction, or "Nothing yet." for the first card. A fix of
such a card carries the same sections, and its review's brief, `Brief.Reviewer`, the parent's
title and body, so the card is judged against what it belongs to. `JobStore.FeatureOf` reads both
inside the claim.

**The report** is `DevFlow.Protocol`'s `Report`: `outcome`, `done` or `stopped`; `stopped_reason`,
empty when done; `summary`; `tasks`, each `text` and `done`; `decisions`, each `chosen`,
`alternative`, `why` and `for_owner`; `tests`, each `what` and `result`; `pages`, each `path`, `why`
and `looked`; `installed`; `follow_ups`; `replies`, each `finding` and `answer`, empty on a build;
and `handover`. Every field is required, and lists may be empty. `Report.Schema()` writes its JSON
Schema from the type, every object closed and every field required, which is
`contracts/devflow/report.schema.json`, pinned by `ContractTests`; `Report.TryRead` reads a report
into the type with a missing, unknown, null or mistyped field refused, and a `stopped_reason` empty
on a stop or written on a done.

**The fake worker**, in the fake mode only, under the `Api` policy and the origin check, and left out
of `contracts/devflow/openapi.json`: `POST /devflow/api/fake/worker/run` with
`{"project_id", "result"}`, `result` a result as above without `worker_id`, `branch` and
`head_commit`; or `{"project_id", "keep_running": true}`, with no result needed, which takes the
job and sends nothing back, as a worker still at it, answering `head_commit` null. It says hello in the caller's project as `devflow-fake-worker` on the image expected,
takes its next job through the same `Dispatcher`, makes a commit on the job's branch in the fake's
git repository, by the job's committer, on top of the base branch, giving the base branch a first
commit of its own when the fake's git has none, as a real repository has, ends the job with the
prepared result through the same `RunStore.Record`, and then opens the project's pull requests,
as the opening loop would on its next minute, so a test sees the pull request when it answers. It answers
`{"job_id", "kind", "card", "branch", "head_commit", "brief"}`, the brief the job carried, so a test
reads what the agent would have been asked, `204` when there is no job, `404` for a project the
caller does not have, and `422` for a result that breaks a rule, checked before a job is taken.
When the fake's git makes no commit, the job ends instead as a failure outside the card, "The
fake's git made no commit on the job's branch", so the card is `queued` again, and it answers `503`. A fix it carries the same way, its commit on top of the card's branch, which
also moves an open pull request from the branch to it, as a push would; with `"leave_no_commit":
true` it makes none and names the commit it was handed, as an agent that changed nothing. A reading
it ends with the prepared result, the reading as its `report`, changing nothing and naming no
commit, and then posts the questions' comments and marks the stops, as the opening loop would.

**The table**, from `migrations/0012_runs.sql`, guarded like every scoped table: `runs`, `id`,
`project_id` and `user_id` referencing `projects`, `card_number`, by number so a removed card leaves
its runs, `worker_id` referencing the project's `workers`, `kind`, `branch`, `base`, `started_at`,
`checking_at`, `finished_at` and `result`, null while it runs, `reason`, `report` and `usage` as
`jsonb`, `check_exit_code`, `check_output`, `head_commit`, `auth_method`, `input_tokens`,
`output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `cost_usd`, `turns`,
`duration_ms` and `stderr`; and, from `migrations/0014_card_pull_requests.sql`, `check_command`,
the check the job carried, null for a run handed out before it. A unique index allows one run under
way per card, a second guard beside the lock. From `migrations/0030_conflict_runs.sql`, a run is
for a card or for a feature: `card_number` is null for a feature's, and `feature_number`, its
parent's number, is set instead, exactly one of the two by a check; `kind` takes `conflict`, which
also needs `from_commit`, the feature's head it starts from, and `default_commit`, the default
branch's commit it brings in; `remerge_diff` is a conflict's, see "A conflict"; and a second unique
index allows one run under way per feature. From `migrations/0031_feature_reviews.sql`, `kind` takes
`feature_review` too, a feature's run that needs `head_commit`, the commit it reviews, see
"Reviewing a feature". From `migrations/0032_feature_fixes.sql`, `kind` takes `feature_fix`, a
feature's run that needs `from_commit`, the feature's head it starts from, see "Fixing a feature"
under "Reviewing a feature".

## The pull request Dev flow opens

A card that built and passed its check gets a pull request, opened as the app, and waits for the
owner with it; each read follows it to merged or closed. Why it is built so is "How a card reaches
its pull request" in `docs/devflow-architecture.md`. It is `PullRequestOpener`, `OpeningLoop` and
`PullRequestText` in `src/DevFlow/PullRequests/`, `CardStore.Follow` in `src/DevFlow/Cards/`, and
the following in `ProjectReader`. A card of a feature's pull request goes from its branch into
the feature's branch, the run's base, and its Summary says "Card #<n> of the feature #<parent>."
in place of `Closes #<n>`, since GitHub's closing keywords close an issue only from a pull request
into the default branch; the rest of the body is as below.

**When.** A card is waiting for its pull request when it is `checking` with none, and its latest
run's result is `succeeded`: a report in its form that says `done`, and a check that exited 0. Any
other result leaves the card as "Jobs" says. `PullRequestOpener.OpenPending(project, user)` opens
the pull request of each such card of the project, as the app with a `Post` key, from the run's
branch into its base, the default branch it was built on, titled by the card as it was picked,
not as a draft. Before opening, it lists the open pull requests as the app, and one the app opened
itself, by `ICodeHost.AppCommitter`'s login, from that branch into that base, is reused instead;
one by anyone else is never taken for the card's, and GitHub refuses a second from the same branch,
so such a card waits, logged, and is tried again. A refusal is looked up the same way, since an
opening at the same moment may have made it. Opened or found, the card moves to
`reviewing`, "Its pull request is open; the reviewer reads it next", with `pull_request_number`
and `pull_request_url`, and its review is the project's next job, see "Review", only while it is still `checking` with none and the run is still its latest,
so two openings record one. GitHub not answering leaves the card as it is.

**On the minute.** `OpeningLoop`, a hosted service, lists `poll_targets` every
`DEVFLOW_POLL_SECONDS`, through the door's `WithoutUser`, and opens each project's as its user, one
project's failure never stopping another's; with `0` it does not run. A worker's result is answered
without waiting for GitHub, so its card's pull request opens on the next minute. The fake worker
waits for it. The same minute then posts each review's copy, runs the merge check and opens the
decisions' issues, in that order, see "Review" and "The merge check".

**The body**, `PullRequestText.Body`, in this order: `## Summary`, the report's `summary`, then a
blank line and `Closes #<card>`; `## Tasks`, the report's tasks as a checklist, `- [x]` or `- [ ]`;
`## Decisions the agent made`, each "<chosen>. Alternative: <alternative>. Why: <why>.", a part
left out when the agent wrote nothing for it and one marked `for_owner` beginning `**For you.**`;
`## Verification`, "Dev flow ran `<check>` on <commit, seven characters>: exit <code>.", then the
report's tests, each "<what>: <result>", then, when the report names pages, "The pages the change
affects:" with each as "`<path>`: <why>.", one the agent did not look at itself ending
"**The agent did not look at this page.**", then, when the worker tried pictures, "Screenshots the
worker took of the commit, desktop and 390px phone width:" with each as
`![<name>](https://github.com/<owner>/<name>/blob/pr-screenshots/devflow/<card>/<commit, seven characters>/<name>.png?raw=true)`,
the form this repository's agents embed theirs in, and the worker's note, as text, see
"Screenshots"; `## Follow ups`; a list with nothing in it is "None.";
and a last line, "Built by Dev flow for #<card>, run <run id>.". Every text from the report is the
agent's, and data: it is made one line; `&`, `<` and `>` are written as entities; its backslashes
and backticks are escaped, so a backslash of its own cannot undo an escape; a `#` it begins with is
escaped; a closing keyword, `close`, `fix` or `resolve` in any
form, followed by `#12`, `GH-12`, `owner/name#12` or an address, with or without emphasis around
either, has the keyword in a code span; and
an `@` mention is in a code span. So it cannot add a section, insert HTML, close another issue or
notify anyone. The check is the project's own, from its default branch, and is shown in a code
span as it is.

**Following.** Each read of a project, `ProjectReader.Read`, looks at every card that waits for the
owner, or is `reviewing`, with a pull request: one still among the open pull requests it lists anyway costs nothing
more, and one that has left the list is read with `ICodeHost.PullRequest`. Merged, by anyone, on
GitHub or from the page, the card is `done`; closed without merging, it is `stopped`, "The pull
request was closed", or removed when its label was taken off while it ran. One not found is left as
it is, and GitHub not answering fails the read as any other read does. It is stored in the read's
transaction, with the cards.

**What waits.** Each card that waits is in `GET /devflow/api/waiting`'s `cards`, from its row and
its latest succeeded run: `project_id`, `project_name`, `owner`, `number`, `title`, as picked,
`web_url`, `pull_request_number`, `pull_request_url`, `head_commit`, the commit the run built and
checked, `base`, `summary`, `decisions`, those the agent marked for the owner in any build's or
fix's report of the card, once each by what was chosen, the newest words winning, in the order they
first appeared, as the merge check counts them and a merge records them, so a card waiting for one
shows it whichever report marked it, each `{"chosen", "alternative", "why"}` with an empty part
null, `other_decisions`, how many the newest report made that it did not mark, `check`,
`{"command", "exit_code", "commit"}`, `review`, the reviewer's verdict on that commit,
`{"verdict", "summary", "blocking", "commit"}`, `blocking` how many findings block it, or null when
the commit has none, `can_merge`, false while the project is stopped, `changed_at`,
`screenshots`, the pictures of that commit as the card's page has them, or null, and `reason`, why
it waits in the merge check's words, or null. Its run is
the latest build or fix that succeeded, never a review, whose report is a verdict. They count in the list's `count`, in the display data's `waiting` and in
`GET /devflow/api/attention`. A card the merge check merged is `done` and no longer listed; the
list's `decisions` are its decisions to look at, see "The merge check".

## Screenshots

After a build's or a fix's check passed and its branch was pushed, the worker starts the project
from that commit and takes its own pictures of the pages the report names, at a desktop's width and a
phone's. The reviewer judges them with the diff, and the owner sees them on the card's page, on
"Waiting for you" and in the pull request. They are evidence, and never decide anything: a run
succeeds by its check whatever they show. Why it is built so is "The worker's pictures" in
`docs/devflow-architecture.md`. It is `BuildJob.Screenshots.cs` and `Pages/` in the worker,
`PageShots` and `ResultScreenshots` in `DevFlow.Protocol`, and `CardScreenshotEndpoints` in
`src/DevFlow/Cards/`.

**For a project's owner.** Two fields of `.devflow/project.json` say how:

- `start`, a command, run with `sh -c` from the root of a clean copy of the commit, that brings the
  project up and keeps it running, installing its packages first, such as
  `npm ci && npm run dev`. It may run in the foreground or leave servers in the background; they
  are all ended afterwards.
- `url`, `http://localhost` with an optional port and path, where the project answers once it is
  up, such as `http://localhost:5173`. Each page is `url` joined with the page's path.

Either empty means the project has no pages to show, and no pictures are taken. **Login is the
project's business.** `DEVFLOW=1` is the one thing Dev flow sets in `start`'s environment, which
is otherwise built from nothing: the agent's `HOME`, `PATH` and `LANG`. A project whose pages need
a login uses it to come up so they open without one, for example by seeding a test user and
signing every request in as it, and that mode must be off whenever `DEVFLOW` is not set, since the
same code runs in production. Nothing a test mode can reach should be real: the copy is a clean
clone of the commit, and only what the project's own start creates is there.

**What the worker does**, after the push:

1. With no `start` or `url`, the note is "This project has no pages to show"; with a report that
   names no page, "The report names no pages". Nothing else happens.
2. The agent clones the pushed commit afresh, through a bundle in root's handover folder, into
   `/work/<job>/pages`, owned by `agent` and checked to be at the commit.
3. `start` runs there as `agent`, through `setpriv` and `setsid` in a process group of its own,
   with the environment above.
4. The worker asks `url` every second, following no redirect, until it answers with any status
   below 500, a login page's redirect included, for at most 3 minutes. `start` exiting with a
   failure ends the wait at once.
5. For each of the report's first 10 pages, in its order, a page whose name an earlier one took
   left out: the address is `url` joined with its path, refused when the join leaves the project's
   own origin; and it is taken full page with
   `playwright screenshot --full-page --viewport-size <w>,<h> <address> <file>`, the image's pinned
   one on `PATH`, never one `npx` would resolve, as `agent` with `PLAYWRIGHT_BROWSERS_PATH`, in
   `/work/<job>/pictures`, each within 60 seconds: `<name>-desktop` at 1280 by 800 and
   `<name>-phone` at 390 by 844. `<name>` is the path lowercased, each run of anything but ASCII
   letters and digits one hyphen, trimmed, at most 60 characters, and `home` for `/`.
6. The process group is ended: `SIGTERM`, and `SIGKILL` 5 seconds later to whatever is left.
7. Root reads each picture through a process of the agent's, `head -c` one byte past 5 MB, and
   keeps it only as a PNG of at most 5 MB.
8. Root commits them in its own repository to `pr-screenshots`, an orphan branch made when the
   repository has none, under `devflow/<card>/<commit, seven characters>/<name>.png`, on top of
   the branch as the job's clone found it, with an index file of its own and nothing checked out,
   as the app's bot; and pushes that commit with a fresh push key, never with force and never to
   the default branch. When the push is refused, the branch is fetched again and the commit made
   once more on top.

**A failure to take them is said, not hidden**, and never fails the job or stops the card: the
note is "Screenshots could not be taken: <why>", with why for each page and size that failed, or
for the project that did not come up, with the last lines its start printed. The pictures taken
are pushed whatever failed beside them. The reviewer sees the note, and may fail the card for it.

A fix's pictures are of the commit it pushed, beside the build's under their own folder; the pull
request body, written when the build opened it, embeds the build's, and the card's page and its
item on "Waiting for you" show the head's.

**In the result**, `screenshots`, `{"names", "note"}`: the pictures pushed, in the report's order,
and the note, null when every page was taken; null when none were tried, as after a failed check.
Dev flow refuses with `422` a list of more than 20, a name twice, or one that is not a page's part
then `-desktop` or `-phone`, and a note that is empty or over 1000 characters. It keeps them on the
run, `screenshots` and `screenshots_note`, migration `0020_run_screenshots`.

**Served** by `GET /devflow/api/projects/{id}/cards/{number}/screenshots/{name}`, under the `Api`
policy, through part 1's checks, `ScreenshotEndpoints.Guard` and `FromHost`: a `name` the card's
newest build that succeeded did not list, a card with no such build, or a project or card the
caller does not have, is `404` without asking GitHub; then `ICodeHost.File` reads
`devflow/<card>/<commit, seven characters>/<name>.png` on `pr-screenshots` as the app, and it is
served as part 1's are, `image/png`, with `nosniff`, `default-src 'none'` and an hour's private
cache, `502` when it is not a PNG of at most 5 MB, `404` when GitHub has no such file and `503`
when GitHub did not answer.

**The fake worker** takes `pictures`, base64 by name, beside its prepared result, and puts each name
the result lists on `pr-screenshots` in the fake's world, under the commit's folder, as a worker
pushes them, with `FakeCodeHost.PutFile`; so an acceptance test sees them on the page.

**The developer's own look.** Before it reports, the developer looks at the pages its change
affects itself, and fixes what is wrong; the worker's pictures after the push stay the evidence,
and the developer's are its own check, which go nowhere. Why it is built so is "The developer looks
too" in `docs/devflow-architecture.md`. For a project with both `start` and `url`, the developer's
and the fix's briefs hold a section after how to work, `src/DevFlow/Briefs/looking.md`, filled by
`Brief`; with either empty, a line that the project has no pages to look at, so a page it lists has
`looked` false. The section gives the commands, the project's `start` and `url` only inside fenced
blocks, as the check is:

1. `start` written to `/work/<job>/self/start.sh` through a here document, and run with
   `DEVFLOW=1 setsid sh` in the background from the working copy, its output in `start.log` and
   its process group's id in `start.pid` beside it;
2. `url` asked every second with `node`'s `fetch`, following no redirect, until it answers below
   500, for at most 3 minutes, as the worker waits;
3. for each page, `npx playwright screenshot --full-page --viewport-size=1280,800 <url><path>
   /work/<job>/self/<name>-desktop.png`, and the same at `390,844` as `-phone`. In a project with
   no Playwright of its own `npx` runs the image's pinned one on `PATH`, whose Chromium is
   installed; the brief says to run `playwright` itself when a project's own cannot find its
   browser;
4. open each picture and look at it against the card, fix what is wrong and look again;
5. stop the project, `kill -- -"$(cat /work/<job>/self/start.pid)"`;
6. in the report, `looked` true only for the pages it looked at.

`/work/<job>/self`, `AgentFolders` in `DevFlow.Protocol`, is made as `agent` by the worker for each
build and fix, beside the copy and never in it, so nothing in it is committed or pushed, and goes
with `/work/<job>/` when the job's folders are removed. Claude Code's environment holds
`PLAYWRIGHT_BROWSERS_PATH`, see "The agent interface". The time spent looking counts within the
run's 90 minutes and 200 turns. A page with `looked` false is shown as "The agent did not look at
this page" on the card's page and in the pull request's body, and the reviewer's brief says to
weigh it; it does not stop the card by itself. `TheAgentCanStartTheProjectAndTakePictures`, in
`tests/image/base-image.test.sh`, runs the brief's commands, `tests/image/looking.sh`, which
`BriefTests` keeps the brief's text, as `agent` in the image on the page project
`tests/image/page-project/`.

## A card's page

`GET /devflow/api/projects/{id}/cards/{number}`, under the `Api` policy, answers a card's whole
story so far, read from what Dev flow stored and never from GitHub, through the door as the caller:
`number`, `title` as last read, `web_url`, `state`, `reason`, `pull_request`,
`{"number", "url"}` or null; `runs`, newest first, each `id`, `kind`, `worker`, the name it said
hello with, `started_at`, `finished_at`, `duration_ms`, `result`, `reason`, `in_card`, for a
failure whether it lay in the card, true for `failed_in_card`, `report_invalid`, `stopped` and
`check_failed` and false for `failed_outside_card` and `lost`, null otherwise, `auth_method`,
`input_tokens`, `output_tokens`, `check`, `{"exit_code", "output"}` or null when it did not run,
and `head_commit`, for a review the commit it reviewed; a reading that read the card is among them,
its feature's for a card of a feature; `report`, the latest build's or fix's
report that reads in its form, or null; `reviews`, newest first, each `run_id`, `commit`,
`verdict`, `summary`, `findings`, `for_owner`, `reviewed_at`, `posted`, and `current`, whether it
is for the card's head commit, so it counts; `rounds`, newest first, each fix as "Fixing" says:
`run_id`, `round`, from 1, or null for the owner's comment, `review_run_id`, the review it
answered, `comments`, his words, `result`, `replies`, from its report, `fixed_commit`, and
`review_after_run_id`, the newest review of that commit. A fix that failed outside the card is
tried again in the same round, so a round may show more than once; `screenshots`, the
pictures the worker took of the head commit, the one its newest build or fix that succeeded left:
`commit`, `groups`, paired by `Screenshots.Group` as part 1's are, each `{"name", "images"}`, and
`note`, the worker's, or null; null when that run tried none; and `merge`, the merge Dev flow made
of its pull request as the app, `{"commit", "approved", "base", "merged_at"}`, the merge commit
from `merges_made`, the card's head it merged, and the branch, or null when it made none, the
owner's own merge from the page among them; `feature`, `{"number", "title"}`, the feature it is
a card of, as last read, or null for a card on its own; `reading`, what it was read as before it
was built, its newest reading in its form, `{"outcome", "questions", "missing", "read_at",
"commented", "answered"}`, each question `{"question", "recommendation", "why", "answer",
"recommended"}`, the answer the card is built on, the owner's once he answered and the
recommendation until then, and whether it is the recommendation, `commented`, whether the comment
for the reading, Dev flow's of its questions or of its refusal or his answers, is on the card yet,
and `answered`, whether he answered its questions; or null before a reading came back, see "Reading
a card"; and `earlier_readings`, the readings before the newest, newest first, each in the same
shape with the answers given to it, empty for a card read once. A project or
card the caller does not have is `404` "No such card". It is `CardStory` and `CardEndpoints` in
`src/DevFlow/Cards/`. The page is "A card's page" under "The pages".

## A feature's page

Why the page reads only what Dev flow stored, and why an answer never reaches the code host, is "A
feature's page" in `docs/devflow-architecture.md`.

**The route.** `GET /devflow/api/projects/{id}/features/{number}`, under the `Api` policy, answers
a feature's whole story so far, read in one transaction through the door as the caller, never from
GitHub: `number`, the parent's, `title` as last read, `web_url`, `state`, `reason`, null when there
is nothing to say; `branch`, `{"name", "commit"}`, null before the branch is made, `commit` the one
its watch recorded after the newest change it judged, `branch_watches.last_commit`, or the one it
was made at before any, null for a watch that began before the column and has judged nothing
since; `pull_request`, `{"number", "url"}`, its pull request to the default branch, null before
one is opened; and `cards`, in position order, each `number`, `title`, `web_url`, `state` and
`reason` as the Cards route answers them, `fix_rounds`, 0 to 3, `input_tokens` and `output_tokens`,
summed over every run of the card, `screenshots`, as "A card's page" answers them, whose pictures
are read through that card's `…/screenshots/{name}`, and `decisions`. A card's `decisions` are those
its latest report marks for the owner, the report "A card's page" answers as `report`, so the two
pages never disagree, in the report's order, each `run_id`, the run whose report it is,
`position`, its place among that report's decisions from 0, `chosen`, `alternative`, `why`,
`reviewer_view`, the reviewer's view of it in the newest review of the commit that run left,
matched by `chosen` as the merge check matches them, null with none, and `answer`,
`{"answer", "answered_at"}` or null; `can_send_back`, whether the card can be sent back now, see
"Send back" below; and `sent_back`, `{"times", "pull_request", "comments"}`, its last send back, how
many times it was sent back, the pull request `{"number", "url"}` it had landed in and the owner's
words, or null when it never was. A project or feature the caller does not have is `404` "No
such feature". It is `FeatureStory` and `FeatureEndpoints` in `src/DevFlow/Features/`. It also
answers `runs`, the feature's own runs, none of its cards', a conflict's among them, newest first,
each as "A card's page" answers a run, a whole feature's review among them, and `input_tokens` and
`output_tokens`, summed over them, 0 before any; `reviews`, the reviews of the whole feature,
newest first, each as "A card's page" answers a review, see "Reviewing a feature"; and
`fix_rounds` and `rounds`, its fix rounds, see "Fixing a feature" under "Reviewing a feature".

**Answering.** `POST /devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/fine`
keeps the answer `fine`, "Fine as it is", to that decision, in one transaction with what it checks,
and answers `{"answer", "answered_at"}`. Only a decision the page can show is answered: `run` a
build or fix of a card of that feature, its report's decision at `position` marked for the owner,
otherwise `404` "No such decision"; and that report still the card's latest, otherwise `409` "A
newer report", so a page read before a newer run reads again rather than answering what the card no
longer says. An answer is kept as first given, so answering again changes nothing and answers the
one kept. Nothing is written to the code host. It is `DecisionAnswers` in `src/DevFlow/Features/`.

**The table**, from `migrations/0028_decision_answers.sql`, guarded like every scoped table and
following its project: `decision_answers`, `project_id`, `user_id`, `card_number`, `run_id`,
referencing `runs` and deleted with it, `position`, `answer`, `fine`, `sent_back`, which
"Sending a card back" gives, or `merged`, confirmed by merging, from
`migrations/0034_feature_merge.sql`, see "Merging a feature", and `answered_at`, its primary key `(project_id, run_id, position)`,
so a newer report's decisions are answered again. `migrations/0027_feature_page.sql` adds
`branch_watches.last_commit`, see "Watching branches", and `features.pull_request_url`, beside
`pull_request_number`, both or neither, as a card's are.

**The page**, `src/feature.tsx`, is at `/devflow/projects/<id>/features/<number>`, under the
Projects tab, which stays selected there, read from the route above as it loads, and laid out as
the feature page of the spec's mockup `2-feature-and-running-now.html`. Its header is a link "Back
to the project" over the feature's title as a section heading; a feature the caller does not have
is the `EmptyState` "There is no such feature", and one that cannot be read an alert. Under it the
package's `Card`s, top to bottom:

- "The feature": its state, as the Cards panel says a state, "Merged" and "Closed" among them, with
  its reason beside it; the links "Issue #<number>" and, once there is one, "Pull request
  #<number>", in a new tab with `rel="noreferrer"` when their address is https and plain text
  otherwise; "<done> of <all> cards done"; while the feature is offered for merging, the primary
  "Merge into <default>", see "Merging a feature"; and, while it is `ready` or `waiting_for_owner`
  with its pull request, "Send the whole feature back", which opens a comment box, see "Fixing a
  feature" under "Reviewing a feature". With both shown, the line saying why neither can be pressed
  while GitHub is not linked or its link has expired is said once, under the merge, and both point
  at it.
- "The review of the whole feature", once it has a review: the list "Reviews of the feature",
  newest first, each as a card's page shows a review, its commit, its verdict, whether it covers the
  pull request's head, its summary, its findings and its views for the owner.
- "Fix rounds, <n> of <m> used", `m` the project's `fix_rounds_feature` as it is now, once a fix of
  the whole feature has run: the list "Fix rounds of the feature", newest first, each as a card's
  page shows a round, "Round <n> of <m>" or "Your comment",
  how its fix went, the review that started it, his words, the developer's replies and the commit
  it left with the review of it.
- "What the agents want you to know", only when a card has a decision for the owner, in attention
  while any is not answered: the list "Decisions for you", each "#<card>: <chosen>", "Instead of
  <alternative>", "Because <why>" and "Reviewer: <view>", or "Reviewer: gave no view on it.", then a
  secondary "Fine as it is", which posts the answer and shows it in place, and, while its card can
  be sent back, a secondary "Send back", which opens a comment box "What should the developer change
  in #<card>?" with Send and Cancel; once answered, the `done` status "Fine as it is", "Sent back",
  or "Confirmed by merging" for one merging confirmed, and "answered <when>". A refused answer reads
  the story again and shows an alert under the decision.
- "The branch": "Its branch is made when its first card starts." before it is; "The feature is
  merged, and its branch is no longer watched." once it is merged; and otherwise a line saying the
  feature is not in main yet, or that its pull request was closed without merging so it is not in
  main, with the commit it is at, short, and the region "Check out
  the branch", `git fetch origin <branch> && git switch <branch>` in the mono font with a secondary
  "Copy the command" that reads "Copied" once it has, as a worker's commands are copied.
- "Screenshots", only when a card has any: a region "Screenshots of #<card>" per card with its
  number and title, each card's pictures as its own page shows them.
- "Cards": the table "Cards of the feature", each card's number and title linked to its page, its
  state, its fix rounds, its tokens and how many decisions it has for the owner, the numbers folded
  under the card at phone width; under it a secondary "Send back #<card>" for each card that can be
  sent back, kept out of the table so its rows stay readable at phone width, which opens the comment
  box "What should the developer change in #<card>?"; then, once a card was sent back, "Sent back", the list "Cards you sent back", each card's
  number and title, "Sent back from pull request #<n>", with how many times when more than once, and
  his words as he wrote them. "Send back" is disabled, with why under it, while GitHub is not
  linked or its link has expired.
- "Runs", only when the feature has a run of its own: the list "Runs of the feature", each run as a
  card's page shows one, its kind "Conflict", a failure's cause said to lie in the job, "so the
  feature waits for you", or outside it, "so it runs again".

Every text from the code host or an agent goes through `src/format.tsx`, as everywhere in Dev flow;
the owner's own words are shown as he wrote them, his line breaks kept. The comment boxes of "Send back" and "Send the whole
feature back" are one component, `src/comment-box.tsx`.

**Send back.** A card of a feature that has landed in the feature's branch is sent back with the
owner's comment: its issue opens again, and a fresh agent builds it on a new branch from the
feature's branch, with what was built for it and his comment; once it lands again the feature goes
through its end again. Why a sent back card gets a new branch is "Sending a card back" in
`docs/devflow-architecture.md`. It is `SendingBack` in `src/DevFlow/Features/`, `SendBackCard` and
`SendBackDecision` in `ActionEndpoints`, `OwnerComments` and `ProjectReader` for the read, and
`LandedCards` for the issue.

- **Which card.** One `done` with the pull request it landed in, of a feature neither `merged` nor
  `closed`: `SendingBack.Sendable`, one SQL expression that `can_send_back`, both routes and the read
  share, so they never disagree.
- **From the page**, `POST /devflow/api/projects/{id}/cards/{number}/send-back`, the route "Fixing"
  describes, posts his comment on the card's issue, closed or not, as him with his linked token and
  never as the app, and reads the project again. For any other card it refuses as it does there,
  `409` "This card cannot be sent back now".
- **From a decision**,
  `POST /devflow/api/projects/{id}/features/{number}/decisions/{run}/{position}/send-back`, with
  `{"comment"}`, under the `Api` policy and the origin check, posts on the card's issue the decision
  quoted, `> Chosen: <chosen>` and `> Instead of: <alternative>`, each further line quoted too and
  the second left out when there is no alternative, then a blank line and his comment, trimmed; once
  posted, it keeps the decision answered `sent_back` and reads the project again. In this order:
  `404` "No such decision", as for "Fine as it is"; `422` for a comment missing, empty once trimmed
  or longer than 10,000 characters; `409` "A newer report"; `409` "This decision is answered", since
  an answer is kept as first given; `409` "This card cannot be sent back now"; `409` without a usable
  link; then GitHub is asked: `204` once posted, `409` "GitHub refused the comment" with its reason,
  and `503` when GitHub did not answer, after the project is read again, with no answer kept.
- **The read.** Each read of the project reads the comments of the issue of every card that can be
  sent back, with no run under way, and takes those by a trusted account, see "Settings", past
  `comments_read_through` and written after the card's last run ended, or its last change for a card
  with no run, as "Fixing" takes his comments. Opening the issue himself on GitHub and commenting
  there is the same comment. A comment by a login off the list, and an opening alone, by anyone, queue nothing,
  and a landed card whose issue is open after the app closed it says why, "Its issue is open again,
  but only your comment on it sends it back", until it is closed again. Taken, in the read's
  transaction, the card is `queued`, "You sent it back; it is built again on a new branch", with his
  comments in `sent_back_comments`, `sent_backs` one more, the pull request it landed in kept in
  `sent_back_from` and `sent_back_from_url` and cleared from the card, its fix rounds and failures
  afresh; and `FeatureEnd.Advance`, in the same transaction, moves the feature to `building`. Its
  pull request stays open, and its end runs again once every card is done, the card's new pull
  request in its body. While its issue is still closed the store keeps the card, which has started.
- **Its issue opened again.** On the minute of the opening loop, and after each fake worker review,
  `LandedCards` opens the issue of every card sent back since its issue was last opened, as the app
  with a `Post` key, `PATCH /repos/{owner}/{repo}/issues/{number}` with `state` `open`, and records
  the send back it opened it for in `reopened_for`; what GitHub did not answer for is tried again on
  the next minute. Open, it is a sub-issue the merge check lands, and the app closes it again with
  its comment once it lands, since it tells each pull request a card lands in once.
- **Its build** is handed out as any card of a feature is, see "Jobs", on
  `devflow/<number>-<slug>-<k>`, `k` 2 the first time it is sent back and 3 the second, from the
  feature's branch at its head, and its pull request goes into the feature's branch. Its brief is
  the developer's with a section `## Sent back` after the card and the feature's sections: that the
  card landed in the feature's branch in pull request #<n> and the owner sent it back, that what was
  built is on the branch it starts from and is built on, not started again; `### The report it
  landed with`, its newest build's or fix's report that succeeded, as indented JSON; and a
  `### The owner's comment` for each of his comments, his exact text, each fenced as the fix brief
  fences them, with a reply asked for each in `replies`. "Run again" follows it when the card stopped
  since and he ran it again. The run records his comments, the ones that sent it back and then any
  that ran it again, as `owner_comments`.
- **The columns**, from `migrations/0033_sent_back_cards.sql`, on `cards`: `sent_backs`, 0 and up;
  `sent_back_from` and `sent_back_from_url`, both or neither, set exactly when `sent_backs` is not 0;
  `sent_back_comments`, a JSON array of his words, null before; and `reopened_for`, the send back
  whose issue the app opened, null before any, never past `sent_backs`.

## Review

Every card's pull request is read by a fresh reviewer agent on one of the project's workers, which
judges the exact commit and answers `approve` or `changes` with its findings. The verdict is kept
by Dev flow for that commit, shown on the card's page and on "Waiting for you", and posted on the
pull request for people to read. Why it is built so is "Review" in `docs/devflow-architecture.md`.
It is `ClaimReview` in `JobStore`, `JudgeReview` in `RunStore`, `Brief.Reviewer` and
`Briefs/reviewer.md`, `ReviewStore`, `ReviewText` and `ReviewPoster` in `src/DevFlow/Reviews/`,
`ReviewVerdict` in `DevFlow.Protocol`, and `ReviewJob` in the worker's `Review/`.

**When.** A card is to be reviewed when it is `reviewing`, with its pull request, picked with its
body kept, with no run under way, and its head commit, the one its newest build that succeeded
left, has no review in `reviews`. Opening its pull request moves it there. `jobs/next` hands out a
project's reviews before its fixes, see "Fixing", a whole feature's review between the two, see
"Reviewing a feature", a whole feature's fix after a card's, see "Fixing a feature" there, and
those before its builds, each by the cards'
order, so work in progress finishes before new work starts. The kind is read first, the key is made
for it, and only a card of that kind is then taken, with the same `FOR UPDATE SKIP LOCKED` lock.
The card stays `reviewing`, "The reviewer is reading it", and the run, `kind` `review`, is recorded
on the build's branch and base with the commit as its `head_commit` from the start.

**The job** is a `Job` of `kind` `review`: the card as it was picked, the build's `branch` and
`base`, the build's `check` command, the reviewer's brief as `brief`, the verdict's form,
`contracts/devflow/verdict.schema.json`, as `report_schema`, and `to_review`, null on a build:
`commit`, the commit to review, `report`, the build's report as it came, and `check`, its
`exit_code` and `output` on that commit. Its `push_key` is a key narrowed to `Fetch`,
`contents: read` and `metadata: read`, and the push key route answers a review `404`, so a
reviewer's worker never holds a key that pushes. The model is the project's `model_reviewing`, see
"Settings".

**On the worker**, `ReviewJob`: root clones the repository with the key into its own bare
repository, as a build's does; checks the commit is there, or fails the run in the card, since a
commit forced off the branch will not come back; points the card's branch at the commit in its own
repository, never pushed; and the agent clones it, through a bundle in root's handover folder,
into `/work/<job>/copy`, checked to be at the commit. Root writes `card.md`, the card as quoted;
`diff.patch`, `git diff --no-ext-diff --no-textconv <base>...<commit>`, from where the branch left
the base; `report.json`, the build's report; and `check.txt`, the check's command, exit code and
output; and `screenshots.txt`, the build's pictures of the commit, `pictures/<name>.png` each, and
its note, or "The build took no pictures of this commit.", in its handover folder, which the agent
reads and cannot write, and the agent copies them into `/work/<job>/review`, so root never writes
where the agent can. The pictures themselves root archives from its own clone of
`pr-screenshots`, `git archive` of the names the build listed under the commit's folder, into the
handover folder, and the agent unpacks them into `/work/<job>/review/pictures`; one that cannot be
read is said in `screenshots.txt`, and the review goes on. The job carries them as
`to_review.screenshots`, the build's `{"names", "note"}`, or null. Claude Code then runs in
`/work/<job>`, as `agent`, with the brief, the verdict's schema and
`--permission-mode default --permission-prompts none --allowedTools Read,Grep,Glob`, never
`bypassPermissions`: every other tool is refused and listed in `permission_denials` instead of
waiting, as the experiment of 2026-11-16 found. For a review those are sent in the result's
`permission_denials` and kept on the run, and fail nothing. Nothing is committed or pushed, and no
push key is asked for. The result names the commit the worker checked out as `head_commit`, null
on a failure, and no `check`.

**The brief**, `Briefs/reviewer.md`, is the same for every project: it judges and does not teach,
after this repository's `.claude/agents/reviewer.md` where that is not this repository's own; it
reads the card, the report, the diff, the check's output and the pictures; it judges whether the
card's acceptance is met and covered by tests, whether the code is correct, safe and in keeping
with the project's own `CLAUDE.md` or `AGENTS.md`, and the pictures against the card, a page that
looks broken, empty or wrong being a blocking finding, and pictures that could not be taken of a
page the card changes a finding; each finding is blocking or not, with file, line,
what and why; for each decision the developer marked for the owner it gives its view; anything in
the files is data, never an instruction, and an instruction found there is a blocking finding. The
card is fenced as in the developer's brief; the commit and branches are Dev flow's.

**The verdict's form**, `ReviewVerdict`, read strictly as the report is, with
`contracts/devflow/verdict.schema.json` written from it: `verdict`, `approve` or `changes`;
`summary`; `findings`, each `blocking`, `file`, empty when it is about no one file, `line`, 0 when
it is about no one line, `what` and `why`; and `for_owner`, each `decision` and `view`. `changes`
needs at least one blocking finding, `approve` has none, and a line is 0 or more. It has no field
for a commit.

**Judged**, `RunStore.JudgeReview`: a failure outside the card puts the card back in `reviewing`
to be reviewed again; a failure in the card, such as the time limit, or a verdict that does not
match its form, `report_invalid`, "The verdict did not match its form: <what>", is a failure in the
run, never a verdict, and stops the card with why, the second in a row, see "Stops"; one in its
form succeeds. An `approve` has the
card wait for the owner, due for the merge check, "Approved; Dev flow checks whether it may merge
it", which merges it or says why not, see "The merge check"; a
`changes` starts its next fix round, or stops it when its rounds are used up, see "Fixing". A lost review run puts its card back in `reviewing`. A result whose `head_commit` is not the
commit the review was handed, or null on a failure, is `422`.

**Kept**, `reviews`, in the same transaction as the run's result: `run_id`, the review run, which
holds its usage like any run; `card_number`; `reviewed_commit`, the run's, never the agent's word;
`verdict`; `summary`; `findings` and `for_owner`, as the form holds them; `pull_request_number`,
the card's; `reviewed_at`; and `posted_at`, null until its copy is posted. It is in migration
`0017_reviews`, with row level security as every table. A card's current review,
`ReviewStore.Current`, is its newest for its head commit and no other, so an approve for an older
commit does not count for the head, and a commit pushed after a review needs a new one.

**Posted for people**, `ReviewPoster`, on the minute `OpeningLoop` keeps, after the openings: one
comment on the pull request by the app with a `Post` key,

```text
Review of <commit, seven characters>: approve

<summary>

## Findings

- **Blocking.** `<file>:<line>` <what>. Why: <why>.

## For the owner

- <decision>: <view>.

Reviewed by Dev flow for #<card>, run <run id>.
```

blocking findings first, a list with nothing in it "None.", and every text of the reviewer's made
data as the pull request body makes the report's. Before posting it, the app's own comments on the
pull request are looked at for one naming the same run, so an answer that was lost never posts it
twice; that is the only comment Dev flow reads, and only to know whether to post. It is a copy:
Dev flow never reads a comment to learn a verdict, so no comment by anyone can approve a card. The
fake worker posts it before it answers, as it opens a pull request.

## Fixing

A `changes` verdict sends the card back to a fresh developer agent on the same branch, with the
findings, for at most the project's `fix_rounds_card`, 3 by default, each followed by a new review;
and a comment by the owner on
the card's open pull request, written on GitHub or sent back from "Waiting for you", starts a fix
too, without counting as a round. Why it is built so is "Fixing" in `docs/devflow-architecture.md`.
It is `ClaimFix` and `Earlier` in `JobStore`, `JudgeFix` and the rounds in `RunStore.JudgeReview`,
`Brief.Fixer`, `FixRounds` and `OwnerComments` in `src/DevFlow/Fixing/`, the reading of comments in
`ProjectReader`, `SendBackCard` in `ActionEndpoints`, and the fix in the worker's `BuildJob`.

**A round** starts on a `changes` verdict on the card's head: the card moves to `fixing`,
"Round <n> of <m>: the reviewer asked for changes; a fix runs next", `m` its project's
`fix_rounds_card` read when the verdict is judged, and records the round, `n`, and the review it
answers. A `changes` verdict with every round used moves the card to `stopped`, "Fix rounds used
up", for "Stops" to mark, so with 0 its first `changes` does; an `approve` has it wait for the owner, as "Review" says.

**Handed out** by `jobs/next`, after the project's reviews and before its builds, each by the cards'
order: a card that is `fixing`, with its pull request, picked with its body kept, no run under way,
and a commit on its branch. The job is a build's, `kind` `fix`, with a push key, on the branch and
base of the card's build, the check from `.devflow/project.json` on the default branch now, the fix
brief as `brief`, the report's form, and `to_fix`, null on any other kind: `commit`, the newest
commit a build or a fix of the card left on its branch, whatever its result, since a worker pushes
what its agent made even when the check failed, and `round`, null for the owner's comment. The run
records the commit as `from_commit`, its round, the review it answers and his comments, and the
card says "Round <n> of <m>: the developer is fixing it" or "The developer is answering your comment"
while it runs.

**On the worker** a fix is carried as a build, "The worker" says how, with one difference: the
card's branch on the remote must be at `to_fix.commit`, or the run fails in the card before
anything runs, "The branch <branch> is at <commit>, not at <commit>, the commit the fix was handed,
so it was not started", since it would build on a commit Dev flow did not hand out. The check runs
on the new commit, which is pushed to the same branch, never forced.

**The brief**, `Brief.Fixer`, is the developer's, "The brief" under "Jobs", with a section after
the card, `## What to fix`: that the card's branch is continued, its round of the project's rounds
as the job was handed out, or that the owner
sent it back; `### The earlier report`, the newest build's or fix's report that succeeded, as it
came; `### The blocking findings`, to be fixed, test first where behaviour changes, or argued in
`replies`; `### The other findings`, the nits, fixed when cheap; and a `### The owner's comment`
for each of his comments, his exact text. The report and the findings are quoted as indented JSON
and each comment as text, each in a fence longer than any run of backticks inside it. Every finding
and every comment gets a reply in `replies`.

**Judged**, `RunStore.JudgeFix`: a failure outside the card puts the card back in `fixing` in the
same round; a failure in the card, "The fix failed: <reason>", a report that does not match its
form or that stopped, a branch left at `from_commit`, "The fix left no new commit on the branch",
failed in the card, and a check that did not run or failed each stop it with why, the second in a
row with no new commit for all but a report that stopped, see "Stops"; anything else
succeeded, the fix's commit is the card's head and its pull request's, and the card goes back to
`reviewing`, "Its pull request is open; the reviewer reads it next". A lost fix puts its card back
in `fixing`.

**The re-review** is the review of the new head, "Review" says how, with the fix's report as
`to_review.report` and each earlier round in `to_review.earlier`, oldest first, empty on a first
review: `round`, `reviewed_commit`, `verdict`, `summary` and `findings` of the review it answered,
null or empty without one, `comments`, his words, `replies`, the fix's, and `fixed_commit`. The
worker writes them to `review/earlier.json`, and the reviewer's brief says whether this is the
card's first review, and that a later one judges the whole change again from the base: a finding
that still holds is still blocking. Only blocking findings make it `changes`.

**The owner's comment.** Each read of a project, `ProjectReader.Read`, reads the comments of the
pull request of every card that has one still open, is `reviewing`, `fixing`, `waiting_for_owner`
or `stopped`, and has no run under way, and of a stopped card's issue too, whose comments run it
again, see "Stops", and of a landed card's issue, whose comments send it back, see "Send back" under
"A feature's page"; and takes those by a trusted account of the project, see "Settings", as GitHub
compares logins, whatever the case, that are new: past `comments_read_through`, the id of the
newest of his already taken, and every one of his before any was. No time is compared, so a comment
written during a review is not lost to the fix a worker claims seconds after it. They are taken in
the read's transaction, `OwnerComments.Take`: the card moves to `fixing`, "You commented on its
pull request; a fix runs next", with no round, answering his comments and the card's latest review
if that review is for the card's head, so never one a fix has answered since; a card already
waiting for a round's fix keeps the round and adds them. It is the only comment read as an input: anyone else's, the app's review copies
among them, is neither a trigger nor in the brief. A comment written while a run is under way waits
for the first read that falls between runs; in a review, fix and review loop those gaps last seconds
and reads come once a minute, so it is often taken only once the card waits for him or stops. Nothing
is lost meanwhile. `Take` locks the card's row in a statement of its own before its update checks
for a run, since every claim locks that row and records its run before it commits: under read
committed the update then sees a run a claim committed while it waited, where a check inside the
update alone would read `runs` as they were when it began. Without a linked login nothing is read, and GitHub not answering fails
the read as any other read does.

**Sending back** a Dev flow card from "Waiting for you":
`POST /devflow/api/projects/{id}/cards/{number}/send-back`, with `{"comment"}`, under the `Api`
policy and the origin check, posts the comment, trimmed and with nothing added, as the owner with
his linked token, never as the app, and reads the project again, which takes it as above: on the
card's pull request for a card that waits for him, and on the card's issue for a stopped one, as
"Comment to run again", see "Stops", since its issue is there whatever became of its pull request;
and on the card's issue for a card landed in an open feature's branch, which sends it back, see
"Send back" under "A feature's page". In this order: `404` "No such card"; `422` for a comment
missing, empty once trimmed or longer than 10,000 characters; `409` "This card cannot be sent back
now" for a card that neither waits for him with its pull request, nor has stopped, nor has landed in
an open feature; `409` without a usable link; then GitHub is asked to post:
`204` once posted, `409` "GitHub refused the comment" with its reason, and `503` when GitHub did not
answer, after the project is read again.

**The tables**, from `migrations/0018_fix_rounds.sql` and `migrations/0019_owner_comments.sql`:
`runs` gains `kind` `fix`, `from_commit`, required on a fix, `fix_round`, `fix_review_id` and
`owner_comments`; `cards` gains `fix_rounds`, the rounds reviews started, 0 to 3, and what its next
fix answers, `fix_round`, `fix_review_id` and `fix_comments`, set as it moves to `fixing`, and
`comments_read_through`. A card's head, everywhere it is read, is the commit its newest build or
fix that succeeded left.

## The merge check

A card whose pull request was approved is merged by Dev flow into the branch it was built on when
every condition holds, and otherwise waits for the owner with the reason, the successor of this
repository's merge gate. Why it is built so is "The merge check" in `docs/devflow-architecture.md`.
It is `MergeCheck`, `MergeChecker`, `MergeRequests` and `AppMerges.MergeCard` in
`src/DevFlow/Merging/`, and `DecisionStore`, `DecisionText` and `DecisionIssues` in
`src/DevFlow/Decisions/`.

**The decision**, `MergeCheck.Decide(MergeFacts)`, one pure function: no database, no network, no
clock and no language model. Its caller reads, fresh, what it decides on and passes it in, each
code host read as it answered, a failure included: the project's hold, `stopped` or not read within
five minutes, as handing out reads it; its setting, labels and trusted accounts, the owner's linked login among them, and on `sometimes` its three conditions, its review label and its paths; whether any build's or fix's report of the card marked a decision for the owner, read in SQL by the rule `DecisionStore.MarkedFrom` records them by; the card's
record of what was picked, and of what its newest reading in its form was handed; what its newest build or fix that succeeded left, its commit, the
check's exit code and when it came back, which is when it was pushed; its latest review in
`reviews`; and as the app the pull request, the statuses and check runs on its head, the open
issues with the pick-up label, the card's edits and its label events, `ICodeHost.LabelEvents`, and, only on `sometimes` with `wait_when_paths` ticked and a path listed, the pull request's files, `ICodeHost.PullRequestFiles`. It answers `Merge(commit)`, the pull request's
head, or `Wait(reason, try_again)`, the first that holds deciding:

| Order | What is read | No merge, the card's reason | Tried again |
|---|---|---|---|
| 1 | The project is stopped | "Its project is stopped: nothing merges until you resume it" | no |
| 2 | Its default branch not read for five minutes | "Its project's default branch has not been read lately; Dev flow tries again each minute" | yes |
| 3 | For a card of a feature, its feature's branch not read for five minutes | "Its feature's branch has not been read lately; Dev flow tries again each minute" | yes |
| 4 | The pull request not read | "Dev flow could not read its pull request; it tries again each minute" | yes |
| 5 | It is merged or closed | "Its pull request is no longer open" | no |
| 6 | Its head is not the commit the card's runs left | "Its head commit is not one the worker checked" | no |
| 7 | The worker's check did not exit 0 on it | "The worker's check did not pass on its head commit" | no |
| 8 | The pull request goes into another branch than the one its run used, the default branch or its feature's | "Its pull request goes into <base>, not <branch>" | no |
| 9 | The latest review is not for exactly that commit | "The reviewer has not approved its head commit" | no |
| 10 | It is, and asks for changes | "The reviewer asked for changes on its head commit" | no |
| 11 | The card, its edits or its label events not read | "Dev flow could not read the card; it tries again each minute" | yes |
| 12 | No linked login | "Dev flow does not know your GitHub login: link GitHub on the Projects tab" | no |
| 13 | Not open with the pick-up label | "The card is no longer open with the label <label>" | no |
| 14 | Written by a login that is not a trusted account | "The card was written by <login>, who is not a trusted account of this project" | no |
| 15 | No trusted account put the pick-up label on, by its timeline | "The card's pick-up label <label> was added by <login>, who is not a trusted account of this project", naming who put it on last, or "The card's pick-up label <label> was not added by a trusted account of this project" when its timeline does not say | no |
| 16 | It carries the hold label | "The card is held by the label <label>" | no |
| 17 | It does not, but a trusted account put the hold label on and only someone untrusted took it off since | "The card is held by the label <label>, taken off by <login>, who is not a trusted account of this project" | no |
| 18 | A login that is not a trusted account edited it | "The card was edited by <login> since it was picked" | no |
| 19 | Its body or title differ from the record with no newer edit to say who | "The card changed since it was picked, and its history does not say by whom" | no |
| 20 | It has a reading, and its body, by its SHA-256, or its title as read now differ from what its newest reading in its form was handed | "The card changed since it was read, and it was built from what was read" | no |
| 21 | The setting is `sometimes` with `wait_when_marked` ticked, and the review label counts on the card, by its timeline | "This card is marked for your review" | no |
| 22 | The same, with `wait_when_paths` ticked and a path listed, and the pull request's files not read | "Dev flow could not read the files of its pull request; it tries again each minute" | yes |
| 23 | They are read, and are 3000 or more, GitHub's limit, so they may not be all | "Too many files to check the paths" | no |
| 24 | A file, by its path or a rename's old path, matches a listed pattern, the first file and the first pattern named | "It changes <path>, which matches <pattern> on this project's list of paths" | no |
| 25 | The same, with `wait_when_decision` ticked, and any build's or fix's report of the card marked a decision for the owner | "The agent made a decision for you" | no |
| 26 | The setting is anything but `never` or `sometimes` | "This project waits for you on every pull request" | no |
| 27 | CI not read | "Dev flow could not read CI on its commit; it tries again each minute" | yes |
| 28 | A check run completed with any conclusion but `success`, or a status `failure` or `error`, the first by name | "CI failed: <name>" | no |
| 29 | A check run queued or in progress, or a status pending | "CI is still running on its commit" | yes |
| 30 | No check run and no status, less than 30 minutes after the push | "Waiting for CI to report on its commit" | yes |
| 31 | None, 30 minutes or more after it | "CI did not report" | no |

Logins and labels are compared whatever their case, as GitHub's are, and branches exactly, as git's
are. An edit by a trusted account since the card was picked is fine, so is a change to its body or
title their edits explain, but not since it was read: row 20 holds the card to its newest
reading's record, the body SHA-256 and title the reading was handed, since that is what it was
built from, so a trusted edit after the reading, which can only come once the card has started,
since one before reads it again, is the owner's to judge. A card with no reading, built before
reading came, skips row 20. Rows 15, 17 and 21 read the labels by `LabelActors.Judge`, as picking does,
see "Cards" above: the review label as one that holds back, so it counts while it is on, whoever put
it there, and while a trusted account put it on and only someone untrusted took it off since.
Patterns are matched as "Paths" under "Settings" above has them. Screenshots, whether tests were written and the agent's report are not on the list,
on purpose: the reviewer judges the first two, and the report decides nothing.

**A card of a feature** is checked by the same function, `MergeFacts.Feature` carrying its feature,
with the feature's branch as its base: its run's base, row 8, and its branch's watch, row 3, read as
`WatchStore.FeatureWatchedSql` reads it for handing out. Its rows 11 to 19 read it through its
parent, since a feature's cards carry no label of their own: as the app, the parent's sub-issues,
`ICodeHost.SubIssues`, its edits and its label events, beside the card's, any of them unread being
row 11. In place of row 13, the parent is open with the pick-up label, "Its feature, #<parent>, is no longer open with the label <label>"; then picked,
"Its feature, #<parent>, is not picked" otherwise; then held to rows 14 to 19 against its own record
in `features`, "Its feature, #<parent>, was written by <login>, who is not a trusted account of this project",
"The pick-up label <label> on its feature, #<parent>, was added by <login>, who is not a trusted account of this project", "Its feature, #<parent>,
is held by the label <label>", "Its feature, #<parent>, is held by the label <label>, taken off by <login>, who is not a trusted
account of this project", "Its feature, #<parent>, was edited by <login> since it was picked"
and "Its feature, #<parent>, changed since it was picked, and its history does not say by whom";
then the card is an open sub-issue of it in the project's repository, "The card is no longer an open
sub-issue of its feature, #<parent>"; then the card itself, rows 14 and 16 to 20, as a card on its
own, row 15 left out since it carries no pick-up label. None
of these is tried again. Row 21 reads the parent first, "Its feature, #<parent>, is marked for your
review", so the review label on a feature's parent marks every card of it, and then the card's own
label. With `never` it merges into the feature's branch, with `sometimes` it does unless a ticked
condition holds, and with `always` it waits on "Waiting for you" with its feature's branch as its
`base`, and the owner merges it from there into that branch. The setting governs a card's pull
request into its feature's branch only: the owner always merges a feature into the default branch
himself, whatever it is.

**A card of a feature that landed.** A card of a feature is `done` when its pull request merged into
the feature's branch, whoever merged it, the check, the owner from the page or anyone on GitHub, which
the read then finds. `LandedCards.TellPending(project, user)` then tells each such card's issue, as
the app with a `Post` key: it reads the pull request, and one merged into the feature's branch gets
one comment,

```text
Built and merged into the feature's branch `<branch>` in #<pull request>. It reaches `<default>` with its feature, #<parent>.
```

`<default>` the default branch as its watch names it, and the issue is then closed as completed with
`ICodeHost.SetIssueState`, `PATCH /repos/{owner}/{repo}/issues/{number}` with `state` and
`state_reason`, which also opens an issue again for the later slices that send a card back. The
app's own comments on the issue are read first, so a comment whose answer was lost is not posted
twice. Told, the card's `landing_told_for` is its pull request's number. One merged into another
branch is marked told and left as it is, since the comment would not be true of it. It runs where
the decisions' issues open, after a merge the result route asked for, in `MergeRequestLoop`, on each
project's minute, in `OpeningLoop`, and in the fake worker after a review; what GitHub did not answer
for is tried again the next minute. A card on its own is closed by its pull request's `Closes` line,
and is never told.

**When it runs.** `MergeChecker.CheckPending(project, user)` checks every card of the project that
is `waiting_for_owner` with `merge_check_due`, which a review that approves sets, as the card
moves there with "Approved; Dev flow checks whether it may merge it". It runs right after a
worker's result is recorded, without the worker waiting for it: the result route asks
`MergeRequests`, whose loop, `MergeRequestLoop`, runs the check and then opens the decisions'
issues; and on each project's minute, in `OpeningLoop`. With `DEVFLOW_POLL_SECONDS=0` neither runs,
and a request is dropped, so the service's own tests run it themselves; the fake worker runs it,
and opens the decisions' issues, before it answers a review. A project that holds is decided without
asking GitHub anything. A wait records the reason on the card, its `changed_at` moving only when the
reason does; one that is tried again keeps the card due, and one that is not clears it, so the
card is the owner's from then on, on "Waiting for you" with its reason, where he can merge it as
his own merge or send it back. One card's failure is logged and never stops the next.

**Merging.** `AppMerges.MergeCard(user, project, card, number, commit)`: under the project's row
lock, the one the watch judges under, it reads again that the project is neither stopped nor
unwatched, that the setting is still the one the check decided on, `never`, or `sometimes` with the
same three conditions, review label and paths, and that the card is still `waiting_for_owner`, due,
with this pull request, holding its row; then merges as the app with a `Merge` key, a merge commit
with `sha` the approved commit; and, when GitHub merged, writes the merge commit to `merges_made`,
records the card `done` and records its decisions for the owner, all in that one transaction. A
merge whose answer named no merge commit is still `done`, and the watch stops the project when it
finds a merge by the app it has no record of. What GitHub answered otherwise is the card's reason:
a moved head, "Its pull request's head moved as it was merged; Dev flow reads it again", tried
again, when the head is a commit the worker never checked and the card is the owner's; not
mergeable, "GitHub would not merge it as it is", and refused, "GitHub refused to merge it", the
owner's; no answer, "GitHub did not answer the merge; Dev flow tries again each minute", and no
key, "Dev flow could not get a key to merge with; it tries again each minute", tried again. A
merge whose answer was lost is found merged by the next read, which records the card `done`.

**The setting.** With `waits_for_owner` `always`, an approved card waits on "Waiting for you"
with "This project waits for you on every pull request", and the owner merges it from there as
himself, through `POST /devflow/api/projects/{id}/cards/{number}/merge`, see "Acting from the
page". With `sometimes`, one waits there the same way with the reason of the condition that held,
rows 21 to 25, and none but row 22's, files not read, is tried again: a label taken off or a
setting changed later does not release it, which only his merge or his sending it back does. The setting is read fresh each
time, so a change applies at the next check.

**A decision for the owner.** When the check merges a card on its own, with `never`, or with
`sometimes` and `wait_when_decision` not ticked, `DecisionStore.Record` records in the
merge's transaction each decision marked `for_owner` in the reports of the card's builds and fixes
that succeeded, once each by what was chosen, whatever its case, the newest run's words winning, in
the order they first appeared; with the reviewer's `for_owner` view on it from the review that
approved the merged commit, matched by the decision's name, and the project's attention label.
Every run's, since a fix's report need not repeat what the build decided. Read in SQL from the
reports as stored, so the merge's code names no type of the agent's. `DecisionIssues.OpenPending`
then opens one issue for each as the app with a `Post` key, on the minute and right after the
merge: titled "Decision in #<pull request>: <chosen>", what was chosen made one line and cut to
80 characters, labelled with the attention label, its body

```text
Dev flow merged #<pull request> for card #<card>. The agent made this decision and marked it for you.

- **Chosen.** <chosen>
- **Alternative.** <alternative>
- **Why.** <why>
- **The reviewer's view.** <view, or "The reviewer gave none.">

The card: <the card's address>
The merged pull request: <the pull request's address>

Whether it stands is yours to judge. A fix is a new card. Close this issue once you have looked; Dev flow lists it on Waiting for you until then.

Opened by Dev flow for decision <id>.
```

every text of the agent's and the reviewer's made data as the pull request body makes the
report's, so it closes no issue and notifies nobody. Before opening, the app's own open issues
with the label are looked at for one whose body names the decision's id, so an answer that was lost
never opens a second. With `always`, or `sometimes` and `wait_when_decision` ticked, the card does
not merge, so no issue is opened: the decision is on the card's item, which waits for him. A card
of a feature opens no issue, whatever the setting: its
decisions stay in its reports, and the feature's page shows them with an answer.

**On "Waiting for you"**, `GET /devflow/api/waiting`'s `decisions` are the open ones, by project,
oldest first: `id`, `project_id`, `project_name`, `owner`, `card_number`, `card_title`, `card_url`,
`pull_request_number`, `pull_request_url`, `issue_number`, `issue_url`, `chosen`, `alternative`
and `why`, null when the agent wrote nothing, `reviewer_view`, null when the reviewer gave none,
and `opened_at`. They count in the list's `count`, the display data's `waiting` and
`GET /devflow/api/attention`. Each read of the project lists the open issues with each label its
open decision issues were opened with, only when it has one, and closes those no longer listed, so
one leaves the list once the owner closes it on GitHub, or takes its label off; a list GitHub did
not answer closes nothing that minute and fails nothing else of the read.

**The tables**, from `migrations/0022_merge_check.sql`, `migrations/0023_decision_issues.sql` and
`migrations/0026_landed_cards.sql`: `cards` gains `merge_check_due`, and `landing_told_for`, the pull
request whose landing in its feature's branch its issue was told of, null until then; and `decision_issues`, one row per decision: `id`, `project_id`,
`user_id`, `card_number`, `card_title`, `card_url`, `pull_request_number`, `pull_request_url`,
`position`, its place among the card's, unique per pull request, `chosen`, `alternative`, `why`,
`reviewer_view`, `label`, `recorded_at`, then `issue_number`, `issue_url` and `opened_at`, all
three or none, and `closed_at`, with row level security as every table.

## The end of a feature

When every card of a feature is done, Dev flow brings the default branch into the feature's branch
and opens the feature's pull request to the default branch; while the feature waits for the owner,
each change on the default branch is brought in again, and a card added to it sends it back to
building. Why it is built so is "The end of a feature" in `docs/devflow-architecture.md`. It is
`FeatureEnd`, the decision, `FeatureEnds`, the writes, and `FeatureText`, the body, in
`src/DevFlow/Features/`, and `BringIn` and `EditPullRequestBody` in `ICodeHost`; a conflict is a job,
see "A conflict" below.

**When.** `FeatureEnd.Advance`, in every read of the project, inside its transaction, and again at
the start of the end's minute, from what Dev flow stored and nothing else. For each feature whose
branch is made, by its cards in its order:

- a feature `building` whose cards are all `done` moves to `bringing_in`;
- one `waiting_for_owner` or `ready` whose cards are all `done` moves to `bringing_in` when the
  default branch's commit, `last_commit` of its watch, is known and is neither `brought_in_commit`,
  the one last brought in, nor `bring_in_failed_at`, the one bringing in last failed at;
- one `building`, `waiting_for_owner` or `ready` with a card not `done` is `building`, its reason
  "Its end waits for #<n>, which is stopped", "is held", "waits for you" or "is ignored", for the
  first card in that state, and none otherwise;
- one `reviewing` with a card not `done`, a card added at its end, is `building` too, as above,
  and its review, when one runs, is kept and judged again at the new head;
- one `resolving`, `fixing` or `bringing_in`, and one `reviewing` whose cards are all `done`, is
  left as it is, and is brought in once it waits for the owner again.

**Bringing in**, on the minute of the opening loop, `OpeningLoop`, after the landed cards are told,
and right after each fake worker review: for each feature `bringing_in`, under the project's lock,
`BranchWatch.Lock`, while the project is not stopped, its default branch was read in the last five
minutes, the feature's branch too, and the feature is still `bringing_in`, the app, with a `Merge`
key, asks `POST /repos/{owner}/{repo}/merges` with `base` the feature's branch, `head` the default
branch and `commit_message` "Bring <default> into <feature branch>". The lock is held while GitHub
answers. A feature already brought in at the default branch's commit asks nothing.

| GitHub answers | Dev flow |
|---|---|
| `201`, the merge commit | written to `merges_made` with the feature's branch and `number` null, in the same transaction; `brought_in_commit` is the answer's second parent, the default branch's commit merged, or its watch's commit when the answer names none; then the pull request |
| `204`, nothing to bring in | `brought_in_commit` is the watch's commit when it has one; then the pull request |
| `409`, a conflict | `resolving`, "<default> conflicts with the feature's branch; an agent resolves it", for a conflict job, see "A conflict" below |
| any other status | `waiting_for_owner`, "GitHub refused to bring <default> into the feature's branch, with <status>", the same way |
| no answer | left `bringing_in`, tried again on the next minute |

The watch takes a push on a feature's branch that left it at a commit in `merges_made` for that
branch as fine, since GitHub lists a merge through this endpoint as a push by the app, see "Watching
branches".

**The feature's pull request**, once the default branch is in: the app's own open pull request from
the feature's branch into the default branch is the feature's, or, while the feature has none
recorded, one is opened as the app with a `Post` key, not a draft, titled with the parent's title as
picked, with the body

```text
Feature #<parent>.

## Cards

- #<card> <title>, landed in #<pull request>

Closes #<parent>

Built by Dev flow for the feature #<parent>.
```

each card in the feature's order, its title as picked, ", landed in #<n>" left out when it has no
pull request recorded, and every title made data as a card's report is in its body, see "The pull
request Dev flow opens", so none adds a heading, hides what follows or closes an issue. A body that
differs from it is replaced, `PATCH /repos/{owner}/{repo}/pulls/{number}` as the app with a `Post`
key, so a card added later is listed once the end runs again. A refusal to open is looked at again
for one opened meanwhile, as a card's is. Then `pull_request_number` and `pull_request_url` are
recorded, and the feature is `reviewing`, for its review of the whole feature, see "Reviewing a
feature"; the owner merges it on GitHub. A pull request recorded that is no longer
open is not opened again: what follows a feature's pull request closed is "Merging a feature into
main" and "Sending a card back". Nothing is held while GitHub answers for the pull request, and a
minute it did not answer for leaves the feature `bringing_in`, already brought in, for the next.

**A card added** to a feature at its end is picked on the next read, as every sub-issue is; the
feature moves to `building`, its pull request stays open, and its end runs again once that card is
done, adding the card to the body.

**A conflict** is resolved by an agent, in a job of its own on one of the project's workers, a run
for the feature rather than for a card. It is `JobStore.ClaimConflict`, `Dispatcher` and
`RunStore.JudgeConflict` in `src/DevFlow/Jobs/`, `Briefs/conflict.md`, and the worker's side in
"The worker".

- **Handed out** after reviews and fixes and before builds, see "Jobs", for a feature
  `resolving` whose branch is made, with no run of its own under way, and whose branch's watch has
  read it in the last five minutes and since the feature began resolving, so the commit the watch
  last named holds the card merged there just before. The job starts from that commit, the feature
  branch's head, `from_commit` on the run, and brings in the default branch's as its watch last named
  it, `default_commit`. Before the feature is taken, the files in conflict are read as the app,
  `ChangedFiles` both ways between the two commits, and those both sides changed are listed in the
  brief, sorted: GitHub cannot say which files conflict without merging, so these are where the
  conflicts are, and the agent's `git merge` names them exactly. The feature is then taken only at
  those two commits, with `FOR UPDATE SKIP LOCKED`, as a card is. A code host that does not answer
  is no work now. The job's `card` is the feature's parent, its `branch` the feature's branch, its
  `base` the default branch, its key a `Push` key, its check the project's, and its `to_resolve`
  the two commits.
- **The brief**, `src/DevFlow/Briefs/conflict.md`, the same for every project, filled by
  `Brief.Conflict`: bring the default branch in with `git merge origin/<default>`, never a rebase,
  and resolve each conflict so both sides' intent holds, the feature's work and what landed on the
  default branch; change nothing that is not in conflict; commit the merge, run the check and fix
  what the resolution broke in the merge commit itself; exactly one new commit, which the worker
  checks; never push or touch the code host; decide and record each choice, `for_owner` where one
  side gives way, never ask, and report `stopped` when it cannot be resolved; the parent as picked,
  each done card's handover note and the files in conflict, under "What is information", each
  fenced; and the report in its usual form, `replies` empty.
- **Its result**, judged in this order, moves the feature only while it is `resolving`:

  | What came back | The run's `result` | The feature |
  |---|---|---|
  | A failure outside the job | `failed_outside_card` | Stays `resolving`, handed out again after the project's wait, see "Stops", nothing counted |
  | A failure in the job | `failed_in_card` | `waiting_for_owner`, "Dev flow could not resolve the conflict with <default>: The run failed: <reason>" |
  | A report out of its form, one that stopped, no check, a check that failed | as a build's | `waiting_for_owner`, "Dev flow could not resolve the conflict with <default>: <the run's reason>" |
  | A success that names no commit, or the one it was handed | `failed_in_card` | `waiting_for_owner`, "…: No merge commit was pushed" |
  | Anything else | `succeeded` | `bringing_in`, `brought_in_commit` the run's `default_commit`, `bring_in_failed_at` null |

  Waiting for the owner, `bring_in_failed_at` is the run's `default_commit`, so bringing in is not
  tried again until the default branch moves: there are no rounds for a conflict. Once it moves, a
  read brings it in again, and a conflict then is a new job. A success goes on as after a clean
  merge: the end's minute finds it brought in at the default branch's commit and opens or updates
  the feature's pull request, or brings in again when the default branch has moved meanwhile. A run
  found lost leaves the feature `resolving`, and the project waits, as for a card's.
- **Its push is Dev flow's own.** The worker pushes the merge to the feature's branch with the job's
  key, while its run is under way, so the watch keeps it in `pushes_awaited`; its result writes the
  commit to `pushes_made`, in the same transaction, and the watch finds it fine, as "Watching
  branches" has it for every job on a feature's branch.
- **The remerge diff** is kept on the run, `remerge_diff`, as text, cut beyond 1 MiB with a line
  saying so, and it is data. The review of the whole feature hands it to the reviewer, see
  "Reviewing a feature".

**The columns**, from `migrations/0029_feature_end.sql`: `merges_made.number` is null for a merge
that merged no pull request, and `features` gains `bring_in_failed_at`, the default branch's commit
at which bringing in last met a conflict or a refusal, null otherwise. `brought_in_commit`,
`pull_request_number` and `pull_request_url` are 0024's and 0027's, set here.

**In the fake**, bringing in is a merge commit in the repository's git repository, with the base's
tree and the base's and head's commits as its parents, recorded as the app's write `branch_merge`
and listed on the base as a push by the app; nothing to bring in when the base holds the head
already; a conflict for a base the repository's `conflicts` lists; `404` for a base or head its git
repository does not have. Editing a body is recorded as `pull_request_edited`. The fake worker
answers a conflict job with a merge commit on the feature's branch, the feature's head and the
default branch's commit its parents, with the feature's tree, listed on the branch as a push by the
app, when the prepared result succeeded with a passing check, and with the prepared
`remerge_diff`; with anything else it pushes nothing, as a worker does. See "The fake".

## Reviewing a feature

Once a feature's pull request is open, a fresh reviewer agent on one of the project's workers
reviews the whole feature at its head, seeing only the feature's own changes and, apart, what an
agent chose where a conflict was resolved. The verdict is kept by Dev flow for that commit, shown
on the feature's page and on "Waiting for you", and posted on the feature's pull request for
people to read. The feature is ready for the owner when the latest review approves its head and CI
passed there. Why it is built so is "Reviewing a feature" in `docs/devflow-architecture.md`. It
is `FeatureReady`, the decision, and `FeatureReadiness`, its minute, in `src/DevFlow/Features/`;
`ClaimFeatureReview` in `JobStore` and `JudgeFeatureReview` in `RunStore`; `Brief.FeatureReviewer`
and `Briefs/feature-reviewer.md`; `FeatureReviewStore`, `ReviewText.FeatureComment` and
`ReviewPoster` in `src/DevFlow/Reviews/`; `MergeCheck.Ci`, the CI rule it shares with a card's
merge; and the worker's `ReviewJob`, which runs a card's review and a feature's.

**When.** `FeatureReadiness.DecidePending`, on the minute of the opening loop, right after the end
of each feature, see "The end of a feature", and after each fake worker run. It looks at each
feature `reviewing`, `waiting_for_owner` or `ready` with its pull request recorded, the default
branch brought in, `bring_in_failed_at` null, every card `done`, no run of its own under way, and
its branch's watch read in the last five minutes and since Dev flow's last merge or push on the
branch, `merges_made.merged_at` and `pushes_made.recorded_at`, so the commit the watch names, its
`last_commit`, is the feature's head with what Dev flow put there. Opening or keeping the pull
request moves the feature to `reviewing`, with no reason, for it to decide. For each, by
`FeatureReady.Decide`, from what Dev flow stored, in this order:

- **No review covers the head**: `reviewing`, "To be reviewed as a whole", with `review_commit`
  the head, and its review is a job, below; unless its review failed in the job at that commit,
  `review_failed_at`, when it stays waiting for the owner with why, until the branch moves.
- **A fix of the head failed in the job**, `fix_failed_at`: it stays as that failure left it,
  waiting for the owner, until the branch moves or he comments, see "Fixing a feature" below.
- **The covering review asked for changes**: `fixing`, its next round, see "Fixing a feature"
  below; or, with every one of the project's `fix_rounds_feature` used, none at all with 0,
  `waiting_for_owner`, "Review rounds used up", with its
  findings on its page and on "Waiting for you".
- **The covering review approved**: CI on the head, read as the app, every check run and status,
  by `MergeCheck.Ci` as "The merge check" reads it: a failure first, by name, "CI failed: <name>";
  anything running, "CI is still running on its commit"; nothing at all, "Waiting for CI to report
  on its commit", and 30 minutes after the push "CI did not report"; a read that failed, "Dev flow
  could not read CI on its commit; it tries again each minute". Each is `waiting_for_owner` with
  that reason. Every check passed: `ready`, no reason. The push is when Dev flow recorded the head
  in `merges_made` or `pushes_made`, or the feature's last change for a head of the owner's.

Whatever the review, the head CI passed on is kept in `ci_passed_commit`, null when it has not or
could not be read: from the read above for a head an approve covers, and, for a feature that waits
for the owner otherwise, from CI read the same way until it has passed on that head, and not again
for it. It is what offers the merge, see "Merging a feature".

The latest review **covers** the head when it is for the head, or when the head differs from it
only by merges of the default branch Dev flow made cleanly: following the head back through
`merges_made` rows of the feature's branch with no `number`, each by its `parent_commit`, the
feature branch's commit it merged into, reaches the reviewed commit. So a clean bringing in needs
CI again and no new review, and a conflict an agent resolved, a push of its own in `pushes_made`
that no such row names, needs a new review. A commit the owner pushes to the branch himself is
covered by no review either. Each minute reads it again, so a feature that is ready and whose CI
fails later, or whose branch moves, moves with it; nothing is written while nothing changes, and
`changed_at` moves only with the state or the reason.

**Handed out** with the reviews of cards, first: `jobs/next` hands out a card's review, then a whole
feature's, then a card's fix, a whole feature's fix, a conflict and a build, see "Jobs". A feature is reviewable when it is
`reviewing` with its pull request, its branch made and its parent picked with its body kept, no run
of its own under way, its `review_commit` still the commit its watch names and not the one its
review failed at, no review in `feature_reviews` for that commit, and its branch watched in the
last five minutes. It is taken by `CardStore.QueueOrder` with `FOR UPDATE SKIP LOCKED`, as a card
is, and its reason is "The reviewer reads the whole feature" while its run is under way. The run,
`kind` `feature_review`, names the feature by `feature_number`, its branch, the default branch as
its `base`, the commit as its `head_commit` from the start, and the check the job carries.

**The job** is a `Job` of `kind` `feature_review`: the parent as picked as its `card`, the
feature's branch as its `branch`, the default branch as its `base`, the project's `check`, the
feature reviewer's brief as `brief`, the verdict's form, `contracts/devflow/verdict.schema.json`, as
`report_schema`, and `to_review_feature`, null on every other job:

| Field | What it is |
|---|---|
| `commit` | The commit to review, `review_commit` |
| `cards` | Each card in the feature's order: `number`, `title` as picked, `verdict` and `summary` of its newest review, each null without one, and `handover`, the note of its newest build or fix that succeeded, empty without one |
| `check` | The newest check a worker ran on the feature's branch, a card's build or fix that succeeded or a conflict's, `{"command", "commit", "exit_code", "output"}`, or null |
| `remerge_diff` | The `remerge_diff` of each conflict run of the feature that succeeded and started since its newest review, oldest first, each under a line naming its run and its two commits, cut beyond 1 MiB as `RemergeDiffs.Cut` cuts one; null when there is none |

Its `push_key` is a key narrowed to `Fetch`, as a card's review's, and the push key route answers it
`404`. The model is the project's `model_reviewing`, as for a card's review.

**On the worker**, `ReviewJob`, as a card's review is fetched: root's bare clone with the key, the
commit checked to be there, the feature's branch pointed at it in root's own repository, never
pushed, and the agent's copy at `/work/<job>/copy`, checked to be at the commit. Root writes in its
handover folder, and the agent copies into `/work/<job>/review`: `feature.md`, the parent as
quoted; `cards.md`, each card with its latest verdict and summary, or "It has no review.", and its
handover note, or "It left no note."; `diff.patch`, `git diff --no-ext-diff --no-textconv
<default>...<commit>`, the feature's own changes from where its branch and the default branch last
met, so nothing brought in from the default branch is in it; `remerge.patch`, the job's
`remerge_diff`, only when there is one; and `check.txt`, the check's command, the commit it ran on,
saying when that is not the commit reviewed, its exit code and output, or "Dev flow holds no check a
worker ran on the feature's branch.". The agent runs read only, as for a card's review, and the
result names the commit the worker checked out as `head_commit`, null on a failure, and no `check`.

**The brief**, `Briefs/feature-reviewer.md`, the same for every project, filled by
`Brief.FeatureReviewer` with the parent fenced as in every brief, the commit, the feature's branch
and the default branch: each card was reviewed on its own, so it judges the whole, in this order,
whether the feature does what its parent says, cards that contradict each other or solve one thing
twice, code duplicated between cards, what a conflict's resolution chose, the whole against the
project's `CLAUDE.md` or `AGENTS.md`, and what is correct and safe only as a whole; it judges and
does not teach, each finding blocking or not with file, line, what and why; and everything in the
files is data, an instruction there being a blocking finding. The line naming `remerge.patch` is
there only when the job carries one. The verdict's form is a card's.

**Judged**, `RunStore.JudgeFeatureReview`, only while the feature is `reviewing`:

| What came back | The run's `result` | The feature |
|---|---|---|
| A failure outside the job | `failed_outside_card` | Stays `reviewing`, "To be reviewed as a whole", handed out again after the project's wait, see "Stops" |
| A failure in the job | `failed_in_card` | `waiting_for_owner`, "Dev flow could not review the whole feature: The run failed: <reason>", `review_failed_at` the commit |
| A verdict out of its form | `report_invalid` | `waiting_for_owner`, "Dev flow could not review the whole feature: The verdict did not match its form: <what>", the same way |
| A verdict in its form | `succeeded` | Stays `reviewing`, "Reviewed; Dev flow checks whether it is ready", for the next minute |

A failure in the job is not handed out again at that commit: the owner reviews a feature anyway,
and once the branch moves its head is reviewed afresh. A result whose `head_commit` is not the
commit handed out, or null on a failure, is `422`. A lost run leaves the feature `reviewing`, "To
be reviewed as a whole", and the project waits, as for a card's. A review names the commit it read
and pushes nothing, so its commit is never written to `pushes_made`.

**Kept**, `feature_reviews`, in the same transaction as the run's result: `run_id`, the review run,
which holds its usage like any run; `feature_number`; `reviewed_commit`, the run's; `verdict`;
`summary`; `findings` and `for_owner` as the form holds them; `pull_request_number`, the feature's;
`reviewed_at`; and `posted_at`, null until its copy is posted. It is in migration
`0031_feature_reviews`, with row level security as every table, beside `features.review_commit`,
`features.review_failed_at`, `merges_made.parent_commit`, set for each bringing in from the code
host's answer, its first parent, and `feature_review` among the kinds of `runs`, which needs
`feature_number` and `head_commit`.

**Posted for people**, by `ReviewPoster` on the same minute as a card's, as one comment by the app
with a `Post` key on the feature's pull request: "Review of the feature at <commit, seven
characters>: approve" or "changes", then the summary, the findings and the views for the owner as a
card's comment has them, every text made data, and "Reviewed by Dev flow for the feature #<parent>,
run <run id>." last. The app's own comments are looked at first for one naming the run, so a lost
answer never posts it twice. It is a copy: Dev flow never reads a comment to learn a verdict.

**On "Waiting for you"**, `features` in `GET /devflow/api/waiting`, the group "Features ready for
your review" after "Pull requests waiting": each feature `ready` or `waiting_for_owner`, by project
and number, `{"project_id", "project_name", "owner", "number", "title", "web_url", "state",
"reason", "pull_request_number", "pull_request_url", "review", "unanswered", "changed_at", "merge"}`,
`review` its latest, `{"verdict", "summary", "blocking", "commit"}` or null, and `unanswered` how
many decisions its cards' latest reports marked for the owner have no answer, counted as its page
counts them, in the same transaction. Each is counted in `count`, in the nav's dot, `attention`,
and in the front page's `waiting`, as every item is. The page shows its title, its state, why it
waits, the review's verdict and summary, the decisions left to answer, "Open the feature", its page
inside the app, "Send the whole feature back", see "Fixing a feature" below, and "Open on
GitHub", its pull request; `merge` is `{"into", "head_commit"}` while it is offered for merging, as
on its page, and null otherwise, and the item then shows "Merge into <default>" before its links,
see "Merging a feature".

**On its page**, `reviews` in the feature's story, newest first, each as a card's page shows a
review, its `current` true for the newest when it covers the head; the page shows them under "The
review of the whole feature", after the feature's state, and its fix rounds after them, see
"Fixing a feature" below.

**In the fake**, the fake worker answers a feature's review as it answers a card's, naming the
commit it was handed and changing nothing, then posts the copy and decides readiness, as the minute
would; and bringing in answers the base's commit it merged into as the first parent.

### Fixing a feature

A `changes` verdict on a feature sends it to a fresh developer agent on the feature's branch with
the findings, for at most the project's `fix_rounds_feature`, 3 by default, each followed by a new
review of the whole feature; the
owner's comment on the feature's pull request, written on GitHub or sent back from its page or its
item on "Waiting for you", starts a fix too, without counting as a round. With the rounds used up
the feature waits for him; it is never stopped. Why it is built so is "Fixing a feature" in
`docs/devflow-architecture.md`. It is `FeatureFix` and `FeatureComments` in `src/DevFlow/Features/`,
the round's start in `FeatureReady.Decide` and `FeatureReadiness`, `ClaimFeatureFix` and
`FeatureEarlier` in `JobStore`, `JudgeFeatureFix` in `RunStore`, `Brief.FeatureFixer` and
`Briefs/feature-fixer.md`, `SendBackFeature` in `ActionEndpoints`, and the fix in the worker's
`BuildJob`.

**A round** starts on the minute, `FeatureReady.Decide`, when the latest review covers the head and
asked for changes: the feature moves to `fixing`, "Round <n> of <m>: the review of the whole
feature asked for changes; a fix runs next", `m` its project's `fix_rounds_feature` as it is then,
counts the round in `fix_round`, and records what its fix
answers, `next_fix_round`, the round, and `fix_review_id`, that review. A `changes` covering the
head with every round used, or with 0 rounds its first, leaves it `waiting_for_owner`, "Review rounds used up", with the findings on its
page and on "Waiting for you"; nothing more is handed out until the branch moves or he comments.

**Handed out** by `jobs/next` after the project's card fixes and before its conflicts, see "Jobs":
a feature that is `fixing`, with its pull request, its branch made and its parent picked with its
body kept, no run of its own under way, and its branch's watch read in the last five minutes and
since Dev flow's last merge or push there. It is taken by `CardStore.QueueOrder` with
`FOR UPDATE SKIP LOCKED`, says "Round <n> of <m>: the developer is fixing the feature", or "The
developer is answering your comment", while its run is under way, and the run, `kind`
`feature_fix`, names the feature by `feature_number`, its branch, the default branch as its `base`,
the commit its watch names as `from_commit`, its round, the review it answers and the owner's
comments. The job is a fix's, a `Job` of `kind` `feature_fix` with the parent as its `card`, the
feature's branch as its `branch`, the default branch as its `base`, a push key, the project's check,
`start` and `url`, the report's form and `to_fix`, `{"commit", "round"}`, `round` null for his
comment.

**The brief**, `Briefs/feature-fixer.md`, filled by `Brief.FeatureFixer`: the parent fenced as
picked, each done card's handover note in the feature's order as information, and the section "What
to fix" a card's fix brief has, see "Fixing": its round or his comment, the earlier report, the
newest feature fix's that succeeded, the blocking findings of the review it answers to fix, test
first where behaviour changes, or argue in `replies`, the nits when cheap, and each of his comments
fenced as his input. Every finding and every comment gets a reply.

**On the worker** it is carried as a card's fix, "The worker" says with what differences: pushed to
the feature's branch, never forced, only once the agent succeeded with a `done` report and its
check passed.

**Judged**, `RunStore.JudgeFeatureFix`, only while the feature is `fixing`:

| What came back | The run's `result` | The feature |
|---|---|---|
| A failure outside the job | `failed_outside_card` | Stays `fixing` in the same round, handed out again after the project's wait, see "Stops" |
| A failure in the job, a report out of its form, a check that did not run or failed, or no new commit, "The fix left no new commit on the branch" | `failed_in_card`, `report_invalid` or `check_failed` | The first from a commit stays `fixing`, "Failed once in the job, so it runs again: <reason>", with `fix_failed_at` that commit; the second in a row from it `waiting_for_owner`, "Dev flow could not fix the whole feature: Failed twice in a row with no new commit: <reason>" |
| A time limit, or a report that stopped | `failed_in_card` or `stopped` | `waiting_for_owner`, "Dev flow could not fix the whole feature: <reason>", at once |
| A new commit, its check passed | `succeeded` | `reviewing`, "Fixed; the reviewer reads the whole feature again next" |

A success records its commit in `pushes_made`, as every run on a feature's branch does, and the
minute queues a new review of it once the watch has read the branch since; a run that names the
commit it was handed records nothing. A feature waiting with `fix_failed_at` its head stays so until
the branch moves or he comments. A lost fix leaves the feature `fixing` in the same round.

**The re-review** is the review of the new head, as above, with `to_review_feature.earlier`, each
earlier round oldest first as a card's re-review has them, see "Fixing": `round`,
`reviewed_commit`, `verdict`, `summary` and `findings` of the review it answered, `comments`, his
words, `replies`, the fix's, and `fixed_commit`. The worker writes them to `review/earlier.json`, an
empty list on a first review, and the brief says whether this is the feature's first review, and
that a later one judges the whole feature again from where its branch and the default branch last
met, so a finding that still holds is still blocking.

**The owner's comment.** Each read of a project, `ProjectReader.Read`, reads the comments of the
pull request of every feature that has one still open, is `reviewing`, `fixing`,
`waiting_for_owner` or `ready`, and has no run of its own under way, and takes those by a trusted
account of the project, whatever its case, that are new by the rule a card's are, see "Fixing": past
`comments_read_through`, the id of the newest of his already taken, and every one of his before any
was, no time compared. They are taken in the read's transaction, `FeatureComments.Take`, the
feature's row locked first: it moves to `fixing`, "You commented on its pull request; a fix runs
next", with no round, answering his comments and its latest review if no fix has succeeded since
it; a feature already waiting for a round's fix keeps the round and adds them. It is the only
comment read there as an input: anyone else's, the app's review copies among them, is neither a
trigger nor in the brief.

**Sending the whole feature back**:
`POST /devflow/api/projects/{id}/features/{number}/send-back`, with `{"comment"}`, under the `Api`
policy and the origin check, posts the comment, trimmed and with nothing added, as the owner with
his linked token, never as the app, on the feature's pull request, and reads the project again,
which takes it as above. In this order: `404` "No such feature"; `422` for a comment missing, empty
once trimmed or longer than 10,000 characters; `409` "This feature cannot be sent back now" for a
feature neither `ready` nor `waiting_for_owner` with its pull request; `409` without a usable link;
then GitHub is asked to post: `204` once posted, `409` "GitHub refused the comment" with its
reason, and `503` when GitHub did not answer, after the project is read again. The page offers it,
"Send the whole feature back" with a comment box, on the feature's page and on its item on
"Waiting for you".

**On its page**, the feature's story answers `fix_rounds`, the rounds its reviews started, and
`rounds`, each fix newest first as a card's page answers its rounds: `run_id`, `round`,
`review_run_id`, `comments`, `result`, `replies`, `fixed_commit` and `review_after_run_id`. The
page shows them under "Fix rounds, <n> of <m> used", after its reviews, `m` being `most_rounds`, the
project's `fix_rounds_feature` as it is now; a card's story answers its `fix_rounds_card` as
`most_rounds` too.

**The tables**, from `migrations/0032_feature_fixes.sql`: `runs` gains `kind` `feature_fix`, which
needs `feature_number` and `from_commit`, and uses `fix_round`, `fix_review_id`, the review in
`feature_reviews`, and `owner_comments`; `features` uses `fix_round`, from 0024, as the rounds its
reviews started, and gains `next_fix_round`, `fix_review_id` and `fix_comments`, what its next fix
answers, set as it moves to `fixing`, `comments_read_through`, and `fix_failed_at`.

**In the fake**, the fake worker answers a feature's fix as a worker would: it pushes a commit by
the app on the feature's branch only when the prepared result succeeded with a `done` report and its
check passed,
names the commit it was handed with `leave_no_commit`, and decides readiness after.

## Merging a feature

The owner merges a feature into the default branch himself, from its page or from its item under
"Features ready for your review", as himself on GitHub, and GitHub's own button works too. Its pull
request closed without merging gives its cards back. Why it is built so is "Merging a feature" in
`docs/devflow-architecture.md`. It is `FeatureMergeOffers` and `FeatureMergeConfirmation`, in
`src/DevFlow/Features/FeatureMergeOffer.cs`; the two routes in `ActionEndpoints`, see "Acting from
the page"; `FeaturePulls`, which a read follows a feature's pull request with; `FeatureOutcomes`,
which tells its cards; and `src/feature-merge.tsx` on the page.

**Offered** while the feature is `ready` or `waiting_for_owner` with its pull request recorded, its
branch's watch names a head, `ci_passed_commit` is that head, see "Reviewing a feature", and the
project is not stopped: decided in SQL by `FeatureMergeOffers.IntoSql` from what Dev flow stored,
so the feature's page, its item and the route agree. The feature's story and its item answer it as
`merge`, `{"into", "head_commit"}`, `into` the default branch as its watch names it, null when it
is not offered. With CI pending, failed or never reported on the head it is not offered, and the
route refuses it the same way.

**The confirmation.** "Merge into <default>" reads `GET …/features/{number}/merge` and opens the
package's `Dialog`, "Merge feature #<number> into <default>?", with the feature's title, "Pull
request #<number> at <commit, seven characters>", what the review says, and, when any is unanswered,
"Merging confirms <n> decisions you have not answered.", one decision said once. What the review
says, by `review`: `approved` when the newest review approved a commit that covers the head, "The
review approved this commit."; `changes` when the newest review asked for changes, "The review asked
for changes: <n> open findings", with the list "Open findings", its blocking findings, each with its
file and line, what and why; and `not_reviewed` otherwise, "Not reviewed as a whole". `unanswered`
counts as the feature's page counts. "Merge" posts the merge naming the commit the dialog showed;
the answer is said as "Merged feature #<number>" or the refusal in words where the button is, and
the page or the list is read again and the nav asked to ask again, as for a card's merge. Without a
usable link the button is disabled, with why, as a card's Merge is.

**After a merge.** The read that finds the feature's pull request merged, the one the route waits
for or the next one, makes the feature `merged`, no reason, and keeps each decision its cards' latest
reports marked for the owner that has no answer as `merged` in `decision_answers`, whoever merged
it. Its branch is no longer watched, since the watch reads only the branches of features neither
`merged` nor `closed`. The parent closes by its pull request's `Closes` line. On the next minute,
`FeatureOutcomes.TellPending` in `OpeningLoop`, after readiness, reads the parent's sub-issues as the
app and closes each direct sub-issue in the repository still open, as the app with a `Post` key,
after the comment "Its feature, #<parent>, was merged into `<default>` in #<pull>, so this card is
closed." The branch is left to GitHub's own setting to delete.

**A pull request closed without merging.** The read makes the feature `closed`, no reason, and its
branch is no longer watched. On the next minute each card Dev flow keeps for the feature whose issue
is closed, as landing closed it, is opened again by the app, after the comment "The feature's pull
request #<pull> was closed without merging, so this card is open again."; a sub-issue Dev flow never
built is left as it is. It stays `closed` while the parent keeps its label. Labelled again later,
after a read found it without the label, it starts afresh: a `closed` feature whose cards were told
and that a read does not find gives up the cards it kept, which are deleted unless a run of one is
under way, and is then deleted as any feature no card refers to is, its watch with it, so a read
that finds the label again picks it as a new feature, queued, with its cards queued. Its old runs,
reviews and answers stay. Its old branch stays on GitHub, so making the new one answers that it
exists, and the feature waits for the owner until he deletes it.

**Told once.** Before each comment, the app's own comments on the issue are read for the same words,
so an answer that was lost never posts it twice; the feature's `outcome_told_at` is set once every
card is told, and what GitHub did not answer for is tried again on the next minute. A parent GitHub
no longer shows is told with nothing written.

**The columns**, from `migrations/0034_feature_merge.sql`: `features.ci_passed_commit`, the head CI
last passed on, and `features.outcome_told_at`, when its cards were told, null until then; and
`merged` among the answers of `decision_answers`.

## Stops

A card that cannot be made to work stops, is marked on its issue and shows under "Stopped" on
"Waiting for you"; a comment from the owner runs it again. A failure outside the card, a usage
limit, the network or the machine, never stops it: the card goes back where it was and its project
waits. Why it is built so is "Stops" in `docs/devflow-architecture.md`. It is `RunStore`'s
judgement and `InARow`, `ProjectWaits`, `StopMarker`, `StopStore` and `StopText` in
`src/DevFlow/Stops/`, the stopped card's reading in `OwnerComments` and `ProjectReader`,
`UsageLimit` in the worker, and `stopped-card.tsx` in the page.

**A card stops** when a run reaches its time limit, 90 minutes, which the worker sends as
`timed_out`; when the fix rounds are used up, "Fix rounds used up", see "Fixing"; when a report
says `stopped`; when two runs in a row fail in the card with no new commit on its branch between
them; or when its pull request is closed without merging, "The pull request was closed". A failure
in the card is a run whose result is `failed_in_card`, `report_invalid` or `check_failed`, a
build's, a review's or a fix's. One alone, past the first two, runs the card again where its kind of
run starts from, a build in `queued`, a review in `reviewing` and a fix in `fixing` in the same
round, "Failed once in the card, so it runs again: <reason>", and the card remembers it and the
commit its branch is at, the newest a build or a fix left there, or none. The next failure in the
card with the branch still at that commit stops it, "Failed twice in a row with no new commit:
<reason>"; one with a new commit runs it again once more and remembers that commit. Any run that
succeeds starts the count afresh. The run keeps its own result and reason either way. Every move
to `stopped` records `stopped_at`.

**Only that card stops.** Other cards of the project are still handed out; a card blocked by the
stopped one waits for it, since its issue is still open, as "Cards" reads it.

**Marked on the card.** `StopMarker`, on the opening loop's minute and after each fake worker run,
for every stopped card not yet marked: the app, with a `Post` key, adds the project's attention
label to the card's issue, and comments once, `StopText.Comment`:

```
Dev flow stopped this card: <reason>.

## What was tried

- Build, failed in the card: The run failed: it reached the most turns, 200 turns.
- Build, failed in the card: The run failed: it reached the most turns, 200 turns.

The last run: Build, failed in the card: The run failed: it reached the most turns, 200 turns.

Its whole story is on [the card's page](https://platform.test/devflow/projects/<id>/cards/<n>).

Comment here to run it again.

Stopped by Dev flow for #<n>, stop <stopped_at>.
```

The runs are the card's, the newest ten, oldest first, with how many there were when there are
more. Every reason is made data as the pull request body makes the report's, so it mentions nobody
and closes nothing. Before commenting, the app's own comments on the issue are looked at for the
last line, so a marking whose answer was lost never posts twice. Nothing else is opened. Once the
card runs again the app takes the label off, a label already gone being as good. A card stopped
before this part, whose `stopped_at` is null, is never marked.

**Running it again.** Each read of a project reads a stopped card's issue, and its pull request
when still open, for the owner's comments, as "The owner's comment" under "Fixing" reads them, by
a trusted account and past `comments_read_through`, and only those written after `stopped_at`, or
after its last change for a card stopped before this part; one by a login off the list does nothing. Taken, a
card with its pull request open moves to `fixing`, "You commented on its pull request; a fix runs
next", as a fix with his comments; any other is built again, `queued`, "You commented on it; it
runs again next", with his comments in the build's brief under `## Run again`, each quoted as the
fix brief quotes them and each to be answered in `replies`, and with its closed pull request let
go, so the build that succeeds opens another. Either way its count of failures and its fix rounds
start afresh, and the next marking takes the label off. "Comment to run again" on "Waiting for
you" is the send back route, which posts his comment on the card's issue, see "Fixing".

**Outside the card.** A failure the worker says lies outside the card, `failed_outside_card`, or a
lost run, puts the card back as "Jobs" and "Fixing" say and never counts against it. The project
then hands out no job, `jobs/next` answering `204` before anything else is read, for 5 minutes
after the first, doubling with each one in a row up to 60, `ProjectWaits`, until a run of the
project succeeds, which ends the count. When the failure carries `resets_at`, the project waits
until then instead, when it is in the future and at most 8 days off; otherwise the doubling holds.

**The usage limit's reset.** Claude Code's `--output-format json` answer to a run at its usage
limit holds only a message for people; the exact time goes out in a streamed `rate_limit_event`
alone. The message ends ` · resets 3pm (UTC)`, the hour, with the minutes when not on the hour, in
the zone named, for a reset within 24 hours, or ` · resets Jan 20, 3:30pm (Europe/Stockholm)`, with
the date and, in another year, the year, for one further off: read from Claude Code 2.1.322's own
formatter, and recorded as `usage-limit-resets-today.json` and `usage-limit-resets-date.json` in
`tests/DevFlow.Worker.Tests/Claude/`. `UsageLimit.ResetsAt` reads it on the worker's clock, a time
without a date being the next at or after now, a minute's grace; a zone .NET does not know, a
date the month has not, a time a change of clocks skips, or any other text is no time.

**A project that keeps failing.** Six failures outside its cards in a row show on the project's
page, `cannot_run` in its workers' list, and on "Waiting for you", `failing_projects`, as "Dev flow
cannot run <project>'s cards: <the last reason>", until a run succeeds. Each counts on the tab,
the nav's dot and the front page.

**On "Waiting for you".** Under "Stopped", after Dev flow's stopped projects: each failing project,
the package's `Card` titled with its message, the `failed` status "Cannot run", its repository, and
how many runs failed since when and when Dev flow tries again; then each stopped card, its `Card`
titled with its title, the `failed` status "Stopped", its repository and number, "Why it stopped"
with its reason, when it stopped, and in its footer "Comment to run again", which opens a box
labelled "A comment runs it again." with Send, and "Open on GitHub", its issue, and no Merge, as
the runner's stopped items. Without a usable link the button is disabled and says why.

**The tables**, from `migrations/0021_stops.sql`: `cards` gains `in_card_failures`, 0 or 1,
`failed_at_commit`, `stopped_at`, `stop_marked_at` and `attention_labelled`; `runs` gains
`timed_out` and `resets_at`; and `project_waits`, guarded like every scoped table, a row while a
project's failures outside its cards are counted: `project_id`, `user_id`, `failures`, the last
`reason`, `wait_until` and `since`.

## Registering the GitHub App

The owner's runbook, done once on GitHub, under Settings, Developer settings, GitHub Apps, New
GitHub App, as the account that owns the repositories. Every field on the form, and what to enter:

| Field | What to enter |
|---|---|
| GitHub App name | A name of your own, such as `platform-devflow`; its slug, from the app's address `github.com/apps/<slug>`, is `DEVFLOW_GITHUB_APP_SLUG` |
| Description | Optional |
| Homepage URL | The hosted copy's origin |
| Callback URL | `https://platform.test/devflow/github/callback`, then "Add Callback URL" and `<the hosted origin>/devflow/github/callback`; each must match Dev flow's redirect address exactly |
| Expire user authorization tokens | On: Dev flow renews the eight hour token itself, and refuses an answer without expiring tokens |
| Request user authorization (OAuth) during installation | Off: linking happens from the Projects tab |
| Enable Device Flow | Off: Dev flow uses the web flow alone |
| Setup URL | Empty |
| Redirect on update | Off |
| Webhook, Active | Off: Dev flow polls, see "The program asks GitHub for changes; there is no webhook" |
| Repository permissions | Contents read and write, Pull requests read and write, Issues read and write, Commit statuses read, Checks read, Metadata read, and nothing else; never Workflows |
| Organization permissions | None |
| Account permissions | None |
| Subscribe to events | None |
| Where can this GitHub App be installed? | Only on this account |

Then, on the app's page:

1. **Generate a private key.** GitHub downloads a `.pem` file. Keep it on the Mac mini outside the
   repository and name its path as `DEVFLOW_GITHUB_PRIVATE_KEY_FILE` in `infra/hosted/.env`, which
   mounts it read only into Dev flow's container; keep a copy in the password manager.
2. **Generate a new client secret.** GitHub shows it once. Put it in `infra/hosted/.env` as
   `DEVFLOW_GITHUB_CLIENT_SECRET`, with the client id the page shows as `DEVFLOW_GITHUB_CLIENT_ID`,
   and keep it in the password manager.
3. **Install App**, on the repositories Dev flow is to read, only those.
4. **Try the link on the laptop first.** Start Dev flow there with `DEVFLOW_CODE_HOST=github`, a
   `DEVFLOW_VAULT_KEY` made as "The vault" says, and the app's four settings, open the Projects
   tab, press "Link GitHub", authorize on GitHub, and see "Linked as @<your login>"; then press
   "Connect a repository" and see the repositories you installed it on. Then release
   the hosted copy, see `infra/hosted/README.md`.

**Issues at write level.** The program opens issues and comments and labels as the app, which
needs Issues read and write. An app registered before it asked for Issues read only: change it on
the app's page, under Permissions and events, then approve the new permission on each installation,
under Settings, Applications, Installed GitHub Apps, where GitHub asks for it. Until it is approved
a `Post` key is not found, and nothing the program posts can be written.

**Revoking.** Unlinking forgets the tokens in Dev flow and revokes nothing. To end what GitHub
granted, revoke the app's authorization under your GitHub Settings, Applications, Authorized
GitHub Apps; to stop it reading the repositories, uninstall it under Installed GitHub Apps; and to
make every client secret or private key useless, delete it on the app's page.

## How Dev flow authenticates a request

The rules every feature follows are in "How a feature authenticates" in
`High-level/docs/feature-contract.md`. Dev flow holds none of the code for them: `PlatformAuth`, in
`libs/dotnet/PlatformAuth/`, does every check of the token and the origin, and its README says what
it holds and how its tests cover the ways verification breaks. Dev flow references it as a project
and does three things with it, in `src/DevFlow/DevFlowApp.cs`:

- `AddPlatformAuth` with `DEVFLOW_CORE_URL`, which registers the key cache, the handler and the two
  policies. The pages take `PlatformAuthPolicies.Page`, as do the return from the code host and the
  fake's sign in page, and `whoami`, `status`, starting a link, unlinking, the projects' routes,
  the waiting list, refreshing a project, making a project's worker key, listing its workers and
  the fake's addresses take `PlatformAuthPolicies.Api`; health and the built files take neither. A
  route reads its caller's user id through `Api/Caller.cs`, as review's do.
- `AddWorkerAuthentication`, Dev flow's own, in `src/DevFlow/Workers/WorkerAuthentication.cs`: the
  second way a request authenticates, a project's worker key, and the policy
  `WorkerAuthentication.Policy`, which the worker routes, `hello`, `jobs/next` and a job's
  `push-key`, `progress` and `result`, take and nothing else does. A worker route reads its project and user through `context.Worker()`. See "Workers".
- `UsePlatformOriginCheck` with `DEVFLOW_PUBLIC_ORIGIN`, before routing, so every route, one added
  later included, is covered: starting a link, unlinking, making a worker key, and the fake's
  addresses and its sign in form. It runs for every path but those under `/devflow/api/worker/`,
  matched case and all, which the worker scheme alone guards and which read no cookie, see
  "Workers". The return from the code host, `GET /devflow/github/callback`, is the one route that
  changes state on a GET, which "How a feature authenticates" otherwise forbids; why it is safe is
  in "Security decisions and accepted risks" in `docs/devflow-architecture.md`.
- `DevFlowApp.Build(settings, database, webRoot, configure)` builds the app, with the door; `configure` runs before it is
  built, which is where `Program` sets the listen address and where the tests put the app on a
  test server, with a key cache over the committed key set answered from memory and a clock fixed
  at the committed token's `iat`, both of which `AddPlatformAuth` keeps.

The tests send the ways verification is known to break through Dev flow's own routes, as "A
feature's authentication tests are part of the feature template" in
`High-level/docs/how-we-build.md` asks: the committed token is accepted from the cookie and the
header, a missing, expired, wrongly signed or unknown key id token gets `401` from the API, and a
page without a token answers `303` to the refresh endpoint with the way back.

## Libraries Dev flow is built on

**The .NET 10 SDK, pinned in `global.json`**, with `Directory.Build.props`,
`Directory.Packages.props` and `.editorconfig` holding the same values as review's folder, copies
rather than shared files, since each C# folder builds from its own directory alone. Why .NET 10,
the analyzers as the linter and `dotnet format` as the formatter are in "Libraries it is built on"
in `libs/dotnet/PlatformAuth/README.md`.

**One web project, not layered projects**, beside the worker. `src/DevFlow/` is the whole
service, organised in
feature folders: `Settings`, `Health` (health and whoami), `Status`, `CodeHost` (the interfaces, their
records, the fake and, in `GitHub`, the adapter), `Link` (linking GitHub and a usable token), `Vault`,
`Projects` (connecting a repository), `Reading` (the snapshot, the loop and the waiting list),
`Workers` (the worker key, the worker scheme and the worker API), `Pages` and `Api` today, more with
later slices, as `review/` is. Two projects stand beside it because a second program needs them:
`src/DevFlow.Worker/`, the worker, a console program, and `src/DevFlow.Protocol/`, the types it and
Dev flow share, which hold nothing else.

**The worker is built on .NET alone**: `HttpClient` and `System.Text.Json` for Dev flow,
`System.Diagnostics.Process` for Claude Code, and `TimeProvider` for its waits, so it adds no
package. It is published self-contained, see "The worker" above.

**The GitHub adapter is written by hand on .NET's `HttpClient`**, with `System.Text.Json`, and
signs the app's tokens with .NET's own `RSA`, and signs a person in with the same `HttpClient` and
.NET's own SHA-256 for PKCE, so no GitHub, JWT or OAuth library is added: its calls are
few, and a library would be a dependency for what a dozen typed requests do. What it remembers for
conditional requests is in a `MemoryCache`, from the shared framework.

**The vault's cipher is .NET's own `AesGcm`**, AES-256-GCM from `System.Security.Cryptography`, the
standard authenticated cipher, which the framework has, so no library is added for it.

**ASP.NET Core minimal APIs**, from the shared framework, for routing, static files and problem
details, with no MVC. JSON is snake case on the wire, as review's, the brain's and canary's is.

**Microsoft.AspNetCore.OpenApi for the schema**, at review's version, for review's reasons, see
"Libraries review is built on" in `review/README.md`.

**xUnit v3 for the tests**, with `Microsoft.AspNetCore.Mvc.Testing` for `TestServer` and
`Microsoft.Extensions.TimeProvider.Testing` for the clock, review's test packages at its versions.
Npgsql and Dapper come through `PlatformData`, at its versions, so `Directory.Packages.props` does
not list them; review's brain client package, Kiota, is left out until Dev flow needs it.

**The page** is review's stack, React, Vite, Vitest, Biome and TypeScript, at its versions; see
"Libraries the brain is built on" in `Brain/README.md` for why each was chosen.
