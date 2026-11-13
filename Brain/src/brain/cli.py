"""`brain`, the package's command, run as `uv run brain <command>` from
Brain/: `migrate`, see "Migrating", and `export` and `import`, see "Export
and import", in Brain/README.md."""

import sys
from pathlib import Path
from uuid import UUID

from brain import memory_file, migrate

USAGE = """usage: brain migrate
       brain export <account-id> <file>
       brain import <file> [--as <account-id>]"""


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    match args:
        case ["migrate"]:
            return migrate.main()
        case ["export", account, file]:
            account_id = _account(account)
            if account_id is None:
                return 2
            return memory_file.export_main(account_id, Path(file))
        case ["import", file]:
            return memory_file.import_main(Path(file), None)
        case ["import", file, "--as", account]:
            account_id = _account(account)
            if account_id is None:
                return 2
            return memory_file.import_main(Path(file), account_id)
    print(USAGE, file=sys.stderr)
    return 2


def _account(text: str) -> UUID | None:
    try:
        return UUID(text)
    except ValueError:
        print(f"{text!r} is not an account id\n{USAGE}", file=sys.stderr)
        return None


if __name__ == "__main__":
    sys.exit(main())
