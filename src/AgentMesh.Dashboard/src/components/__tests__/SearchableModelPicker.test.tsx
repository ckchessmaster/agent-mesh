import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SearchableModelPicker } from '../SearchableModelPicker';
import type { ModelCapability } from '../../types';

const sampleModels: ModelCapability[] = [
  {
    id: 'openrouter/auto',
    name: 'Auto Router (OpenRouter)',
    efforts: [],
    maxContextTokens: 200_000,
    promptPricePerMillion: 0,
    completionPricePerMillion: 0
  },
  {
    id: 'anthropic/claude-sonnet-4',
    name: 'Claude Sonnet 4',
    efforts: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'],
    maxContextTokens: 200_000,
    promptPricePerMillion: 3,
    completionPricePerMillion: 15
  },
  {
    id: 'google/gemini-2.5-pro',
    name: 'Gemini 2.5 Pro',
    efforts: ['low', 'high'],
    maxContextTokens: 1_000_000,
    promptPricePerMillion: 1.25,
    completionPricePerMillion: 10
  },
  {
    id: 'meta-llama/llama-3.3-70b-instruct:free',
    name: 'Llama 3.3 70B Instruct',
    efforts: [],
    maxContextTokens: 131_072,
    promptPricePerMillion: 0,
    completionPricePerMillion: 0
  }
];

describe('SearchableModelPicker', () => {
  it('filters models, shows context/pricing/reasoning badges, and selects a model', () => {
    const onChange = vi.fn();
    render(
      <SearchableModelPicker
        ariaLabel="New Session Model"
        models={sampleModels}
        value=""
        onChange={onChange}
        defaultOptionLabel="Default Model"
      />
    );

    const trigger = screen.getByRole('button', { name: 'New Session Model' });
    fireEvent.click(trigger);

    const search = screen.getByLabelText('Filter models');
    expect(screen.getByText('$3/$15')).toBeInTheDocument();
    expect(screen.getByText('1M')).toBeInTheDocument();
    expect(screen.getAllByText('free').length).toBeGreaterThan(0);
    expect(screen.getAllByText('reasoning').length).toBeGreaterThan(0);

    fireEvent.change(search, { target: { value: 'gemini' } });
    expect(screen.queryByText('Claude Sonnet 4')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('option', { name: /Gemini 2\.5 Pro/i }));
    expect(onChange).toHaveBeenCalledWith('google/gemini-2.5-pro');
  });

  it('supports clearing to default and entering custom routing slugs', () => {
    const onChange = vi.fn();
    render(
      <SearchableModelPicker
        ariaLabel="New Session Model"
        models={sampleModels}
        value="anthropic/claude-sonnet-4"
        onChange={onChange}
        defaultOptionLabel="Default Model"
        allowCustom
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'New Session Model' }));
    fireEvent.click(screen.getByRole('option', { name: 'Default Model' }));
    expect(onChange).toHaveBeenCalledWith('');

    fireEvent.click(screen.getByRole('button', { name: 'New Session Model' }));
    fireEvent.change(screen.getByLabelText('Filter models'), {
      target: { value: 'anthropic/claude-sonnet-4:nitro' }
    });
    fireEvent.click(screen.getByRole('option', { name: /Use custom model/i }));
    expect(onChange).toHaveBeenCalledWith('anthropic/claude-sonnet-4:nitro');
  });
});
