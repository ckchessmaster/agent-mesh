import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MermaidViewer } from '../MermaidViewer';
import mermaid from 'mermaid';

describe('MermaidViewer', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders diagram title and initializes mermaid render', async () => {
    const code = 'graph LR\nA-->B';
    render(<MermaidViewer code={code} title="Flow Architecture" />);

    expect(screen.getByText('Flow Architecture')).toBeInTheDocument();
    expect(await screen.findByTestId('mermaid-svg')).toBeInTheDocument();
    expect(mermaid.render).toHaveBeenCalledWith(expect.stringMatching(/^mermaid-/), 'graph LR\nA-->B');
  });

  it('allows switching between Diagram and Code modes', async () => {
    const code = 'sequenceDiagram\nAlice->>Bob: Hello';
    render(<MermaidViewer code={code} />);

    // Initially in diagram mode
    expect(await screen.findByTestId('mermaid-svg')).toBeInTheDocument();

    // Switch to code mode
    const codeTab = screen.getByRole('button', { name: /Code/i });
    fireEvent.click(codeTab);

    expect(screen.getByText(/Alice->>Bob: Hello/)).toBeInTheDocument();

    // Switch back to diagram mode
    const diagramTab = screen.getByRole('button', { name: /Diagram/i });
    fireEvent.click(diagramTab);

    expect(await screen.findByTestId('mermaid-svg')).toBeInTheDocument();
  });

  it('handles copy button click and copies code to clipboard', async () => {
    const code = 'graph TD\nNode1-->Node2';
    const writeTextMock = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, {
      clipboard: {
        writeText: writeTextMock
      }
    });

    render(<MermaidViewer code={code} />);

    const copyBtn = screen.getByTitle('Copy diagram code');
    fireEvent.click(copyBtn);

    expect(writeTextMock).toHaveBeenCalledWith(code);
    expect(await screen.findByText('Copied')).toBeInTheDocument();
  });

  it('handles syntax or render error gracefully without crashing', async () => {
    vi.mocked(mermaid.render).mockRejectedValueOnce(new Error('Parse error on line 2'));

    render(<MermaidViewer code="invalid syntax diagram" />);

    await waitFor(() => {
      expect(screen.getByText(/Failed to render Mermaid diagram/i)).toBeInTheDocument();
      expect(screen.getByText(/Parse error on line 2/i)).toBeInTheDocument();
      expect(screen.getByText('invalid syntax diagram')).toBeInTheDocument();
    });
  });
});
