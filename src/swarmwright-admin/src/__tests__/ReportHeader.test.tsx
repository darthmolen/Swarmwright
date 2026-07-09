import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ReportHeader } from '../components/ReportHeader';

describe('ReportHeader', () => {
  const baseProps = {
    swarmId: '614d5ec0-d7fd-43cc-ba85-e96f8c02db9e',
    onBack: vi.fn(),
    onCopy: vi.fn(),
    onRefresh: vi.fn(),
  };

  it('renders the short swarm id in the title', () => {
    render(<ReportHeader {...baseProps} />);

    expect(screen.getByRole('heading')).toHaveTextContent('614d5ec0');
  });

  it('renders a Refresh button that triggers onRefresh when clicked', () => {
    const onRefresh = vi.fn();
    render(<ReportHeader {...baseProps} onRefresh={onRefresh} />);

    fireEvent.click(screen.getByRole('button', { name: /refresh/i }));

    expect(onRefresh).toHaveBeenCalledOnce();
  });

  it('renders a Copy button that triggers onCopy when clicked', () => {
    const onCopy = vi.fn();
    render(<ReportHeader {...baseProps} onCopy={onCopy} />);

    fireEvent.click(screen.getByRole('button', { name: /copy/i }));

    expect(onCopy).toHaveBeenCalledOnce();
  });

  it('renders a Dashboard back button that triggers onBack when clicked', () => {
    const onBack = vi.fn();
    render(<ReportHeader {...baseProps} onBack={onBack} />);

    fireEvent.click(screen.getByRole('button', { name: /dashboard/i }));

    expect(onBack).toHaveBeenCalledOnce();
  });

  it('renders Refresh button LEFT of the Copy button', () => {
    render(<ReportHeader {...baseProps} />);

    const buttons = screen.getAllByRole('button');
    const refreshIdx = buttons.findIndex((b) => /refresh/i.test(b.textContent ?? ''));
    const copyIdx = buttons.findIndex((b) => /^copy$/i.test(b.textContent ?? ''));

    expect(refreshIdx).toBeGreaterThanOrEqual(0);
    expect(copyIdx).toBeGreaterThanOrEqual(0);
    expect(refreshIdx).toBeLessThan(copyIdx);
  });
});
