using Guild.Domain.Entity;

namespace Guild.Domain.Services;

/// <summary>
/// The wiki a new household starts with: one "House manual" category holding the six pages every
/// shared flat needs and nobody ever writes.
/// </summary>
public static class HouseManualSeed
{
    public const string CategoryName = "House manual";

    /// <summary>The rows a seeded house manual consists of.</summary>
    public sealed record HouseManual(Wiki Wiki, WikiCategory Category, IReadOnlyList<WikiPage> Pages)
    {
        public IReadOnlyList<object> Rows => [Wiki, Category, .. Pages];
    }

    /// <summary>Builds the starter manual for a freshly created household.</summary>
    /// <param name="ownerId">Authors every page.</param>
    public static HouseManual ForHousehold(string guildId, string ownerId)
    {
        var wiki = Wiki.Create(guildId);

        var category = WikiCategory.Create(new CreateWikiCategoryParams
        {
            GuildId = guildId,
            Name = CategoryName,
            Position = 0,
        });

        var pages = new List<WikiPage>();

        void Add(string title, string content, bool pinned = false) =>
            pages.Add(WikiPage.Create(new CreateWikiPageParams
            {
                GuildId = guildId,
                Title = title,
                Content = content.Trim(),
                AuthorId = ownerId,
                CategoryId = category.Id,
                IsPinned = pinned,
            }));

        Add("Wifi and devices", WifiAndDevices);
        Add("Bin day and recycling", BinDayAndRecycling);
        Add("Appliances and the boiler", AppliancesAndTheBoiler);
        Add("Landlord and emergencies", LandlordAndEmergencies);
        Add("Meter readings", MeterReadings);

        // Pinned, because it is the page a new arrival should land on and the only one that points
        // at the rest of the modules. The other five are reference; this one is the map.
        Add("How this house works", HowThisHouseWorks, pinned: true);

        return new HouseManual(wiki, category, pages);
    }

    /// <summary>The titles, in the order they are seeded.</summary>
    public static readonly IReadOnlyList<string> PageTitles =
    [
        "Wifi and devices",
        "Bin day and recycling",
        "Appliances and the boiler",
        "Landlord and emergencies",
        "Meter readings",
        "How this house works",
    ];

    // ── Page bodies ──────────────────────────────────────────────────────────

    private const string WifiAndDevices = """
# Wifi and devices

Fill this in once and nobody has to ask again.

- **Network name:** [ ]
- **Password:** [ ]
- **Guest network, if there is one:** [ ]
- **Where the router lives:** [ ]
- **How to restart it when the internet dies:** [ ]

## Other shared devices

| What | Where it is | How to get on it |
| --- | --- | --- |
| [ printer, speaker, TV ... ] | [ ] | [ ] |

## Who to call about the internet

- **Provider:** [ ]
- **Account or customer number:** [ ]
- **Support number:** [ ]
""";

    private const string BinDayAndRecycling = """
# Bin day and recycling

- **General waste goes out:** [ which night ]
- **Recycling goes out:** [ which night ]
- **Whose turn it is:** [ or point at the chore rota ]

## Where things go

| What | Where | Notes |
| --- | --- | --- |
| Paper and card | [ ] | [ ] |
| Glass | [ ] | [ ] |
| Cans and plastic | [ ] | [ ] |
| Food waste | [ ] | [ ] |
| Batteries and electricals | [ ] | [ ] |

## The awkward ones

Anything the council will not take with the normal collection - bulky items, garden waste, paint.

- [ what it is, and where it actually has to go ]
""";

    private const string AppliancesAndTheBoiler = """
# Appliances and the boiler

The details live in the upkeep channel, one entry per appliance, with the service dates and the
warranty. This page is for the things that are not a date.

## The boiler

- **Where it is:** [ ]
- **How to reset it:** [ ]
- **How to top up the pressure:** [ ]
- **Serviced by:** [ ]

## Washing machine and dryer

- **Anything that has to be done a particular way:** [ ]
- **What not to put in it:** [ ]

## Oven, hob and extractor

- **Quirks worth knowing:** [ ]

## Anything that is broken right now

Mark it broken in the upkeep channel rather than here, so everyone gets told and it stops being
something you have to remember to write down.
""";

    private const string LandlordAndEmergencies = """
# Landlord and emergencies

The page somebody opens at 22:00 on a Sunday. Keep it short and keep it true.

## Where the shut-offs are

- **Water stopcock:** [ ]
- **Fuse box / consumer unit:** [ ]
- **Gas shut-off valve:** [ ]
- **Main water meter:** [ ]

## Who to call

| For | Who | Number |
| --- | --- | --- |
| Landlord or agency | [ ] | [ ] |
| Out-of-hours repairs | [ ] | [ ] |
| Plumber | [ ] | [ ] |
| Electrician | [ ] | [ ] |
| Building caretaker | [ ] | [ ] |

Emergency services are whatever the number is where this house is - if you are not sure, look it
up now and write it here rather than at the moment you need it.

## Insurance

- **Contents insurance is with:** [ ]
- **Policy number:** [ ]
- **What it covers:** [ ]
""";

    private const string MeterReadings = """
# Meter readings

Where the meters are, and what they said.

- **Electricity meter:** [ where it is ]
- **Gas meter:** [ where it is ]
- **Water meter:** [ where it is ]
- **How to get in there:** [ key, code, which cupboard ]

## Readings

Add a row whenever somebody takes a reading. A photo in the upkeep channel works too - this is for
the number, so a disputed bill has something to argue with.

| Date | Electricity | Gas | Water | Who read it |
| --- | --- | --- | --- | --- |
| [ ] | [ ] | [ ] | [ ] | [ ] |

## Suppliers

| Supply | Supplier | Account number |
| --- | --- | --- |
| Electricity | [ ] | [ ] |
| Gas | [ ] | [ ] |
| Water | [ ] | [ ] |
""";

    private const string HowThisHouseWorks = """
# How this house works

Start here. The rest of the manual is reference; this is the map.

## The chore rota

Lives in the chores channel. It hands out whoever's turn it is by how much everyone has actually
done, not by going round in a circle, so picking up an extra job counts for something.

- **Anything this house does differently:** [ ]

## Money

Shared costs go in the ledger channel: put in what you paid, say who it was for, and settle up when
it suits. Nobody has to keep a mental tally.

- **How we usually settle up:** [ how often, and how ]
- **What counts as a shared cost:** [ ]

## Things we need to agree on

Big questions go in the decisions channel rather than a group chat, so an objection is a thing you
can raise once rather than a thing you have to keep repeating.

## Shopping and the kitchen

The list channel is the shopping list; the pantry channel is what is actually in the cupboard, and
it puts things on the list when they run low.

- **What we share and what is personal:** [ ]

## Guests and quiet

- **Having people over:** [ ]
- **Quiet hours:** [ ]
- **Anything else worth saying out loud:** [ ]
""";
}
