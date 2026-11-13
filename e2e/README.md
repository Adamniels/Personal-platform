# e2e

The platform's acceptance suite. Every planned acceptance criterion that has a UI is a test here,
written with Playwright (Python) and run against `https://platform.test`, the way a person would use
the platform. A slice is not done until its criteria pass here.

## Running it

The stack must be up, as `.claude/skills/pick-up-slice/SKILL.md` describes: from `infra/`,
`docker compose up -d --wait` (step 4, Preflight); `migrate` as the owner role from `Core/` (step
6, Branch); every frontend built, since Caddy serves the gallery, the brain serves the memory
centre from `Brain/web/dist` and will not start without it, and the accessibility checks of the
gallery, the nav bar and the memory centre inject `design/ui/node_modules/axe-core/axe.min.js`,
from the repository root:

```zsh
for dir in $(bash scripts/ci/frontends.sh); do (cd "$dir" && npm ci && npm run build); done
```

and the core, canary and the brain, on 8080, 8081 and 8082, as "Starting the platform" in step 9
starts them. Then, from this directory:

```zsh
uv run playwright install chromium   # once
uv run pytest
uv run ruff check
uv run ruff format --check
```

With the stack down the tests fail with a connection error, which is the expected red.
Screenshots land in `screenshots/`, which is git ignored.

## Fixtures

- `base_url` is `https://platform.test` (set in `pyproject.toml`), and the browser context ignores
  HTTPS errors, so the suite does not depend on the laptop's keychain trusting Caddy's local CA.
- `invite_code` runs `cargo run -q -- invite` in `Core/` and returns the code. Each test gets its own.
- `reset_token` is a function: `reset_token(email)` runs `cargo run -q -- reset <email>` in `Core/`
  and returns the token. A function rather than a value, since the account usually exists only once
  the test has registered it.
- `viewport` runs the test twice, `[desktop]` (1280x800) and `[phone]` (390x844), and sizes the page.
- `capture(page, name)` writes a full page screenshot to `screenshots/<name>-<viewport>.png` and
  returns the path. It needs the page to be sized by the `viewport` fixture.

`register_and_log_in(page, invite_code)` registers a fresh account with the code, logs in and
returns its email, ending on the front page, for a test that only needs to be logged in. Its two
halves are there for a test that logs in from somewhere else: `register(page, invite_code)` returns
the email and password and ends on the login page, not logged in, and `log_in(page, email,
password)` submits the login form the page is on, which ends wherever its return path says.

`/register` does not prefill the code from a query string, so tests fill the `code` field.

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
