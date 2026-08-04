using Messaging;
using Wolverine;

namespace Echo.Tests.Sagas;

/// <summary>
/// The fan-out sagas rely on Wolverine retrying a saga write that lost an optimistic-concurrency
/// race, and nothing else in the codebase makes that dependency visible.
///
/// <para>The lightweight Postgres saga storage updates with
/// <c>set version = @version + 1 where id = @id and version = @version</c> and throws
/// <see cref="SagaConcurrencyException"/> when that matches no row. Eight services answering a
/// fan-out at once means that race is the normal case, not an edge one. Before this policy existed
/// nothing retried the throw, so the losing acknowledgement was discarded: a real export logged a
/// countdown of 7,7,6,5,4,3,2 instead of 7,6,5,4,3,2,1, could never reach zero, and resolved as
/// <c>Partial</c> naming a service that had actually answered - with that service's fragment lost.</para>
///
/// <para>This asserts the policy is registered rather than that it works, which the database
/// enforces. It exists so that deleting those four lines fails a test instead of quietly
/// reintroducing dropped acknowledgements under load.</para>
/// </summary>
[TestFixture]
public class SagaConcurrencyPolicyTests
{
    [Test]
    public void ConfigureWolverine_RegistersARetryPolicyForSagaConcurrencyViolations()
    {
        var configured = new WolverineOptions().ConfigureWolverine(useEfCore: false);

        Assert.That(DescribeFailureRules(configured), Does.Contain(nameof(SagaConcurrencyException)),
            "a saga write that loses the optimistic-concurrency race must be retried against fresh "
            + "state; without a rule for it the acknowledgement is dropped and the fan-out can "
            + "never complete");
    }

    /// <summary>A bare options object must NOT already satisfy the assertion above, or the test
    /// would pass whatever ConfigureWolverine did.</summary>
    [Test]
    public void ABareWolverineOptions_HasNoSuchPolicy()
    {
        Assert.That(DescribeFailureRules(new WolverineOptions()),
            Does.Not.Contain(nameof(SagaConcurrencyException)));
    }

    /// <summary>
    /// Reflects over the failure rules rather than binding to Wolverine's rule-matching API, which
    /// is internal enough that a version bump would break the test for reasons unrelated to the
    /// behaviour being guarded. The type name appearing anywhere in the rule graph is a weaker
    /// signal than invoking the matcher, but it is a stable one.
    /// </summary>
    private static string DescribeFailureRules(WolverineOptions options)
    {
        var failures = options.Policies.Failures;
        var described = new List<string>();

        foreach (var rule in failures)
        {
            described.Add(rule.ToString() ?? string.Empty);

            // The match predicate carries the exception type; ToString on the rule does not always
            // surface it, so walk the rule's own properties for anything that names a type.
            foreach (var property in rule.GetType().GetProperties())
            {
                object? value;
                try
                {
                    value = property.GetValue(rule);
                }
                catch
                {
                    continue;
                }

                if (value is null) continue;
                described.Add(value.ToString() ?? string.Empty);
                described.Add(value.GetType().FullName ?? string.Empty);

                foreach (var generic in value.GetType().GetGenericArguments())
                {
                    described.Add(generic.FullName ?? string.Empty);
                }
            }
        }

        return string.Join(" | ", described);
    }
}
