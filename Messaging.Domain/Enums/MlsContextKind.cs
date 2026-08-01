namespace Messaging.Domain.Enums;

/// <summary>
/// Which kind of context an MLS group encrypts.
///
/// <para>Everything below the endpoints is deliberately context-agnostic, so this exists only where
/// the two genuinely differ. Today that is the admission threshold: a conversation has two humans in
/// it and requiring two approvals deadlocks it, while a channel can afford - and wants - a second
/// opinion. Passed in rather than inferred from the actor count, because inferring made the answer
/// depend on how much traffic the group happened to have seen.</para>
/// </summary>
public enum MlsContextKind
{
    Conversation,
    Channel,
}
