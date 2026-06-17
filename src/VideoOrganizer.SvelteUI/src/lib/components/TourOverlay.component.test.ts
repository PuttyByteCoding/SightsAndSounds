import { render, screen, fireEvent } from '@testing-library/svelte';
import { tick } from 'svelte';
import { afterEach, describe, expect, test } from 'vitest';
import TourOverlay from './TourOverlay.svelte';
import { tour, type TourStep } from '$lib/tour.svelte';

// The tour store is a module-level singleton, so make sure each test starts
// (and leaves) it inactive.
afterEach(() => tour.stop());

const STEPS: TourStep[] = [
  { selector: '[data-tour="a"]', title: 'Step one', body: 'First thing.' },
  { selector: '[data-tour="b"]', title: 'Step two', body: 'Second thing.' },
];

describe('TourOverlay', () => {
  test('renders nothing while the tour is inactive', () => {
    render(TourOverlay);
    expect(screen.queryByRole('dialog', { name: 'Guided tour' })).not.toBeInTheDocument();
  });

  test('walks forward through the steps and finishes on Done', async () => {
    render(TourOverlay);

    tour.start(STEPS);
    await tick();

    expect(screen.getByText('Step one')).toBeInTheDocument();
    expect(screen.getByText('1 / 2')).toBeInTheDocument();
    // Back is disabled on the first step.
    expect(screen.getByRole('button', { name: 'Back' })).toBeDisabled();

    await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await tick();

    expect(screen.getByText('Step two')).toBeInTheDocument();
    expect(screen.getByText('2 / 2')).toBeInTheDocument();
    // Last step's primary button reads "Done".
    const done = screen.getByRole('button', { name: 'Done' });
    expect(done).toBeInTheDocument();

    await fireEvent.click(done);
    await tick();

    expect(tour.active).toBe(false);
    expect(screen.queryByText('Step two')).not.toBeInTheDocument();
  });

  test('Skip dismisses the tour immediately', async () => {
    render(TourOverlay);

    tour.start(STEPS);
    await tick();
    expect(screen.getByText('Step one')).toBeInTheDocument();

    await fireEvent.click(screen.getByRole('button', { name: 'Skip' }));
    await tick();

    expect(tour.active).toBe(false);
    expect(screen.queryByText('Step one')).not.toBeInTheDocument();
  });

  test('Back returns to the previous step', async () => {
    render(TourOverlay);
    tour.start(STEPS);
    await tick();

    await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await tick();
    expect(screen.getByText('2 / 2')).toBeInTheDocument();

    await fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    await tick();
    expect(screen.getByText('1 / 2')).toBeInTheDocument();
  });
});
