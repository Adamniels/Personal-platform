# e2e

The platform's acceptance suite. Every planned acceptance criterion that has a UI is a test here,
written with Playwright (Python) and run against `https://platform.test`, the way a person would use
the platform. A slice is not done until its criteria pass here. The same suite also runs against the
hosted copy's containers, from `infra/hosted/check.sh`, see "Against the hosted copy" below.

## Running it

The stack must be up, as `.claude/skills/pick-up-slice/SKILL.md` describes: from `infra/`,
`docker compose up -d --wait` (step 4, Preflight); every service's migrations, through
`scripts/agent/services.sh` (step 6, Branch); every frontend built, since Caddy serves the
gallery, the brain serves the memory centre from `Brain/web/dist` and will not start without it, and the accessibility checks of the
gallery, the nav bar and the memory centre inject `design/ui/node_modules/axe-core/axe.min.js`,
from the repository root:

```zsh
for dir in $(bash scripts/ci/frontends.sh); do (cd "$dir" && npm ci && npm run build); done
```

and every service in `infra/services.json` on its port. `bash scripts/agent/gate.sh --start-platform`,
from the repository root, does all of it but the preflight, and `--stop-platform` stops the services
again. Then, from this directory:

```zsh
uv run playwright install chromium   # once
uv run pytest
uv run ruff check
uv run ruff format --check
```

With the stack down the tests fail with a connection error, which is the expected red.
Screenshots land in `screenshots/`, which is git ignored.

The agents do not run the suite by hand: `scripts/agent/gate.sh` runs the tests a change can reach,
the files `scripts/agent/e2e-scope.sh` picks from each service's `e2e` globs in
`infra/services.json`. CI runs all of it on every push.

## Running in parallel

`addopts` runs the suite on four workers (`-n 4`, pytest-xdist), each with its own browser, which
takes the suite from about 20 minutes to a fraction of it. It holds because every test signs up an
account of its own, and every feature's state, Dev flow's fake code host and fake worker included,
belongs to an account. `-n 0` runs it in one process, which a debugger needs.

A test that changes state no account owns is marked `serial`. Every serial test holds one exclusive
file lock, `platform-e2e-serial.lock` in the system's temp folder, for its whole run, fixtures
included, so no two run at once on any worker; the rest run beside them. Today that is the backup
line's two tests, which read and replace the hosted copy's one status file. A lock rather than
pytest-xdist's `xdist_group`, which 3.8.0 cannot apply under pytest 9: it renames each test's node
id in a field pytest 9 no longer reads, so the grouping silently does nothing.

## Fixtures

- `base_url` is the one address the suite uses: `https://platform.test` by default, set in
  `pyproject.toml`, and another with pytest's `--base-url`. The agents' run passes the dev stack's
  origin, `bash scripts/agent/services.sh origin`, which carries a port on the Mac mini, see "The
  HTTPS port" in `infra/README.md`. A test that needs the address takes the
  `base_url` fixture and compares against it, as in `expect(page).to_have_url(f"{base_url}/review")`;
  none spells it out, which `tests/test_base_url.py` checks, and none reads it from an environment
  variable. The browser context ignores HTTPS errors, so the suite does not depend on the laptop's
  keychain trusting Caddy's local CA, nor on the hosted check's test front's.
- `invite_code` runs the core's `invite` command and returns the code. Each test gets its own.
  `another_invite_code` is a second one, for a test that registers two accounts.
- `reset_token` is a function: `reset_token(email)` runs the core's `reset <email>` command and
  returns the token. A function rather than a value, since the account usually exists only once
  the test has registered it.
- The core's command is `cargo run -q --` in `Core/`, the dev core's. With `E2E_CORE_COMMAND` set it
  is that value instead, split as a shell would and run from the repository root with the command's
  arguments appended; `check.sh` sets it to a `docker compose ... exec -T core platform_core` into
  the hosted copy's core. Set but empty counts as unset. A command that fails or takes more than ten
  minutes fails the test, with the command line in the message.
