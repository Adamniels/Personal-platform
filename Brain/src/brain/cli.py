"""`brain`, the package's command, run as `uv run brain <command>` from
Brain/: `migrate`, see "Migrating", `export` and `import`, see "Export and
import", `purge`, see "Purging", and `eval`, see "Evaluating", in
Brain/README.md."""

import sys
from pathlib import Path
from uuid import UUID

from brain import evaluation, memory_file, migrate, purge

USAGE = """usage: brain migrate
       brain export <account-id> <file>
       brain import <file> [--as <account-id>]
       brain purge <account-id> <item-id>
       brain eval"""


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
        case ["eval"]:
            return evaluation.main()
        case ["purge", account, item]:
            account_id = _account(account)
            item_id = _id(item, "an item id")
            if account_id is None or item_id is None:
                return 2
            return purge.main(account_id, item_id)
    print(USAGE, file=sys.stderr)
    return 2


def _account(text: str) -> UUID | None:
    return _id(text, "an account id")


def _id(text: str, what: str) -> UUID | None:
    try:
        return UUID(text)
    except ValueError:
        print(f"{text!r} is not {what}\n{USAGE}", file=sys.stderr)
        return None


if __name__ == "__main__":
    sys.exit(main())
