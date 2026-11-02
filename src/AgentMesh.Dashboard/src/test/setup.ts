import '@testing-library/jest-dom/vitest';
import { configure } from '@testing-library/react';
import { vi } from 'vitest';

configure({ asyncUtilTimeout: 5000 });

vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockImplementation(async (id: string, code: string) => ({
      svg: `<svg id="${id}" data-testid="mermaid-svg" viewBox="0 0 400 200"><g><text>${code}</text></g></svg>`,
      bindFunctions: vi.fn()
    }))
  }
}));
