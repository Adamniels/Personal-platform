-- The version of each project's recipe, its .devflow/Dockerfile, as Dev flow last read it: the id of
-- the newest commit of the repository's default branch that changed the file. The recipe route
-- writes it each time it hands the recipe out, and the loop each time it reads the project, so a
-- worker built from an older recipe is told apart and given no work. Null until it has been read,
-- and again when the default branch has no recipe. See "Workers" in devflow/README.md.
ALTER TABLE projects ADD COLUMN recipe_version text
    CHECK (recipe_version ~ '^([0-9a-f]{40}|[0-9a-f]{64})$');
