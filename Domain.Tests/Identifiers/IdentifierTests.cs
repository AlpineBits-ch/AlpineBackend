using Ids;

// Namespace deliberately not "Domain.Tests.Ids": that would shadow the Ids namespace the type under
// test lives in, and resolution would depend on where the usings sit relative to the declaration.
namespace Domain.Tests.Identifiers;

/// <summary>
/// Covers <see cref="Identifier"/>, the single generator every id in the system comes from.
/// </summary>
[TestFixture]
public class IdentifierTests
{
    /// <summary>Crockford's base32 alphabet: digits, then A-Z less I, L, O and U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string BodyOf(string id) => id[(id.IndexOf(Identifier.Separator) + 1)..];

    // ══════════════════════════════════════════════════════════════════════════ Shape
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void New_ProducesPrefixSeparatorAndTwentySixCharacterBody()
    {
        var id = Identifier.New("mesg");

        Assert.Multiple(() =>
        {
            Assert.That(id, Does.StartWith("mesg_"));
            Assert.That(BodyOf(id), Has.Length.EqualTo(Identifier.BodyLength));
            Assert.That(id, Has.Length.EqualTo("mesg".Length + 1 + Identifier.BodyLength));
        });
    }

    [Test]
    public void New_BodyUsesOnlyTheCrockfordAlphabet()
    {
        foreach (var c in BodyOf(Identifier.New("mesg")))
        {
            Assert.That(Alphabet, Does.Contain(c), $"'{c}' is not a Crockford base32 symbol");
        }
    }

    /// <summary>Separate from the alphabet check because uppercase is the property callers rely on,
    /// so it should fail by name.</summary>
    [Test]
    public void New_BodyContainsNoLowercase()
    {
        var body = BodyOf(Identifier.New("mesg"));

        Assert.That(body, Is.EqualTo(body.ToUpperInvariant()));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Sortability - the reason this change was made
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Ids_sort_lexicographically_by_mint_time()
    {
        // Two milliseconds apart, because ordering is exact only to the millisecond by design -
        // ids minted inside one millisecond are deliberately unordered relative to each other.
        var minted = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            minted.Add(Identifier.New("mesg"));
            Thread.Sleep(2);
        }

        // Ordinal, not culture-aware: this is exactly the comparison a Postgres index and a Scylla
        // clustering key perform.
        var sorted = minted.OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.That(sorted, Is.EqualTo(minted).AsCollection,
            "ids must sort by mint time under ordinal comparison");
    }

    [Test]
    public void Ids_with_different_prefixes_still_sort_by_time_within_a_prefix()
    {
        var first = Identifier.New("chan");
        Thread.Sleep(2);
        var second = Identifier.New("chan");

        Assert.That(string.CompareOrdinal(first, second), Is.LessThan(0));
    }

    // ══════════════════════════════════════════════════════════════════════════ Prefix
    // normalisation ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void New_AcceptsAPrefixWithOrWithoutTheSeparator()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Identifier.New("user"), Does.StartWith("user_"));
            Assert.That(Identifier.New("user_"), Does.StartWith("user_"));
            Assert.That(Identifier.New("user_"), Has.Length.EqualTo(Identifier.New("user").Length));
        });
    }

    [Test]
    public void New_DoesNotDoubleTheSeparator()
    {
        var id = Identifier.New("user_");

        Assert.That(id, Does.Not.Contain("__"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Failing gracefully
    // ══════════════════════════════════════════════════════════════════════════

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("_")]
    public void New_RejectsAPrefixThatWouldProduceAnUntaggedId(string prefix)
    {
        Assert.That(() => Identifier.New(prefix), Throws.ArgumentException);
    }

    [Test]
    public void New_RejectsNull()
    {
        Assert.That(() => Identifier.New(null!), Throws.ArgumentException);
    }

    [Test]
    public void NewUnprefixed_ReturnsABareBodyWithNoSeparator()
    {
        var id = Identifier.NewUnprefixed();

        Assert.Multiple(() =>
        {
            Assert.That(id, Has.Length.EqualTo(Identifier.BodyLength));
            Assert.That(id, Does.Not.Contain(Identifier.Separator.ToString()));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Uniqueness
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void New_ProducesUniqueIdsUnderTightLooping()
    {
        // All within a handful of milliseconds, so this exercises the 80 random bits rather than the
        // timestamp - the case where a weak generator would start repeating.
        const int count = 50_000;

        var ids = new HashSet<string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            ids.Add(Identifier.New("mesg"));
        }

        Assert.That(ids, Has.Count.EqualTo(count));
    }

    [Test]
    public void New_ProducesUniqueIdsAcrossThreads()
    {
        // The generator is called concurrently by every request-handling thread in every service, so
        // shared mutable state inside it would show up here and nowhere else.
        const int perThread = 5_000;
        const int threads = 8;

        var results = new string[threads][];
        Parallel.For(0, threads, t =>
        {
            var local = new string[perThread];
            for (var i = 0; i < perThread; i++) local[i] = Identifier.New("mesg");
            results[t] = local;
        });

        var all = results.SelectMany(x => x).ToList();

        Assert.That(all.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(threads * perThread));
    }
}
