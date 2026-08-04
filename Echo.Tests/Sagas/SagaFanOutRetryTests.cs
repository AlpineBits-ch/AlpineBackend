using Echo.Sagas;
using Messaging;

namespace Echo.Tests.Sagas;

/// <summary>
/// Governs the relationship between how wide a saga fans out and how many times a losing write is
/// retried. These two numbers live in different projects and nothing else connects them.
///
/// <para><b>Why they are connected.</b> The lightweight Postgres saga storage guards every update
/// with <c>where id = @id and version = @version</c>, so when N replies to the same fan-out arrive
/// together the storage serialises them: exactly one writer wins per round, and every other writer
/// throws. The last straggler in an N-way fan-out therefore needs <c>N - 1</c> retries in the worst
/// case. Give it fewer and the tail is dead-lettered - which silently drops an acknowledgement, and
/// a dropped acknowledgement is precisely the failure this whole policy exists to prevent. It would
/// return as a load-dependent bug rather than a constant one, which is strictly worse to
/// diagnose.</para>
///
/// <para>A real export showed the un-retried version of this as a countdown reading
/// <c>7,7,6,5,4,3,2</c> instead of <c>7,6,5,4,3,2,1</c>: two replies had both read eight pending,
/// each removed itself, and only one write survived.</para>
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
    /// 50ms, and collide again. Wolverine's cooldown list carries no jitter, so the only available
    /// defence is that the delays are actually different from one another and spread apart.
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
