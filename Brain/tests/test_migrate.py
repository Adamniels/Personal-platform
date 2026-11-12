"""`brain migrate`, against a real Postgres, each test on a database of its own
with no migration applied."""

import os
import subprocess
import sys
import threading
import time
from pathlib import Path

import psycopg
import pytest
from psycopg.conninfo import make_conninfo

from brain import migrate
from tests.harness import admin_connection, admin_url, owner_connection

BRAIN_COMMAND = Path(sys.executable).parent / "brain"


def write(directory: Path, files: dict[str, str]) -> Path:
    directory.mkdir(exist_ok=True)
    for name, text in files.items():
        (directory / name).write_text(text)
    return directory


def ledger(database: str) -> list[tuple[object, ...]]:
    with admin_connection(database) as conn:
        return conn.execute(
            "SELECT version, name FROM schema_migrations ORDER BY version"
        ).fetchall()


def exists(database: str, table: str) -> bool:
    with admin_connection(database) as conn:
        row = conn.execute("SELECT to_regclass(%s) IS NOT NULL", (table,)).fetchone()
    assert row is not None
    return bool(row[0])


# Each file appends its own number, so the log shows the order they ran in.
# Written out of order, and with a gap, so neither the directory's order nor
# a count decides it.
ORDERED = {
    "0010_third.sql": "INSERT INTO log (n) VALUES (3);",
    "0001_create_log.sql": """
        CREATE TABLE log (seq int GENERATED ALWAYS AS IDENTITY, n int NOT NULL);
        INSERT INTO log (n) VALUES (1);
    """,
    "0002_second.sql": "INSERT INTO log (n) VALUES (2);",
}


def test_migrate_applies_each_file_once_in_order(
    empty_database: str, tmp_path: Path
) -> None:
    directory = write(tmp_path / "migrations", ORDERED)

    with owner_connection(empty_database) as conn:
        first = migrate.run(conn, directory)
    with owner_connection(empty_database) as conn:
        second = migrate.run(conn, directory)

    assert [(m.version, m.name) for m in first] == [
        (1, "create_log"),
        (2, "second"),
        (10, "third"),
    ]
    assert second == []
    assert ledger(empty_database) == [(1, "create_log"), (2, "second"), (10, "third")]
    with admin_connection(empty_database) as conn:
        assert conn.execute("SELECT n FROM log ORDER BY seq").fetchall() == [
            (1,),
            (2,),
            (3,),
        ]
        # Applied by the owner, so the owner's default privileges reach what
        # the files create.
        row = conn.execute(
            "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'log'::regclass"
        ).fetchone()
    assert row == ("brain_owner",)


def test_migrate_stops_at_a_failing_file_and_records_nothing_for_it(
    empty_database: str, tmp_path: Path
) -> None:
    directory = write(
        tmp_path / "migrations",
        {
            "0001_first.sql": "CREATE TABLE first (x int);",
            # Fails after creating a table, which must not survive it.
            "0002_fails.sql": "CREATE TABLE half (x int); SELECT 1 / 0;",
            "0003_later.sql": "CREATE TABLE later (x int);",
        },
    )

    with (
        owner_connection(empty_database) as conn,
        pytest.raises(migrate.MigrationError, match="0002_fails.sql"),
    ):
        migrate.run(conn, directory)

    assert ledger(empty_database) == [(1, "first")]
    assert exists(empty_database, "first")
    assert not exists(empty_database, "half")
    assert not exists(empty_database, "later")


@pytest.mark.parametrize(
    "files",
    [
        {"1_short.sql": "SELECT 1;"},
        {"0001_Capital.sql": "SELECT 1;"},
        {"0001-hyphen.sql": "SELECT 1;"},
        {"0001_a.sql": "SELECT 1;", "0001_b.sql": "SELECT 1;"},
    ],
    ids=["short version", "capital", "hyphen", "duplicate version"],
)
def test_migrate_refuses_files_it_cannot_order(
    empty_database: str, tmp_path: Path, files: dict[str, str]
) -> None:
    directory = write(tmp_path / "migrations", files)

    with (
        owner_connection(empty_database) as conn,
        pytest.raises(migrate.MigrationError),
    ):
        migrate.run(conn, directory)

    assert (
        not exists(empty_database, "schema_migrations") or ledger(empty_database) == []
    )


