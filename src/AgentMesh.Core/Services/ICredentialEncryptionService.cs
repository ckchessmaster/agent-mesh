namespace AgentMesh.Core.Services;

public interface ICredentialEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    string Mask(string token);
}
