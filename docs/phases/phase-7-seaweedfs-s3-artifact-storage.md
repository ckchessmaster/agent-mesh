# Phase 7: SeaweedFS S3 Artifact Storage & Large Payload Offload

## 1. Phase Objective & Scope

The objective of Phase 7 is to integrate high-performance S3-compatible storage (SeaweedFS) into AgentMesh to handle large agent payloads, execution outputs, and log bundles without straining PostgreSQL JSONB storage or violating Temporal gRPC payload limits (2–4 MB).

By the end of this phase:
1. `IStorageService` abstraction and `SeaweedS3StorageService` implementation (`AWSSDK.S3`) supporting path-style AWS SigV4 authentication.
2. Hybrid size-based offloading strategy:
   * Payloads `< 64 KB` remain inline in PostgreSQL JSONB for maximum query speed.
   * Payloads `> 64 KB` are transparently uploaded to SeaweedFS S3 (`s3://agent-mesh-artifacts/jobs/{jobId}/payload.json`), with S3 URIs recorded in PostgreSQL.
3. Database schema migration adding `payload_s3_uri`, `result_s3_uri`, and `artifacts JSONB` to `agent_jobs`.
4. Staging MCP tools and Temporal worker activities updated with offload lifecycle hooks.
5. API artifact gateway endpoint (`GET /api/jobs/{id}/artifacts/{name}`) with presigned URL support.
6. React 19 Cockpit UI updated with artifact indicators and download links.
7. Local Aspire AppHost container integration and Helm chart production configuration.
8. Automated unit & integration tests covering the entire storage pipeline.

---

## 2. Architecture & Data Flow

```text
Agent / MCP Client
      │
      │ 1. stage_agent_intent (Payload > 64 KB)
      ▼
AgentMesh.Api (StagingTools)
      │
      ├─── 2. Uploads raw payload ───► SeaweedFS S3 (:8333)
      │                                 (s3://agent-mesh-artifacts/jobs/{id}/payload.json)
      │
      ├─── 3. Dispatches lean workflow with S3 URI ───► Temporal Cluster (:7233)
      │
      └─── 4. Records metadata + S3 URI ───► PostgreSQL (:5432)
                                                (agent_jobs.payload_s3_uri)
```

---

## 3. Storage Configuration Schema

```json
{
  "Storage": {
    "S3": {
      "ServiceUrl": "http://localhost:8333",
      "BucketName": "agent-mesh-artifacts",
      "AccessKey": "agentmesh-storage-key",
      "SecretKey": "AgentMesh#SecureS3Pass2026!",
      "ForcePathStyle": true,
      "ThresholdBytes": 65536
    }
  }
}
```

---

## 4. SeaweedFS Security & Bucket Isolation Policy

In production, SeaweedFS enforces AWS SigV4 authentication with strict bucket isolation in `/etc/seaweedfs/s3.json`:
* `cnpg-admin`: Restricted exclusively to `postgres-wal` (CloudNativePG backups).
* `agentmesh`: Restricted exclusively to `agent-mesh-artifacts` (Agent task payloads & outputs).
