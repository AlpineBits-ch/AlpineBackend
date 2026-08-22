using Discovery.Domain.Entities;

namespace Discovery.Tests.Entities;

[TestFixture]
public class ListingStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void A_new_listing_is_a_draft_and_has_never_published()
    {
        var listing = Listing.Create("gld_1");
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Draft));
            Assert.That(listing.PublishedAt, Is.Null);
        });
    }

    [Test]
    public void Publishing_stamps_the_first_publish_and_bumps()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Published));
            Assert.That(listing.PublishedAt, Is.EqualTo(T0));
            Assert.That(listing.LastBumpedAt, Is.EqualTo(T0));
        });
    }

    [Test]
    public void Republishing_after_an_unlist_keeps_the_original_publish_date()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        listing.Unlist();
        listing.Publish(T0.AddDays(30));
        Assert.That(listing.PublishedAt, Is.EqualTo(T0));
    }

    [Test]
    public void Bumping_inside_the_cooldown_is_refused()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        Assert.Multiple(() =>
        {
            Assert.That(listing.Bump(T0.AddHours(71)), Is.False);
            Assert.That(listing.Bump(T0.AddHours(73)), Is.True);
        });
    }

    [Test]
    public void Suspension_records_why_and_unlisting_does_not()
    {
        var suspended = Listing.Create("gld_1");
        suspended.Publish(T0);
        suspended.Suspend(SuspensionReason.PlanLapsed);

        var unlisted = Listing.Create("gld_2");
        unlisted.Publish(T0);
        unlisted.Unlist();

        Assert.Multiple(() =>
        {
            Assert.That(suspended.State, Is.EqualTo(ListingState.Suspended));
            Assert.That(suspended.SuspendedReason, Is.EqualTo(SuspensionReason.PlanLapsed));
            Assert.That(unlisted.State, Is.EqualTo(ListingState.Unlisted));
            Assert.That(unlisted.SuspendedReason, Is.Null);
        });
    }

    [Test]
    public void A_draft_cannot_be_suspended()
    {
        var listing = Listing.Create("gld_1");
        listing.Suspend(SuspensionReason.PlanLapsed);
        Assert.That(listing.State, Is.EqualTo(ListingState.Draft));
    }
}
