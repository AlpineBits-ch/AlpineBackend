namespace Domain;

/// <summary>
/// Who can see one profile field. Applied <b>in the projection</b>: a field the viewer may not see
/// must not be present in the payload at all, not merely hidden by the client.
/// </summary>
public enum Visibility
{
    Everyone,
    Friends,
    Nobody,
}
