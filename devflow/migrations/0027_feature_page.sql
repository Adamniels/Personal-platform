-- What a feature's page reads that was not stored yet: the commit each watched branch was at after
-- the newest change its watch judged, and the address of a feature's pull request beside its
-- number. The page is "A feature's page" in devflow/README.md, and why it reads only what Dev flow
-- stored is "A feature's page" in devflow/docs/devflow-architecture.md.

-- The commit the branch was at after the newest change judged, fine or not, as the code host named
-- it; for a feature's branch, the commit it was made at until a change is judged. Null for a watch
-- that began before this column, until its next judged change.
ALTER TABLE branch_watches ADD COLUMN last_commit text CHECK (last_commit ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');

-- The feature's pull request's address, beside its number, as the code host answered it when it
-- was opened; both or neither, as a card's are.
ALTER TABLE features ADD COLUMN pull_request_url text CHECK (pull_request_url <> '');
ALTER TABLE features ADD CHECK ((pull_request_number IS NULL) = (pull_request_url IS NULL));
