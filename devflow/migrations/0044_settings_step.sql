-- The settings step of setting a project up: the owner accepts the settings a setup run proposed,
-- or saves the settings panel, once, and the project hands out no reading or build until he has.
-- accepted_at is when he first did; null until then. Every project there was, those registered
-- among them, starts with it null, so the owner confirms each one's settings once. How it is shown
-- and read is "Setting up a project" in devflow/README.md, and why "Setting up a project" in
-- devflow/docs/devflow-architecture.md.
ALTER TABLE project_settings ADD COLUMN accepted_at timestamptz;
