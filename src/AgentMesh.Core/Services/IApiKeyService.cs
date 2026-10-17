namespace AgentMesh.Core.Services;

public interface IApiKeyService
{
    string GenerateToken(string prefix = "mesh_tok_");
    string HashToken(string token);
    bool VerifyToken(string token, string expectedHash);
}
