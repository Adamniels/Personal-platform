"""A database of its own, made and dropped by the Postgres superuser and set
up as `infra/postgres/initdb/brain.sh` sets up `brain`: what the tests, see
`tests/harness.py`, and the evaluation, `brain eval`, run on. Only they hold
the superuser's connection string, `BRAIN_TEST_ADMIN_DATABASE_URL`; the
running brain never does."""

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo


def admin_connection(
    admin_url: str, database: str = "postgres"
) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection, in autocommit, since CREATE and DROP DATABASE
    cannot run inside a transaction."""
    return psycopg.connect(make_conninfo(admin_url, dbname=database), autocommit=True)


def owner_connection(
    admin_url: str, database: str
) -> psycopg.Connection[tuple[object, ...]]:
    """A superuser connection acting as brain_owner, as `brain migrate` runs,
    so what it creates is the owner's and the owner's default privileges
    apply."""
    conn = admin_connection(admin_url, database)
    conn.execute("SET ROLE brain_owner")
    return conn


def as_app(admin_url: str, database: str) -> str:
    """A connection string for `database` that acts as brain_app from its
    first statement: the app role's privileges and row level security,
    exactly as the running brain has them, without its password."""
    return make_conninfo(admin_url, dbname=database, options="-c role=brain_app")


def create(admin_url: str, name: str, template: str | None = None) -> None:
    """Creates `name`, owned by brain_owner, empty or a copy of `template`,
    with the database privileges brain.sh gives `brain`."""
    with admin_connection(admin_url) as admin:
        if template is None:
            admin.execute(
                sql.SQL("CREATE DATABASE {} OWNER brain_owner").format(
                    sql.Identifier(name)
                )
            )
        else:
            admin.execute(
                sql.SQL("CREATE DATABASE {} TEMPLATE {} OWNER brain_owner").format(
                    sql.Identifier(name), sql.Identifier(template)
                )
            )
        # Database privileges belong to the database, not its contents, so a
        # copy does not inherit the template's. The same two lines as brain.sh.
        admin.execute(
            sql.SQL("REVOKE ALL ON DATABASE {} FROM PUBLIC").format(
                sql.Identifier(name)
            )
        )
        admin.execute(
            sql.SQL("GRANT CONNECT ON DATABASE {} TO brain_app").format(
                sql.Identifier(name)
            )
        )


def drop(admin_url: str, name: str) -> None:
    with admin_connection(admin_url) as admin:
        admin.execute(
            sql.SQL("DROP DATABASE IF EXISTS {} WITH (FORCE)").format(
                sql.Identifier(name)
            )
        )
