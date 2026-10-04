# This is just a place for me to write down some personal notes

## Goal:

Regarding AI I will have one goal/rule and that is to ONLY use AI to write things that I could have written myself. If I can't then it is probably something there that I don't 100% understand. I want to grow with this project and really understand everything about it, especially if this is going to be something that I will always have beside me.

## Notes:

- Question 22/9: I would like to really understand how to work with my system, how do i actually close sub-issues if a branch is for a issue above? can a commit close a sub-issue as well? maybe it can?
  answer 22/9: It works like this, I create a branch for the other issue, then in that branch I work through the leaves and by hand moves the issues along the board. When that slice then is done I create a pr which will close the other issue

- To move an item on the board: gh project item-edit 5 --owner Adamniels --url https://github.com/Adamniels/personal-platform/issues/10 --field "Status" --value "In progress"
- To close: gh issue close issue_nr --reason completed --comment "message"


## prompt for starting on ticket 22
I'm picking up #22, "Trust Caddy's local root on the laptop", the last leaf of slice #19 on
branch 5-walking-skeleton. Read #22, #19 and the docs it points at, and infra/README.md, before
anything else.

This is the first time I'm doing this kind of task, so teach it as a lesson: one step at a time,
concept first, then the exact commands for this repo, and wait for me between steps. Only ask
check questions about things you have actually taught. Teach the foundation before any question
that depends on it, and make questions concrete scenarios with clear wording.

Where things stand:
- #20 is done: platform.test maps to 127.0.0.1 in /etc/hosts, documented in infra/README.md.
- #21 is done: Caddy (caddy:2) runs in infra/compose.yaml, ports 80 and 443 on 127.0.0.1 only,
  with tls internal and skip_install_trust in infra/Caddyfile. The upstreams are
  host.docker.internal:8080 (core) and :8081 (canary), tested to work on Docker Desktop.
  Volumes are caddy_data (/data) and caddy_config (/config), project name personal-platform.
- Caddy's log reports the root at storage:pki/authorities/local/root.crt, which is
  /data/caddy/pki/authorities/local/root.crt in the container. The chain is
  "Caddy Local Authority - 2026 ECC Root" -> "Caddy Local Authority - ECC Intermediate" -> leaf
  (empty subject, platfur lifetime). Today
  `openssl s_client -coervername platform.test` gives   Verify return code 20rm.test/` fails without -k. - I already understand e root is trusted rather than the leaf, that
  private keys rather than certificates are the secret, and that docker compose down -v makes
  a new root, so the old one must be re-trusted and the stale one removed
from the keychain.

What Session 4 should cover: the login vs System keychain and which to choose (recommend one,
with reasons), `securitat its trust settingsmean, why `caddy trust`
inside the container does nothing for the Mac, getting the root out with `docker compose cp`,
which browsers use the 't by default), how toremove the root later,
and verifying with curl without -k, openssl returning 0 (ok), and the
browser padlock on both
routes.

Done when the browser s and the steps are ininfra/README.md. Then the
docs/README.md Infra entry gains "and trusting its root", and the
Caddyfile comment's
"see README.md" becomes true. Commits follow my convention: subject, short body, "Refs #22". The
close comment is "Done on completed. I commitand close myself, so
suggest the message ander #22, the #19 sliceand walking skeleton
done conditions 1 and 2 to end before the PR.