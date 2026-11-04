import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, setAuthTokenProvider } from '../api';

/**
 * The project-document client: the URLs it hits, the body it sends, and how it normalizes a response.
 *
 * Two behaviours here are the ones a passing type-check cannot prove, and both are the kind that fail
 * silently in production:
 *
 *  - The multipart POST must NOT set a Content-Type header. Setting one by hand overwrites the boundary
 *    the browser generates, and the API then reads an empty form — the user's upload disappears with a
 *    200-shaped request and no error message anywhere.
 *  - The row mapper must accept PascalCase as well as camelCase. A proxied or older API build that leaves
 *    the naming policy unset would otherwise render an empty library, which reads as "nobody attached
 *    anything" rather than as a contract mismatch.
 */
describe('project document API client', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    setAuthTokenProvider(null);
  });

  const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

  describe('getProjectDocuments', () => {
    it('hits the project-scoped collection endpoint', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.getProjectDocuments('agent-mesh');

      expect(fetchSpy).toHaveBeenCalledWith(
        '/api/projects/agent-mesh/documents',
        expect.objectContaining({ headers: expect.objectContaining({ Authorization: expect.any(String) }) })
      );
    });

    it('escapes a project id that contains a slash', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.getProjectDocuments('weird/id');

      expect(fetchSpy.mock.calls[0][0]).toBe('/api/projects/weird%2Fid/documents');
    });

    it('maps a camelCase document', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([
        {
          id: 'abc123',
          projectId: 'agent-mesh',
          title: 'API contract',
          kind: 0,
          fileName: null,
          contentType: null,
          sizeBytes: 29,
          s3Key: null,
          downloadUrl: '/api/projects/agent-mesh/documents/abc123/content',
          forceDownloadUrl: '/api/projects/agent-mesh/documents/abc123/content?download=true',
          createdAt: '2026-11-04T00:00:00Z',
          updatedAt: '2026-11-04T00:00:00Z',
          content: null
        }
      ]));

      const [doc] = await api.getProjectDocuments('agent-mesh');

      expect(doc.id).toBe('abc123');
      expect(doc.title).toBe('API contract');
      expect(doc.kind).toBe(0);
      expect(doc.sizeBytes).toBe(29);
      expect(doc.downloadUrl).toBe('/api/projects/agent-mesh/documents/abc123/content');
    });

    it('maps a PascalCase document from an API build with no naming policy', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([
        {
          Id: 'deadbeef',
          ProjectId: 'agent-mesh',
          Title: 'Architecture diagram',
          Kind: 1,
          FileName: 'arch.png',
          ContentType: 'image/png',
          SizeBytes: 20480,
          S3Key: 'projects/agent-mesh/documents/deadbeef/arch.png',
          DownloadUrl: '/api/projects/agent-mesh/documents/deadbeef/content',
          ForceDownloadUrl: '/api/projects/agent-mesh/documents/deadbeef/content?download=true',
          CreatedAt: '2026-11-04T00:00:00Z',
          UpdatedAt: '2026-11-04T00:00:00Z'
        }
      ]));

      const [doc] = await api.getProjectDocuments('agent-mesh');

      expect(doc.id).toBe('deadbeef');
      expect(doc.title).toBe('Architecture diagram');
      expect(doc.kind).toBe(1);
      expect(doc.fileName).toBe('arch.png');
      expect(doc.sizeBytes).toBe(20480);
      expect(doc.downloadUrl).toBe('/api/projects/agent-mesh/documents/deadbeef/content');
    });

    it('derives a download URL when the response omits one', async () => {
      // Without a URL the row cannot be opened, so a missing field degrades to a derived one rather than
      // to a dead row.
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([
        { id: 'x1', projectId: 'p', title: 'T', kind: 1, sizeBytes: 1 }
      ]));

      const [doc] = await api.getProjectDocuments('p');

      expect(doc.downloadUrl).toBe('/api/projects/p/documents/x1/content');
      expect(doc.forceDownloadUrl).toBe('/api/projects/p/documents/x1/content?download=true');
    });

    it('surfaces a server error message rather than swallowing it', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        json({ error: 'Project \'ghost\' not found.' }, 404)
      );

      await expect(api.getProjectDocuments('ghost')).rejects.toThrow(/Failed to load project documents/);
    });
  });

  describe('addProjectDocument', () => {
    it('sends multipart form data with no manual Content-Type', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.addProjectDocument('agent-mesh', { files: [new File(['hi'], 'a.md', { type: 'text/markdown' })] });

      const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
      expect(url).toBe('/api/projects/agent-mesh/documents');
      expect(init.method).toBe('POST');
      expect(init.body).toBeInstanceOf(FormData);
      // The assertion this file exists for: getHeaders() adds Content-Type: application/json, and
      // spreading it into a multipart request would strip the boundary the browser must add.
      expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined();
    });

    it('appends files, title and content to the same form', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.addProjectDocument('agent-mesh', {
        title: 'Sprint notes',
        content: 'Ship the library.',
        files: [new File(['x'], 'a.png', { type: 'image/png' }), new File(['y'], 'b.pdf', { type: 'application/pdf' })]
      });

      const body = (fetchSpy.mock.calls[0] as [string, RequestInit])[1].body as FormData;
      expect(body.get('title')).toBe('Sprint notes');
      expect(body.get('content')).toBe('Ship the library.');
      expect(body.getAll('files')).toHaveLength(2);
    });

    it('omits empty optional fields so the API does not see a blank title', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.addProjectDocument('agent-mesh', { content: 'body only' });

      const body = (fetchSpy.mock.calls[0] as [string, RequestInit])[1].body as FormData;
      expect(body.get('content')).toBe('body only');
      expect(body.has('title')).toBe(false);
      expect(body.getAll('files')).toHaveLength(0);
    });

    it('posts text with no files at all', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([]));

      await api.addProjectDocument('agent-mesh', { title: 'Notes', content: 'just text' });

      const body = (fetchSpy.mock.calls[0] as [string, RequestInit])[1].body as FormData;
      expect(body.getAll('files')).toHaveLength(0);
      expect(body.get('content')).toBe('just text');
    });

    it('surfaces the API\'s rejection reason verbatim', async () => {
      // The 400s carry the user-facing rule ("exceeds the 25 MB maximum", "already has 50 documents"), so
      // the message is the UI for the limit and must not be replaced by a generic string. The body below
      // is the exact text ProjectsController.CreateProjectDocuments emits for a full library.
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        json({
          error: 'This project can hold 50 documents and already has 50; this request would add 1.'
        }, 400)
      );

      await expect(
        api.addProjectDocument('agent-mesh', { content: 'x' })
      ).rejects.toThrow(/already has 50; this request would add 1/);
    });

    it('falls back to status text when the error body is not JSON', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('boom', { status: 500, statusText: 'Server Error' }));

      await expect(api.addProjectDocument('p', { content: 'x' })).rejects.toThrow(/Server Error/);
    });

    it('maps the created documents the server returns', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(json([
        { id: 'new1', projectId: 'p', title: 'spec.pdf', kind: 1, fileName: 'spec.pdf', sizeBytes: 12 }
      ]));

      const created = await api.addProjectDocument('p', {
        files: [new File(['data'], 'spec.pdf', { type: 'application/pdf' })]
      });

      expect(created).toHaveLength(1);
      expect(created[0].fileName).toBe('spec.pdf');
    });

    it('tolerates a non-array success body', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ status: 'ok' }));

      await expect(api.addProjectDocument('p', { content: 'x' })).resolves.toEqual([]);
    });
  });

  describe('getProjectDocument', () => {
    it('reads one document by id and keeps its body', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({
        id: 'd1', projectId: 'p', title: 'Spec', kind: 0, sizeBytes: 5, content: 'the body'
      }));

      const doc = await api.getProjectDocument('p', 'd1');

      expect(fetchSpy.mock.calls[0][0]).toBe('/api/projects/p/documents/d1');
      expect(doc.content).toBe('the body');
    });

    it('escapes a document id in the path', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ id: 'a/b' }));

      await api.getProjectDocument('p', 'a/b');

      expect(fetchSpy.mock.calls[0][0]).toBe('/api/projects/p/documents/a%2Fb');
    });
  });

  describe('deleteProjectDocument', () => {
    it('issues a DELETE to the document path', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ status: 'deleted', id: 'd1' }));

      const result = await api.deleteProjectDocument('p', 'd1');

      expect(fetchSpy).toHaveBeenCalledWith(
        '/api/projects/p/documents/d1',
        expect.objectContaining({ method: 'DELETE' })
      );
      expect(result.status).toBe('deleted');
    });

    it('surfaces the API rejection when the document belongs to another project', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        json({ error: "Document 'd1' was not found for project 'p'." }, 404)
      );

      await expect(api.deleteProjectDocument('p', 'd1')).rejects.toThrow(/was not found for project/);
    });
  });
});
