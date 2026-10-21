import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MarkdownMessage } from '../MarkdownMessage';

describe('MarkdownMessage', () => {
  it('renders inline formatting correctly (bold, italic, code, links)', () => {
    const content = 'Hello **bold text**, *italic*, `inline_code`, and [Google](https://google.com)!';
    render(<MarkdownMessage content={content} />);

    expect(screen.getByText('bold text')).toBeInTheDocument();
    expect(screen.getByText('italic')).toBeInTheDocument();
    expect(screen.getByText('inline_code')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'Google' });
    expect(link).toHaveAttribute('href', 'https://google.com');
  });

  it('renders headings cleanly', () => {
    const content = '# Heading 1\n## Heading 2\n### Heading 3';
    render(<MarkdownMessage content={content} />);

    expect(screen.getByRole('heading', { level: 1, name: 'Heading 1' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Heading 2' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 3, name: 'Heading 3' })).toBeInTheDocument();
  });

  it('renders bullet lists and numbered lists with formatting', () => {
    const content = `### Summary
* **Item One:** Description of one
* **Item Two:** Description of two
1. Step first
2. Step second`;
    render(<MarkdownMessage content={content} />);

    expect(screen.getByText('Item One:')).toBeInTheDocument();
    expect(screen.getByText('Item Two:')).toBeInTheDocument();
    expect(screen.getByText('Step first')).toBeInTheDocument();
    expect(screen.getByText('Step second')).toBeInTheDocument();
    expect(screen.getByText('1.')).toBeInTheDocument();
    expect(screen.getByText('2.')).toBeInTheDocument();
  });

  it('renders code blocks with language indicator and copy button', () => {
    const content = '```typescript\nconst x: number = 42;\nconsole.log(x);\n```';
    render(<MarkdownMessage content={content} />);

    expect(screen.getByText('TYPESCRIPT')).toBeInTheDocument();
    expect(screen.getByText(/const x: number = 42;/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /copy/i })).toBeInTheDocument();
  });

  it('renders GitHub alerts (NOTE, WARNING)', () => {
    const content = '> [!WARNING]\n> This is a high-risk operation!';
    render(<MarkdownMessage content={content} />);

    expect(screen.getByText('WARNING')).toBeInTheDocument();
    expect(screen.getByText('This is a high-risk operation!')).toBeInTheDocument();
  });

  it('renders mermaid diagrams visually with diagram and code toggle options', async () => {
    const content = '```mermaid\ngraph TD\nClient-->API\n```';
    render(<MarkdownMessage content={content} />);

    expect(screen.getByText('Mermaid Architecture Diagram')).toBeInTheDocument();
    expect(await screen.findByTestId('mermaid-viewer')).toBeInTheDocument();
    expect(await screen.findByTestId('mermaid-svg')).toBeInTheDocument();
  });
});