- `backup_status` is a function: `backup_status(status)` replaces the hosted copy's backup status
  file, `/status/backup.json` in the backup's container, with `status` as JSON, for a test that needs
  the front page's backup line in a chosen state. The real file is read before and written back
  after the test. It runs two commands from the repository root, split as a shell would:
  `E2E_BACKUP_STATUS_READ`, which prints the file, and `E2E_BACKUP_STATUS_WRITE`, which writes its
  standard input to it, to a temporary file renamed over it as the backup job does. `check.sh` sets
  both to a `docker compose ... exec -T backup`; without them a test that needs the file fails
  saying so. No test helper is in the backup image.
- `devflow_world` is a function: `devflow_world(world)` replaces Dev flow's fake code host's world
  for the account the page is logged in as, by a `PUT /devflow/api/fake/world` through the page's
  own session, with the platform's origin, so it lands in this test's account and no other. Call it
  after logging in. The world is a dictionary, the document "The fake" in `devflow/README.md`
  describes; `devflow_worlds.py` beside this file builds one, so a test says only what matters to
  it: `one_repository()` makes a world with one installation, on the repository owner's account,
  and one empty repository,
  `add_pull_request(world, number, ...)` adds a pull request with any fields the test cares about,
  its conversation, and the statuses and check runs on its head commit, made with `comment`,
  `status` and `check_run`, and `add_file` adds a file on a branch. `add_screenshot(world, number,
  name, content)` adds a pull request's screenshot as `scripts/agent/screenshots.sh` commits one,
  on `pr-screenshots`, and returns the line that script prints for the body; `png(width, height,
  rgb)` makes a one colour PNG for it, with no image library. It works on the dev stack and in
  the hosted check, both of which fake GitHub. `link_github(page, base_url)` links the account's
  GitHub through the fake's sign in page, from Dev flow's Projects tab, which the page must be on,
  as connecting a repository needs. A project shows its setup steps in place of its cards until a check of
  its setup has passed: `with_setup(world)` puts a `.devflow/project.json` naming `CHECK` on the
  world's default branch, and `registered(page, base_url, project)` reads the project and has the
  fake worker, which says hello as `devflow-fake-worker`, pass that check, through
  `fake_check(page, base_url, project, exit_code, output)`, which a test of a failed check calls
  itself.
- `viewport` runs the test twice, `[desktop]` (1280x800) and `[phone]` (390x844), and sizes the page.
- `capture(page, name)` writes a full page screenshot to `screenshots/<name>-<viewport>.png` and
  returns the path. It needs the page sized by the `viewport` fixture, or set to `WIDE` (1920x1080),
  which it names `wide` and which the fixture never runs a test at.

`register_and_log_in(page, invite_code, base_url)` registers a fresh account with the code, logs in and
returns its email, ending on the front page, for a test that only needs to be logged in. Its two
halves are there for a test that logs in from somewhere else: `register(page, invite_code)` returns
the email and password and ends on the login page, not logged in, and `log_in(page, email,
password)` submits the login form the page is on, which ends wherever its return path says.

`open_section(page, label, name)` opens a section of a page by its tab's name, among the tabs
named `label`: it clicks the tab when the tab list is shown, and chooses the option when the
select that stands for more than five tabs on phone is, so a test passes at both viewports.
`sections(page, label)` is whichever of the two is shown, for a test that waits for the page to
draw. See "Tabs" in `design/ui/README.md`.

`/register` does not prefill the code from a query string, so tests fill the `code` field.

## The look checks

`look.py` beside this file holds the rules of `design/DESIGN.md` that hold on every page, as
functions a test calls on an open page once it has drawn:

- `assert_headings_are_capitals(page)`: every visible element in the heading font is in capitals at
  weight 600, hard rule 12.
- `assert_nothing_scrolls_sideways(page)`: the document does not scroll sideways, and no visible
  element whose `overflow-x` is `auto` or `scroll` has more to scroll than it shows, hard rule 13.
- `assert_active_is_inverted(page)`: no visible element with `aria-current="page"` or
  `aria-selected="true"` is marked in `--accent-border`, and each tab or nav item among them has the
  `--text` fill, hard rule 11. A selected table row or option keeps its own rule, `--info-soft`. The
  current item of an in-page breadcrumb, a `nav` named "Breadcrumb", is no nav item or tab, so it
  needs no fill, though it is still never marked in amber.
