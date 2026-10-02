# This is just a place for me to write down some personal notes

## Goal:

Regarding AI I will have one goal/rule and that is to ONLY use AI to write things that I could have written myself. If I can't then it is probably something there that I don't 100% understand. I want to grow with this project and really understand everything about it, especially if this is going to be something that I will always have beside me.

## Notes:

- Question 22/9: I would like to really understand how to work with my system, how do i actually close sub-issues if a branch is for a issue above? can a commit close a sub-issue as well? maybe it can?
  answer 22/9: It works like this, I create a branch for the other issue, then in that branch I work through the leaves and by hand moves the issues along the board. When that slice then is done I create a pr which will close the other issue

- To move an item on the board: gh project item-edit 5 --owner Adamniels --url https://github.com/Adamniels/personal-platform/issues/10 --field "Status" --value "In progress"
- To close: gh issue close issue_nr --reason completed --comment "message"

claude --resume 4c2e4409-f07b-4ec5-aea2-7b262d666cde
What #16 is

Your binary gets two modes. Run with no arguments, it serves as it does now. Run as platform_core migrate, it connects as core_owner using a connection string from its own variable that you set in the terminal for that one command. It then applies every SQL file in Core/migrations/ that hasn't been applied yet. Done means: run it twice, the first run applies the pending migrations and the second says there is nothing to do.

What it leaves out: no real migration (#17 writes the first one), no CI (#24), and no running migrations at startup, which the docs rule out because the app role isn't allowed to migrate.

What you need to know first

1. sqlx::migrate!() embeds the migrations at compile time. The macro reads Core/migrations/ while the code compiles and expands to a static Migrator. That value holds each file's version, description, SQL text and a checksum (a hash of the file's contents). This has three consequences:
- The binary carries its own migrations, so it needs no files at runtime.
- If the directory is missing, the build fails. sqlx calls canonicalize on the path, which errors when the path doesn't exist. Until #17, the directory needs a .gitkeep, the same trick canary/ uses. An empty directory compiles fine.
- There's a trap here. Adding a new .sql file without touching any Rust code does not make cargo rebuild, so migrate would say "nothing to do" while a migration sits right there. sqlx's documented fix for stable Rust is a three line build.rs that prints cargo:rerun-if-changed=migrations.

2. The _sqlx_migrations table is the bookkeeping. On its first run the migrator creates this table in core, owned by core_owner. It keeps one row per applied migration: version, description, success, checksum and timing. Each run does the following, which I read in Migrator::run_direct:
1. Takes a Postgres advisory lock, a lock on an agreed number rather than a table, so two migrators can't run at once.
2. Creates the table if it doesn't exist.
3. Refuses to continue if an earlier migration failed partway (Dirty).
4. Errors if an applied version no longer exists as a file (VersionMissing).
5. Errors if an applied file's checksum has changed (VersionMismatch).
6. Applies each pending file in its own transaction and records a row for it.

Steps 4 and 5 put "forward only" into practice. Once a file has been applied you never edit or delete it. You fix things by adding a new migration.

3. run doesn't tell you what it did. Its signature is Result<(), MigrateError>, so on its own it can't print "applied 0001" or "nothing to do". To meet the done condition, your code has to work out what's pending before it calls run.

4. The owner's credentials only go where they're needed. main currently lonnects with CORE_APP_DATABASE_URL. The migrate path must:
- branch off before any of that, because it needs neither the listen address nor the app connection;
- read only the owner variable, and never fall back to the app's.

A long running server uses a pool. For a single command, one PgConnection run accepts either.

5. Numbering. sqlx takes the version from the integer before the first _. rivileges.sql matches "numbered" in the docs.

Proposed order, one step at a time

1. Add "migrate" to sqlx's features in Cargo.toml. This isn't a new dependcs already plan it. The correction commit deliberately left it out untilnow.
2. Add Core/migrations/.gitkeep and Core/build.rs.
3. Restructure main: dispatch on the first argument, move the current body into a serve function, and reject unknown commands with a short usage line.
4. Write the migrate function: read the owner variable, connect, work out eport.
5. Update Core/.env.example with a comment saying the owner variable is deliberately not there, and where it goes instead.
6. Verify by hand, then clean up (see question 3).