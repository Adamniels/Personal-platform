"""`infra/postgres/initdb/brain.sh`, the script that creates the brain's
database and roles, run against the real Postgres a second time."""

import os
import shutil
import subprocess
from pathlib import Path

from psycopg.conninfo import conninfo_to_dict

from tests.harness import admin_connection, admin_url

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "infra/postgres/initdb/brain.sh"

# Not the real passwords: a second run must leave the roles as they are, so
# whatever is passed here never reaches them, and the test checks it does not.
THROWAWAY = {
    "BRAIN_OWNER_PASSWORD": "throwaway-owner-from-test-setup",
    "BRAIN_APP_PASSWORD": "throwaway-app-from-test-setup",
}


def snapshot() -> list[tuple[object, ...]]:
    """Everything brain.sh decides: the two roles, their passwords and
    attributes, and the database, its owner and its privileges."""
    with admin_connection() as admin:
        roles = admin.execute(
            """
            SELECT rolname, rolpassword, rolsuper, rolinherit, rolcreaterole,
                   rolcreatedb, rolcanlogin, rolreplication, rolbypassrls
            FROM pg_authid WHERE rolname IN ('brain_owner', 'brain_app')
            ORDER BY rolname
            """
        ).fetchall()
        database = admin.execute(
            """
            SELECT datname, pg_get_userbyid(datdba), datacl::text
            FROM pg_database WHERE datname = 'brain'
            """
        ).fetchall()
    return [*roles, *database]


def run_setup_script() -> subprocess.CompletedProcess[str]:
    """As CI runs it, with `psql` on the runner, when there is one. Otherwise
    the way it is run by hand on a machine whose Postgres is the compose
    service: inside the container, over its socket, see infra/README.md."""
    if shutil.which("psql"):
        parts = conninfo_to_dict(admin_url())
        env = {
            "PATH": os.environ["PATH"],
            "PGHOST": str(parts.get("host", "127.0.0.1")),
            "PGPORT": str(parts.get("port", "5432")),
            "PGPASSWORD": str(parts.get("password", "")),
            "POSTGRES_USER": str(parts.get("user", "postgres")),
            "POSTGRES_DB": str(parts.get("dbname", "postgres")),
            **THROWAWAY,
        }
        return subprocess.run(
            ["bash", str(SCRIPT)], env=env, capture_output=True, text=True, check=False
        )
    command = [
        "docker",
        "compose",
        "--project-directory",
        str(ROOT / "infra"),
        "exec",
        "-T",
    ]
    for name, value in THROWAWAY.items():
        command += ["-e", f"{name}={value}"]
    command += ["postgres", "bash", "/docker-entrypoint-initdb.d/brain.sh"]
    return subprocess.run(command, capture_output=True, text=True, check=False)


def test_the_setup_script_can_run_twice() -> None:
    before = snapshot()
    # The first run happened when the volume was created, or by hand, or in
    # CI's setup step. Running it here with throwaway passwords would make
    # roles nobody can log in as, so a missing first run is a failure.
    assert len(before) == 3, (
        f"brain_owner, brain_app and the brain database must exist before this test, "
        f"run {SCRIPT.relative_to(ROOT)} first, see infra/README.md; found {before}"
    )

    second = run_setup_script()

    assert second.returncode == 0, second.stdout + second.stderr
    assert snapshot() == before
