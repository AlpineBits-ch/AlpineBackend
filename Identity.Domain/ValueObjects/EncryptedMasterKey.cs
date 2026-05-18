namespace Identity.Domain.Entities;

public class EncryptedMasterKey
{
    public byte[] CipherText { get; init; } = null!; 
    public byte[] Salt { get; init; } = null!; 
    public byte[] Iv { get; init; } = null!;  
    public int Argon2Iterations { get; init; }
    public int Argon2Memory { get; init; }
    public int Argon2Parallelism { get; init; }
    public int Version { get; init; } = 1; 
}