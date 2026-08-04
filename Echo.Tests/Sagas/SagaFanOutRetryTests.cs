using Echo.Sagas;
using Messaging;

namespace Echo.Tests.Sagas;

/// <summary>
/// Governs the relationship between how wide a saga fans out and how many times a losing write is
/// retried.
/// </summary>
[TestFixture]
public class SagaFanOutRetryTests
{
    private static IEnumerable<TestCaseData> FanOuts()
    {
        yield return new TestCaseData(
            nameof(AccountDeletionSaga), AccountDeletionSaga.ParticipatingServices.Length);
        yield return new TestCaseData(
            nameof(ExportUserDataSaga), ExportUserDataSaga.ParticipatingServices.Length);
    }

    [TestCaseSource(nameof(FanOuts))]
    public void TheRetryBudgetCoversTheWorstCaseContentionForEveryFanOut(string saga, int participants)
    {
        // N-1: the first round is the original delivery, not a retry, and one writer wins in every
        // round including that one.
        var worstCaseRetriesNeeded = participants - 1;

        Assert.That(Messaging.Messaging.SagaConcurrencyRetryDelays, Has.Length.GreaterThanOrEqualTo(worstCaseRetriesNeeded),
            $"{saga} fans out to {participants} services, so its last reply may lose "
            + $"{worstCaseRetriesNeeded} races before it wins. With fewer cooldowns than that, the "
            + "tail of a simultaneous fan-out is dead-lettered and its acknowledgement - and for an "
            + "export, its fragment - is lost. Add cooldowns, or narrow the fan-out.");
    }

    /// <summary>
    /// Identical delays would keep a contending set in lockstep: they collide, all wait the same
    /// 50ms, and collide again.
    /// </summary>
    [Test]
    public void TheCooldownsAreStrictlyIncreasing_SoContendersDoNotRetryInLockstep()
    {
        var delays = Messaging.Messaging.SagaConcurrencyRetryDelays;

        Assert.Multiple(() =>
        {
            for (var i = 1; i < delays.Length; i++)
            {
                Assert.That(delays[i], Is.GreaterThan(delays[i - 1]),
                    $"cooldown {i} ({delays[i]}) must exceed cooldown {i - 1} ({delays[i - 1]}); "
                    + "a flat or repeating schedule makes every loser retry at the same instant "
                    + "and collide again");
            }
        });
    }

    /// <summary>
    /// The retries must all fit inside the saga deadline, or a saga could be resolved as incomplete
    /// while a reply of its own is still legitimately backing off - which would report a service as
    /// missing when it had answered and was merely queued behind a race.
    /// </summary>
    [Test]
    public void TheWholeRetrySchedule_FitsComfortablyInsideBothSagaDeadlines()
    {
        var total = Messaging.Messaging.SagaConcurrencyRetryDelays
            .Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);

        Assert.Multiple(() =>
        {
            Assert.That(total, Is.LessThan(AppEnvironment.Env.SagaDeadlines.AccountPurge),
                "a reply still retrying when the purge deadline fires would be counted as absent");
            Assert.That(total, Is.LessThan(AppEnvironment.Env.SagaDeadlines.DataExport),
                "a reply still retrying when the export deadline fires would have its fragment "
                + "replaced by a MissingFragment naming a service that did in fact answer");
        });
    }
}
