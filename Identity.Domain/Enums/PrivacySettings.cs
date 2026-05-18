namespace Identity.Domain.Enums;

[Flags]
public enum PrivacySettings : ulong
{
    None = 0,
    AllowDataCollection = 1 << 0,
    AllowVoiceRecordedInClips = 1 << 1,
    AllowDataUseForPersonalization = 1 << 2
}