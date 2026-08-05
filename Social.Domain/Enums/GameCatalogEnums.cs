namespace Social.Domain.Enums;

/// <summary>Where a catalog row came from.</summary>
public enum GameCatalogSource
{
    /// <summary>Came from the one-time bootstrap artifact. Safe for the seeder to overwrite.</summary>
    Seeded,

    /// <summary>Submitted by a user and accepted. The seeder must never touch these.</summary>
    Community,

    /// <summary>Entered or corrected by staff.</summary>
    Manual,

    /// <summary>
    /// Learned on demand: an application id arrived over the RPC socket that the bootstrap did not
    /// contain, and its display name was resolved once from the public application endpoint and
    /// kept.
    /// </summary>
    Resolved,
}

/// <summary>The platform an executable rule applies to.</summary>
public enum GamePlatform
{
    Win32,
    Darwin,
    Linux,
}
