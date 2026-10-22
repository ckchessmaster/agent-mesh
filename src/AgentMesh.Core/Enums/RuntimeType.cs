namespace AgentMesh.Core.Enums;

public enum RuntimeType
{
    McpCloud,           // Gemini Spark, external cloud agents
    LocalOllama,        // Ollama, vLLM self-hosted models
    CliAgy,             // agy / Antigravity terminal harness
    OpenAiCompatible    // Custom OpenAI compatible servers (Ollama, vLLM, LM Studio, etc.)
}
