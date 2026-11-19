import { describe, expect, it } from 'vitest';
import { describeUsageChip, formatUsdCost } from '../driverUsage';
import type { AgentUsageResponse } from '../../types';

describe('driverUsage cost helpers', () => {
  it('formats USD costs accurately and hides zero/missing values', () => {
    expect(formatUsdCost(undefined)).toBeNull();
    expect(formatUsdCost(null)).toBeNull();
    expect(formatUsdCost(0)).toBeNull();
    expect(formatUsdCost(-1)).toBeNull();
    expect(formatUsdCost(0.00421)).toBe('$0.0042');
    expect(formatUsdCost(0.1254)).toBe('$0.125');
    expect(formatUsdCost(1.256)).toBe('$1.26');
  });

  it('appends USD cost to usage chip text only when costUsd > 0', () => {
    const baseUsage: AgentUsageResponse = {
      agentId: 'openrouter',
      agentName: 'OpenRouter',
      sessionCount: 1,
      lifetimeTokens: {
        inputTokens: 1000,
        outputTokens: 500,
        thinkingTokens: 0,
        totalTokens: 1500,
        totalDurationSeconds: 2.0,
        costUsd: 0
      },
      providerQuota: null
    };

    const withoutCost = describeUsageChip(baseUsage);
    expect(withoutCost.text).toBe('quota n/a · 1.5k tokens');

    const withCost = describeUsageChip({
      ...baseUsage,
      lifetimeTokens: { ...baseUsage.lifetimeTokens, costUsd: 0.015 }
    });
    expect(withCost.text).toBe('quota n/a · 1.5k tokens · $0.015');
    expect(withCost.title).toContain('$0.015');
  });
});
