-- A card's pull request, and the check each run was given. How Dev flow opens a card's pull
-- request and follows it is "The pull request Dev flow opens" in devflow/README.md, and why it is
-- built so "How a card reaches its pull request" in devflow/docs/devflow-architecture.md.

-- The pull request a card's built run opened, or found already open for its branch: its number and
-- where a person opens it. Null until one is opened; both or neither.
ALTER TABLE cards ADD COLUMN pull_request_number int CHECK (pull_request_number > 0);
ALTER TABLE cards ADD COLUMN pull_request_url text CHECK (pull_request_url <> '');
ALTER TABLE cards ADD CHECK ((pull_request_number IS NULL) = (pull_request_url IS NULL));

-- The check the run's job carried, from .devflow/project.json on the default branch as it was
-- handed out, which the pull request and the card's page say Dev flow ran. Null for a run handed
-- out before this column.
ALTER TABLE runs ADD COLUMN check_command text;
