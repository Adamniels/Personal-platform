using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PlatformData.Tests;

/// <summary>
/// The session advisory lock <c>migrate</c> takes, so two runs for one service cannot overlap.
/// That a held lock does keep a second run out is tested against a real Postgres in review's
/// <c>MigrateTests.TwoRunsCannotOverlap</c>.
/// </summary>
public sealed class LockKeyTests
{
    [Fact]
    public void TheKeyIsTheFirstEightBytesOfTheNamesHash()
    {
        // The value review's own migrator held before it moved here, written out, so a review
        // migration and one run by an older build of review still exclude each other.
        Assert.Equal(4863386983409076113L, Migrator.LockKey("review"));

        foreach (var service in new[] { "review", "dev_flow", "a" })
        {
            Assert.Equal(
                BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{service}.migrate"))),
                Migrator.LockKey(service));
        }

        // Each service's key is its own, so one service's run never waits for another's.
        Assert.NotEqual(Migrator.LockKey("review"), Migrator.LockKey("dev_flow"));
    }
}