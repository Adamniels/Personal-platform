"""Worlds for Dev flow's fake code host, built so a test says what matters to
it and nothing else. A world is the JSON document `PUT /devflow/api/fake/world`
takes, shaped as the code host interface's records, every field written; see
"The code host" in devflow/README.md. The `devflow_world` fixture in
conftest.py stages one in the test's own account.

    world = one_repository()
    add_pull_request(world, 304, labels=["owner-merge"])
    devflow_world(world)
"""

import base64

INSTALLATION = 1
REPOSITORY = 1001
OWNER = "Adamniels"
NAME = "Personal-platform"
WHEN = "2026-11-15T09:30:00+00:00"


def one_repository(owner: str = OWNER, name: str = NAME) -> dict:
    """A world with one installation holding one empty repository."""
    return {
        "installations": [
            {
                "id": INSTALLATION,
                "account": owner,
                "repositories": [
                    {
                        "id": REPOSITORY,
                        "owner": owner,
                        "name": name,
                        "pull_requests": [],
                        "conversations": [],
                        "commits": [],
                        "files": [],
                    }
                ],
            }
        ]
    }


def repository(world: dict) -> dict:
    """The world's one repository, as `one_repository` made it."""
    return world["installations"][0]["repositories"][0]


def add_pull_request(
    world: dict,
    number: int,
    *,
    title: str | None = None,
    author: str = "mac-mini-agent",
    labels: list[str] | None = None,
    head_commit: str | None = None,
    head_branch: str | None = None,
    base_branch: str = "main",
    draft: bool = False,
    body: str = "",
    updated_at: str = WHEN,
    state: str = "open",
    comments: list[dict] | None = None,
    statuses: list[dict] | None = None,
    check_runs: list[dict] | None = None,
) -> dict:
    """Adds a pull request to the world's repository and returns it. Its
    comments become its conversation, and its statuses and check runs are
    posted on its head commit, a made up one unless given."""
    head_commit = head_commit or f"{number:040x}"
    pull_request = {
        "number": number,
        "title": title or f"Pull request {number}",
        "author": author,
        "labels": labels or [],
        "head_commit": head_commit,
        "head_branch": head_branch or f"{number}-branch",
        "base_branch": base_branch,
        "draft": draft,
        "body": body,
        "url": f"https://github.com/{repository(world)['owner']}/"
        f"{repository(world)['name']}/pull/{number}",
        "updated_at": updated_at,
        "state": state,
    }
    repo = repository(world)
    repo["pull_requests"].append(pull_request)
    if comments:
        repo["conversations"].append({"number": number, "comments": comments})
    if statuses or check_runs:
        repo["commits"].append(
            {
                "id": head_commit,
                "statuses": statuses or [],
                "check_runs": check_runs or [],
            }
        )
    return pull_request


def comment(comment_id: int, author: str, body: str, created_at: str = WHEN) -> dict:
    """A conversation comment, never edited."""
    return {
        "id": comment_id,
        "author": author,
        "body": body,
        "created_at": created_at,
        "updated_at": created_at,
    }


def reviewer_comment(
    comment_id: int,
    kind: str,
    sha: str,
    created_at: str = WHEN,
    for_the_owner: list[str] | None = None,
) -> dict:
    """The reviewer's comment on a pull request, by the bot, with kind approve
    or changes for the commit sha, as scripts/agent/verdict.sh reads it, and
    its points for the owner, none unless given. Its marker is put together
    from its parts and never written out whole, since the agents' tooling
    refuses its text, see contracts/README.md."""
    marker = "<!-- " + "verdict" + ": " + kind + " sha=" + sha + " -->"
    points = (
        "\n".join(f"- {point}" for point in for_the_owner) if for_the_owner else "None."
    )
    return comment(
        comment_id,
        "mac-mini-agent",
        f"{marker}\nSlice #272, round 1.\n\n## Blocking\n\nNone.\n\n"
        f"## For the owner\n\n{points}\n\n## Replies\n\nNone.\n",
        created_at,
    )


def add_conversation(world: dict, number: int, comments: list[dict]) -> None:
    """Adds the conversation of an issue, such as a slice the runner comments
    on, to the world's repository."""
    repository(world)["conversations"].append({"number": number, "comments": comments})


def status(context: str, state: str, description: str | None = None) -> dict:
    """A commit status: state is pending, success, failure or error."""
    return {"context": context, "state": state, "description": description}


def check_run(
    name: str,
    conclusion: str | None = "success",
    app: str = "github-actions",
) -> dict:
    """A check run, completed with the conclusion, or in progress without
    one."""
    return {
        "name": name,
        "status": "completed" if conclusion else "in_progress",
        "conclusion": conclusion,
        "app": app,
    }


def add_file(world: dict, path: str, content: bytes, branch: str = "main") -> None:
    """Adds a file on a branch to the world's repository."""
    repository(world)["files"].append(
        {
            "path": path,
            "branch": branch,
            "content": base64.b64encode(content).decode("ascii"),
        }
    )
