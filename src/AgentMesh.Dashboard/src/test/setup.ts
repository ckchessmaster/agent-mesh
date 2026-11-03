import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi, afterEach } from 'vitest';

configure({ asyncUtilTimeout: 5000 });

afterEach(() => {
  if (typeof localStorage !== 'undefined') {
    localStorage.clear();
  }
  if (typeof sessionStorage !== 'undefined') {
    sessionStorage.clear();
  }
});

vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockImplementation(async (id: string, code: string) => ({
      svg: `<svg id="${id}" data-testid="mermaid-svg" viewBox="0 0 400 200"><g><text>${code}</text></g></svg>`,
      bindFunctions: vi.fn()
    }))
  }
}));
