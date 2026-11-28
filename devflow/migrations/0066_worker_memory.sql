-- Memory per worker: how much memory each of a project's workers is given, in whole gigabytes, its
-- setup worker's too, which the machine program starts it with in a later slice. A setting of the
-- project, answered by the settings route like the others, and saved from the Workers card through
-- its own route, since it is about where the project runs; the settings page never changes it. See
-- "Workers" in devflow/README.md.

-- Every project made before this has the 2 GB each worker was started with until now. The check
-- repeats the route's range, in src/DevFlow/Projects/ProjectSettings.cs, for a row written around
-- the route.
ALTER TABLE project_settings
    ADD COLUMN worker_memory_gb int NOT NULL DEFAULT 2 CHECK (worker_memory_gb BETWEEN 1 AND 16);
