using System.Security.Cryptography;

namespace Rochell.Audit;

/// <summary>
/// E-PR15-4: ECDSA P-256 / SHA-256 signature of the digest document. The private key is only available to the sealing service
/// (secret store); verification needs only the public key.
/// </summary>
public sealed class DigestSigner(ECDsa key)
{
    public byte[] Sign(byte[] document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return key.SignData(document, HashAlgorithmName.SHA256);
    }

    public bool Verify(byte[] document, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        return key.VerifyData(document, signature, HashAlgorithmName.SHA256);
    }
}
