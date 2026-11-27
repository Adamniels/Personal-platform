-- Whether a project's wait after failures outside its cards lasts until Claude's usage limit
-- resets, as the last failure's answer said, rather than for the doubling wait. The rule that
-- decides what runs tells the two apart: "At Claude's usage limit until <time>" for this one, "Dev
-- flow cannot run its cards" otherwise. See "Jobs" and "Stops" in devflow/README.md.
ALTER TABLE project_waits ADD COLUMN until_reset boolean NOT NULL DEFAULT false;
