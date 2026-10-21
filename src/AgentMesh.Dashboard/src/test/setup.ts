import '@testing-library/jest-dom/vitest';
import { vi } from 'vitest';

vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockImplementation(async (id: string, code: string) => ({
      svg: `<svg id="${id}" data-testid="mermaid-svg" viewBox="0 0 400 200"><g><text>${code}</text></g></svg>`,
      bindFunctions: vi.fn()
    }))
  }
}));
