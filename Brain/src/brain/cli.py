"""`brain`, the package's command, run as `uv run brain <command>` from
Brain/. Today its one command is `migrate`, see Brain/README.md."""

import sys

from brain import migrate

USAGE = "usage: brain migrate"


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if args == ["migrate"]:
        return migrate.main()
    print(USAGE, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
