namespace Social.Domain.Enums;

// Curated set of client-rendered font families for profile customization — deliberately not a
// free-text/upload field, so we never have to host or license arbitrary font files.
public enum ProfileFont
{
    Default,
    Serif,
    Monospace,
    Rounded,
    Display,
    Handwritten,
}
