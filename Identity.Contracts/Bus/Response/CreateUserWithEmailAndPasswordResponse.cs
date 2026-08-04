using FluentValidation.Results;

namespace Identity.Contracts.Bus.Response;

/// <summary>
/// The registration handler's answer to the controller.
///
/// <para><b>There is no <c>UserId</c> here any more, on purpose.</b> The controller used to return
/// this object as the HTTP body, so the id of the account that had just been created was the
/// success signal - and the only way to keep that signal while refusing to say whether an address is
/// taken would be to invent an id for the taken branch, which is worse than the leak (a client that
/// stores a fabricated id then acts on it). The property is gone rather than merely unread, so no
/// future edit can put it back on the wire by returning this type from an action.</para>
///
/// <para><see cref="Failures"/> is only ever populated for refusals that do <b>not</b> depend on
/// whether the email is registered: a birth date under the age floor, a taken username, a malformed
/// address, or an outright failure to create the account. "That address already has an account" is
/// not among them and must never be added.</para>
/// </summary>
public class CreateUserWithEmailAndPasswordResponse
{
    public ICollection<ValidationFailure> Failures { get; set; } = [];
}
