// Drops the run's migrated template of Dev flow's database once every test has run, as
// DevFlow.Tests does for its own.
[assembly: AssemblyFixture(typeof(DevFlow.Tests.Templates))]