def test_migrate_refuses_when_the_ledger_and_the_files_disagree(
    empty_database: str, tmp_path: Path
) -> None:
    directory = write(
        tmp_path / "migrations",
        {"0001_first.sql": "SELECT 1;", "0002_second.sql": "SELECT 1;"},
    )
    with owner_connection(empty_database) as conn:
        migrate.run(conn, directory)

    # An applied file renamed: the history no longer says what ran.
    (directory / "0002_second.sql").rename(directory / "0002_renamed.sql")
    with (
        owner_connection(empty_database) as conn,
        pytest.raises(migrate.MigrationError, match="0002"),
    ):
        migrate.run(conn, directory)

    # An applied file removed.
    (directory / "0002_renamed.sql").unlink()
    with (
        owner_connection(empty_database) as conn,
        pytest.raises(migrate.MigrationError, match="0002"),
    ):
        migrate.run(conn, directory)

    # A new file numbered below one already applied would run out of order.
    write(directory, {"0002_second.sql": "SELECT 1;", "0003_third.sql": "SELECT 1;"})
    with owner_connection(empty_database) as conn:
        migrate.run(conn, directory)
    write(directory, {"0000_too_late.sql": "CREATE TABLE too_late (x int);"})
    with (
        owner_connection(empty_database) as conn,
        pytest.raises(migrate.MigrationError, match="0000"),
    ):
        migrate.run(conn, directory)

    assert not exists(empty_database, "too_late")
    assert ledger(empty_database) == [(1, "first"), (2, "second"), (3, "third")]


def test_migrate_waits_while_another_run_holds_the_lock(
    empty_database: str, tmp_path: Path
) -> None:
    directory = write(tmp_path / "migrations", ORDERED)
    errors: list[migrate.MigrationError | psycopg.Error] = []

    def second_run() -> None:
        try:
            with owner_connection(empty_database) as conn:
                migrate.run(conn, directory)
        except (migrate.MigrationError, psycopg.Error) as err:  # reported below
            errors.append(err)

    with admin_connection(empty_database) as holder:
        holder.execute("SELECT pg_advisory_lock(%s)", (migrate.LOCK_KEY,))
        runner = threading.Thread(target=second_run)
        runner.start()
        until_one_waits_on_an_advisory_lock(empty_database)

        # Still waiting, so nothing has been touched.
        assert not exists(empty_database, "log")
        holder.execute("SELECT pg_advisory_unlock(%s)", (migrate.LOCK_KEY,))

    runner.join(timeout=30)
    assert not runner.is_alive()
    assert errors == []
    assert ledger(empty_database) == [(1, "create_log"), (2, "second"), (10, "third")]


def until_one_waits_on_an_advisory_lock(database: str) -> None:
    """Returns once a connection to `database` waits on an advisory lock, so
    the test knows the run it started has reached the lock. Fails after
    about five seconds."""
    with admin_connection(database) as conn:
        for _ in range(250):
            row = conn.execute(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND wait_event = 'advisory'
                """
            ).fetchone()
            if row and row[0]:
                return
            time.sleep(0.02)
    pytest.fail("the second run never waited on the migration lock")


def owner_url(database: str) -> str:
    """The superuser's string, acting as brain_owner for the whole session,
    since tests do not hold the owner's own password."""
    return make_conninfo(admin_url(), dbname=database, options="-c role=brain_owner")


def run_command(environ: dict[str, str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(BRAIN_COMMAND), "migrate"],
        env=environ,
        capture_output=True,
        text=True,
        check=False,
    )


def test_the_command_migrates_from_brain_migrations_and_says_what_it_did(
    empty_database: str,
) -> None:
    environ = {**os.environ, "BRAIN_OWNER_DATABASE_URL": owner_url(empty_database)}

    first = run_command(environ)
    second = run_command(environ)

    assert first.returncode == 0, first.stderr
    assert first.stdout.splitlines()[0] == "applied 1 default_privileges"
    assert second.returncode == 0, second.stderr
    assert second.stdout == "nothing to do, every migration is applied\n"


def test_the_command_refuses_without_the_owners_string_and_never_prints_it(
    empty_database: str,
) -> None:
    environ = {k: v for k, v in os.environ.items() if k != "BRAIN_OWNER_DATABASE_URL"}
    unset = run_command(environ)

    assert unset.returncode != 0
    assert "BRAIN_OWNER_DATABASE_URL is not set" in unset.stderr

    wrong = make_conninfo(owner_url(empty_database), password="not-the-password-a1b2c3")
    refused = run_command({**environ, "BRAIN_OWNER_DATABASE_URL": wrong})

    assert refused.returncode != 0
    assert "could not connect" in refused.stderr
    assert "not-the-password-a1b2c3" not in refused.stdout + refused.stderr