- `assert_nothing_active_is_amber(page)`: no visible element with `aria-current` or
  `aria-selected="true"`, a selected table row or option included, has a border, an outline or a
  box shadow in `--accent` or `--accent-border`, hard rules 4 and 11.

Each reads the elements inside open shadow roots too, so the nav bar is checked as a person sees
it, and fails naming the elements that break its rule.

`tests/test_look.py` runs them on every page of the platform, at desktop and phone width and in
both themes. Its list of pages, `PAGES`, is written out one entry per page with how it is opened,
so a slice that adds a page adds its entry there. It opens each page in one state, as a fresh
account with one Review card due; a slice that restyles a page also calls the checks on each of
the page's states, as `test_the_core_pages_follow_the_look` in `tests/test_style.py` does for the
core's pages. They are not a replacement for the page's own tests of its look, only the rules
every page shares.

## Markers

Three markers, registered in `pyproject.toml`, which runs with `--strict-markers` so a misspelt one
fails:

- `dev_stack_only(reason=...)`, for a test that cannot hold against the hosted copy. The reason says
  why. A test that fails against the containers because of the hosted shape, a file missing from an
  image or a wrong setting in the compose file, is fixed there and never marked. One test carries
  it: the front page has no backup line on the dev stack, where `CORE_BACKUP_STATUS_FILE` is unset,
  and the hosted copy sets it.
- `hosted_only`, for a test that needs something only the hosted copy has: the backup line's tests,
  which need the backup job and its status file.
- `serial`, for a test that changes state no account owns, so no two such tests run at once; see
  "Running in parallel".

A plain `uv run pytest` is against the dev stack and leaves `hosted_only` out, through `addopts`.
A `-m` on the command line replaces that one.

## Against the hosted copy

`bash infra/hosted/check.sh`, from the repository root, starts a throwaway hosted copy, runs one
`backup now` in it, and runs, from this folder, with `<exec>` standing for
`docker compose -p hostedcheck --env-file <its env file> -f infra/hosted/compose.yaml exec -T`:

```zsh
E2E_CORE_COMMAND="<exec> core platform_core" \
  E2E_BACKUP_STATUS_READ="<exec> backup cat /status/backup.json" \
  E2E_BACKUP_STATUS_WRITE="<exec> backup sh -c 'cat > /status/backup.json.tmp && mv /status/backup.json.tmp /status/backup.json'" \
  uv run pytest --base-url https://platform.test:9443 -m "not dev_stack_only"
```

It installs nothing: `uv` and Playwright's Chromium must be there, as for a dev run, and it says so
when one is missing. `platform.test` must resolve to `127.0.0.1`, as for a dev run; the port is the
test front's. See "check.sh" in `infra/hosted/README.md`.

A test simulates an expired access token by deleting the `__Host-access` cookie with
`context.clear_cookies(name="__Host-access")`. The cookie's `Max-Age` matches the token's
lifetime, so at expiry the browser drops it and makes exactly that request, without the thirty
minute wait. `tests/test_refresh.py` does this.

## Libraries

**uv for the toolchain**, as in `canary/`: a locked `uv.lock` and the Python pinned in
`.python-version`.

**pytest with pytest-playwright (0.10.0, playwright 1.65.0).** Chosen because the plugin supplies
the `page` fixture, browser lifecycle and `base_url`, so tests are plain functions like canary's.
Playwright's own Node test runner was the alternative, but it would add a second language to a
repository that already has Rust and Python. Playwright over Selenium for auto-waiting assertions
(`expect`), which removes most sleeps and flakiness.

**pytest 9.2.0** and **ruff 0.17.4** with default rules, as in `canary/`.

**pytest-xdist 3.8.0**, for the four workers in "Running in parallel": the standard way to run a
pytest suite in processes, and pytest-playwright starts a browser per worker by itself. Its
`--dist loadgroup` does not work under pytest 9, which is why `serial` is a lock.
